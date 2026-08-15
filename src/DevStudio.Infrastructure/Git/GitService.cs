using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Repositories;
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
    private readonly ISourceControlHosts _hosts;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<GitService> _logger;
    private readonly IReadOnlyList<string> _configuredRoots;

    private static readonly TimeSpan RootCacheWindow = TimeSpan.FromSeconds(10);
    private IReadOnlyList<string> _cachedRoots = [];
    private DateTimeOffset _rootsCachedAt;

    public GitService(
        IProcessRunner runner,
        ISourceControlRegistry forges,
        ISourceControlHosts hosts,
        IEntityStore<GitRepository> repositories,
        IOptions<OrchestratorOptions> options,
        ILogger<GitService> logger)
    {
        _runner = runner;
        _forges = forges;
        _hosts = hosts;
        _repositories = repositories;
        _options = options.Value;
        _logger = logger;
        _configuredRoots = LocalRepositoryPaths.NormaliseRoots(_options.LocalRepositoryRoots);
    }

    public IReadOnlyList<string> LocalRepositoryRoots => ResolveRoots();

    /// <summary>
    /// The allow-list the picker works within: the configured mounts, plus every drive on the machine
    /// when the deployment has said that is fine. Drives are enumerated rather than configured because
    /// they change while the app runs, so the answer is held for a few seconds instead of for the
    /// lifetime of the service — the UI reads this on every render.
    /// </summary>
    private IReadOnlyList<string> ResolveRoots()
    {
        if (!_options.AllowAllLocalDrives)
            return _configuredRoots;

        if (_cachedRoots.Count > 0 && DateTimeOffset.UtcNow - _rootsCachedAt < RootCacheWindow)
            return _cachedRoots;

        // Configured roots come first so the picker opens on the folder somebody chose to name,
        // rather than at the top of a drive.
        _cachedRoots = LocalRepositoryPaths.NormaliseRoots([.. _configuredRoots, .. LocalRepositoryPaths.DriveRoots()]);
        _rootsCachedAt = DateTimeOffset.UtcNow;
        return _cachedRoots;
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

    public async Task<LocalBrowseResult> BrowseLocalAsync(string? path, CancellationToken ct = default)
    {
        var registered = (await _repositories.GetAllAsync(ct)).Select(r => r.LocalPath).ToList();
        bool IsRegistered(string candidate) =>
            registered.Any(p => string.Equals(p, candidate, LocalRepositoryPaths.Comparison));

        // Read once: the drive list behind it is recomputed periodically, and a listing that used one
        // set of roots to resolve a path and another to decide what is above it would be incoherent.
        var allowed = LocalRepositoryRoots;

        // The top level is the roots themselves — the configured mounts and, where the deployment
        // allows it, each drive on the machine.
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = allowed
                .Where(Directory.Exists)
                .Select(r => new LocalDirectoryEntry(r, r, LocalRepositoryPaths.IsGitRepository(r), IsRegistered(r)))
                .ToList();

            return new LocalBrowseResult(null, null, roots);
        }

        if (!LocalRepositoryPaths.TryResolveWithinRoots(path, allowed, out var resolved))
            throw new InvalidOperationException("That path is not inside a folder devStudio is allowed to browse.");

        if (!Directory.Exists(resolved))
            throw new InvalidOperationException($"'{resolved}' does not exist. Has the folder moved, or the mount gone?");

        var children = new List<LocalDirectoryEntry>();

        try
        {
            // A drive root holds folders the user has no business in — System Volume Information,
            // $Recycle.Bin — and one unreadable entry used to fail the whole listing.
            var enumeration = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
                RecurseSubdirectories = false,
            };

            foreach (var child in Directory.EnumerateDirectories(resolved, "*", enumeration)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);

                // Dot directories are .git and the worktrees folder — noise in a repository picker.
                if (name.StartsWith('.'))
                    continue;

                children.Add(new LocalDirectoryEntry(name, child, LocalRepositoryPaths.IsGitRepository(child), IsRegistered(child)));

                // A mount with thousands of folders would otherwise render as thousands of rows.
                if (children.Count >= 500)
                    break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"'{resolved}' is not readable by the user devStudio runs as.");
        }

        // Up is offered only as far as the allow-list reaches, which is what stops the picker walking
        // out of it. A configured mount nested inside a browsable drive can still be stepped out of,
        // because its parent is allowed in its own right.
        var parent = LocalRepositoryPaths.TryResolveWithinRoots(Path.GetDirectoryName(resolved), allowed, out var above)
            ? above
            : null;

        return new LocalBrowseResult(resolved, parent, children);
    }

    public async Task<GitRepository> AttachLocalAsync(string path, string? name, CancellationToken ct = default)
    {
        if (LocalRepositoryRoots.Count == 0)
            throw new InvalidOperationException("No local folders are browsable, so nothing can be attached.");

        if (!LocalRepositoryPaths.TryResolveWithinRoots(path, LocalRepositoryRoots, out var resolved))
            throw new InvalidOperationException("That path is not inside a folder devStudio is allowed to attach from.");

        if (!Directory.Exists(resolved))
            throw new InvalidOperationException($"'{resolved}' does not exist.");

        if (!LocalRepositoryPaths.IsGitRepository(resolved))
            throw new InvalidOperationException($"'{resolved}' is not a git repository — no .git there.");

        var all = await _repositories.GetAllAsync(ct);
        if (all.FirstOrDefault(r => string.Equals(r.LocalPath, resolved, LocalRepositoryPaths.Comparison)) is { } already)
            return already;

        var remote = await RunGitAsync(resolved, ["remote", "get-url", "origin"], 60, ct);
        var remoteUrl = remote.Succeeded ? remote.Output.Trim() : string.Empty;

        var repository = new GitRepository
        {
            Name = UniqueName(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(resolved) : name, all),
            RemoteUrl = remoteUrl,
            SourceControl = DetectForge(remoteUrl),
            LocalPath = resolved,
            DefaultBranch = await DetectLocalDefaultBranchAsync(resolved, ct),
            IsLocal = true,
        };

        _logger.LogInformation("Attached host-mounted repository {Path} as {Name}", resolved, repository.Name);
        return await _repositories.UpsertAsync(repository, ct);
    }

    public async Task<GitRepository> RenameAsync(GitRepository repository, string? name, CancellationToken ct = default)
    {
        var all = await _repositories.GetAllAsync(ct);
        var others = all.Where(r => r.Id != repository.Id).ToList();

        // Blank means "use the folder the checkout actually lives in", which is what attaching does too.
        var requested = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(repository.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : name;
        var unique = UniqueName(requested, others);
        if (string.Equals(unique, repository.Name, StringComparison.Ordinal))
            return repository;

        _logger.LogInformation("Renamed repository {Old} to {New}", repository.Name, unique);
        repository.Name = unique;
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
        // A host-mounted repo puts its worktrees on the mount, so the IDE editing that checkout can
        // see what the agent is doing; a volume clone keeps using the shared worktrees directory.
        var worktreeRoot = LocalRepositoryPaths.WorktreeRoot(
            repository.IsLocal,
            repository.LocalPath,
            _options.WorktreesPath,
            _options.LocalWorktreesFolderName);

        Directory.CreateDirectory(worktreeRoot);
        var path = Path.Combine(worktreeRoot, $"{repository.Name}-{TemplateRenderer.Slugify(branch)}");

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

    /// <summary>
    /// Prefers the remote's idea of the default branch. Whatever is checked out right now is the
    /// wrong answer for a mounted repo, because that is the branch the person at the IDE is on.
    /// </summary>
    private async Task<string> DetectLocalDefaultBranchAsync(string path, CancellationToken ct)
    {
        var head = await RunGitAsync(path, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], 60, ct);

        // The output is "origin/main"; the branch name is what is wanted.
        if (head.Succeeded && head.Output.Trim() is { Length: > 0 } reference)
            return reference.StartsWith("origin/", StringComparison.Ordinal) ? reference["origin/".Length..] : reference;

        return await DetectDefaultBranchAsync(path, ct);
    }

    /// <summary>Picks the forge from the remote's host, so a self-managed instance resolves too.</summary>
    private SourceControlProvider DetectForge(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return SourceControlProvider.GitHub;

        var host = RemoteHost(remoteUrl);

        foreach (var provider in Enum.GetValues<SourceControlProvider>())
        {
            if (host.Equals(_hosts.Get(provider), StringComparison.OrdinalIgnoreCase))
                return provider;
        }

        // An unconfigured host is still more likely to be the one whose name is in the URL.
        return host.Contains("gitlab", StringComparison.OrdinalIgnoreCase)
            ? SourceControlProvider.GitLab
            : SourceControlProvider.GitHub;
    }

    /// <summary>Handles both URL remotes and the scp-like <c>git@host:owner/repo.git</c> form.</summary>
    private static string RemoteHost(string remoteUrl)
    {
        var trimmed = remoteUrl.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        var at = trimmed.IndexOf('@');
        var colon = trimmed.IndexOf(':', at + 1);
        return at >= 0 && colon > at ? trimmed[(at + 1)..colon] : string.Empty;
    }

    /// <summary>Names end up in worktree directory names, so two repositories cannot share one.</summary>
    private static string UniqueName(string requested, IReadOnlyList<GitRepository> existing)
    {
        var slug = TemplateRenderer.Slugify(requested);
        if (string.IsNullOrWhiteSpace(slug))
            slug = "repo";

        var candidate = slug;
        var suffix = 2;

        while (existing.Any(r => string.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{slug}-{suffix++}";

        return candidate;
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
