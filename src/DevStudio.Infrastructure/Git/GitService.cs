using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Git;

/// <summary>
/// Clones repositories into the persistent volume and cuts a worktree per session, which is what
/// keeps concurrent agents from tripping over each other in the same checkout.
/// </summary>
public sealed class GitService : IGitService
{
    private readonly IProcessRunner _runner;
    private readonly ISourceControlRegistry _forges;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<GitService> _logger;

    public GitService(
        IProcessRunner runner,
        ISourceControlRegistry forges,
        IEntityStore<GitRepository> repositories,
        IOptions<OrchestratorOptions> options,
        ILogger<GitService> logger)
    {
        _runner = runner;
        _forges = forges;
        _repositories = repositories;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GitRepository> CloneAsync(
        string remoteUrl,
        string? name,
        SourceControlProvider sourceControl = SourceControlProvider.GitHub,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("A remote URL is required.", nameof(remoteUrl));

        var folder = string.IsNullOrWhiteSpace(name) ? DeriveName(remoteUrl) : TemplateRenderer.Slugify(name);
        Directory.CreateDirectory(_options.RepositoriesPath);
        var target = Path.Combine(_options.RepositoriesPath, folder);

        var existing = (await _repositories.GetAllAsync(ct))
            .FirstOrDefault(r => string.Equals(r.LocalPath, target, StringComparison.OrdinalIgnoreCase));

        if (Directory.Exists(target) && existing is not null)
        {
            await FetchAsync(existing, ct);
            return existing;
        }

        if (Directory.Exists(target))
            throw new InvalidOperationException($"'{folder}' already exists on disk but is not registered. Rename it or pick another name.");

        // Terminal prompts are off, so git has to already know how to authenticate. Wiring the
        // forge CLI in as the credential helper here means a private clone works off the login
        // whether or not the login flow got round to setting it up.
        var credentials = await _forges.Get(sourceControl).ConfigureGitCredentialsAsync(ct);
        if (!credentials.Succeeded)
            _logger.LogWarning("Could not configure git credentials for {Forge}: {Output}", sourceControl, credentials.Output);

        var clone = await RunGitAsync(_options.RepositoriesPath, ["clone", remoteUrl, folder], 600, ct);
        if (!clone.Succeeded)
            throw new InvalidOperationException($"Clone failed: {clone.Output}");

        var defaultBranch = await DetectDefaultBranchAsync(target, ct);

        var repository = new GitRepository
        {
            Name = folder,
            RemoteUrl = remoteUrl,
            SourceControl = sourceControl,
            LocalPath = target,
            DefaultBranch = defaultBranch,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        return await _repositories.UpsertAsync(repository, ct);
    }

    public async Task<GitCommandOutcome> FetchAsync(GitRepository repository, CancellationToken ct = default)
    {
        var result = await RunGitAsync(repository.LocalPath, ["fetch", "--all", "--prune"], 300, ct);
        repository.LastFetchedAt = DateTimeOffset.UtcNow;
        repository.LastError = result.Succeeded ? null : result.Output;
        await _repositories.UpsertAsync(repository, ct);
        return result;
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(GitRepository repository, CancellationToken ct = default)
    {
        var result = await RunGitAsync(
            repository.LocalPath,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes/origin"],
            60,
            ct);

        if (!result.Succeeded)
            return [];

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => b.StartsWith("origin/", StringComparison.Ordinal) ? b["origin/".Length..] : b)
            .Where(b => b != "HEAD")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> GetStatusAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunGitAsync(workingDirectory, ["status", "--short", "--branch"], 60, ct);
        return result.Output;
    }

    public async Task<Worktree> CreateWorktreeAsync(
        GitRepository repository,
        string branch,
        string? baseBranch,
        bool ephemeral,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.WorktreesPath);
        var path = Path.Combine(_options.WorktreesPath, $"{repository.Name}-{TemplateRenderer.Slugify(branch)}");

        if (Directory.Exists(path))
        {
            var known = repository.Worktrees.FirstOrDefault(w => w.Path == path);
            if (known is not null)
                return known;

            path += "-" + Guid.NewGuid().ToString("n")[..6];
        }

        var start = string.IsNullOrWhiteSpace(baseBranch) ? repository.DefaultBranch : baseBranch!;

        // -B resets the branch if it already exists, so a re-run never fails on leftovers.
        var result = await RunGitAsync(repository.LocalPath, ["worktree", "add", "-B", branch, path, start], 300, ct);
        if (!result.Succeeded)
        {
            // Falling back to the local HEAD covers a repo whose default branch name differs.
            result = await RunGitAsync(repository.LocalPath, ["worktree", "add", "-B", branch, path], 300, ct);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Could not create worktree: {result.Output}");
        }

        var worktree = new Worktree
        {
            Branch = branch,
            Path = path,
            IsEphemeral = ephemeral,
        };

        repository.Worktrees.Add(worktree);
        await _repositories.UpsertAsync(repository, ct);
        return worktree;
    }

    public async Task<GitCommandOutcome> RemoveWorktreeAsync(
        GitRepository repository,
        Worktree worktree,
        CancellationToken ct = default)
    {
        var result = await RunGitAsync(repository.LocalPath, ["worktree", "remove", worktree.Path, "--force"], 120, ct);

        repository.Worktrees.RemoveAll(w => w.Id == worktree.Id);
        await _repositories.UpsertAsync(repository, ct);

        if (!result.Succeeded)
            _logger.LogWarning("git worktree remove reported: {Output}", result.Output);

        return result;
    }

    public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
        RunGitAsync(workingDirectory, arguments, 300, ct);

    private async Task<GitCommandOutcome> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            new ProcessRequest(
                _options.GitExecutable,
                arguments,
                workingDirectory,
                new Dictionary<string, string>
                {
                    ["HOME"] = _options.HomePath,
                    // Never let git block a background operation waiting for a password.
                    ["GIT_TERMINAL_PROMPT"] = "0",
                },
                TimeoutSeconds: timeoutSeconds),
            ct);

        return new GitCommandOutcome(result.Succeeded, result.Text);
    }

    private async Task<string> DetectDefaultBranchAsync(string path, CancellationToken ct)
    {
        var result = await RunGitAsync(path, ["rev-parse", "--abbrev-ref", "HEAD"], 60, ct);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : "main";
    }

    private static string DeriveName(string remoteUrl)
    {
        var trimmed = remoteUrl.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return TemplateRenderer.Slugify(name);
    }
}
