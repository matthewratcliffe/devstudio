using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Skills;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// A host-mounted checkout is open in someone's editor while an agent works in it. The files this
/// app stages into a workspace must not show up there as untracked work.
/// </summary>
public class LocalWorkspaceExcludeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-exclude-" + Guid.NewGuid().ToString("n"));
    private readonly string _checkout;
    private readonly string _gitDir;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly WorkspaceService _service;

    public LocalWorkspaceExcludeTests()
    {
        _checkout = Path.Combine(_root, "checkout");
        _gitDir = Path.Combine(_checkout, ".git");
        Directory.CreateDirectory(_gitDir);

        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });
        _repositories = Store<GitRepository>(options);

        _service = new WorkspaceService(
            new StubGit(_gitDir),
            _repositories,
            Store<Skill>(options),
            Store<McpServer>(options),
            new StubTokens(),
            Store<Project>(options),
            Store<GlobalSettings>(options),
            options,
            NullLogger<WorkspaceService>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    private async Task<Agent> AgentForAsync(bool isLocal)
    {
        var repository = await _repositories.UpsertAsync(new GitRepository
        {
            Name = "checkout",
            LocalPath = _checkout,
            IsLocal = isLocal,
        });

        return new Agent { RepositoryId = repository.Id, UseWorktree = false };
    }

    [Fact]
    public async Task Staged_files_are_excluded_in_a_mounted_checkout()
    {
        await _service.PrepareAsync(await AgentForAsync(isLocal: true), "session01", null);

        var exclude = await File.ReadAllTextAsync(Path.Combine(_gitDir, "info", "exclude"));

        Assert.Contains("/global-files/", exclude);
        Assert.Contains("/project-files/", exclude);
        Assert.Contains("/GUIDANCE.md", exclude);
        Assert.Contains("/.mcp.json", exclude);
        Assert.Contains("/.claude/skills/", exclude);
    }

    [Fact]
    public async Task Existing_exclude_rules_survive_and_nothing_is_written_twice()
    {
        var infoPath = Path.Combine(_gitDir, "info");
        Directory.CreateDirectory(infoPath);
        await File.WriteAllLinesAsync(Path.Combine(infoPath, "exclude"), ["# mine", "/scratch.txt"]);

        var agent = await AgentForAsync(isLocal: true);
        await _service.PrepareAsync(agent, "session01", null);
        await _service.PrepareAsync(agent, "session02", null);

        var lines = await File.ReadAllLinesAsync(Path.Combine(infoPath, "exclude"));

        Assert.Contains("/scratch.txt", lines);
        Assert.Single(lines, l => l == "/global-files/");
    }

    [Fact]
    public async Task A_volume_clone_is_left_alone()
    {
        await _service.PrepareAsync(await AgentForAsync(isLocal: false), "session01", null);

        Assert.False(File.Exists(Path.Combine(_gitDir, "info", "exclude")));
    }

    /// <summary>Answers the one question the exclude logic asks git, and nothing else.</summary>
    private sealed class StubGit(string gitDir) : IGitService
    {
        public IReadOnlyList<string> LocalRepositoryRoots => [];

        public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            Task.FromResult(arguments.Contains("--absolute-git-dir")
                ? new GitCommandOutcome(true, gitDir)
                : new GitCommandOutcome(true, string.Empty));

        public Task<LocalBrowseResult> BrowseLocalAsync(string? path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> AttachLocalAsync(string path, string? name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> RenameAsync(GitRepository repository, string? name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> CloneAsync(string remoteUrl, string? name, SourceControlProvider sourceControl = SourceControlProvider.GitHub, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitCommandOutcome> FetchAsync(GitRepository repository, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBranchesAsync(GitRepository repository, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> GetStatusAsync(string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<Worktree> CreateWorktreeAsync(GitRepository repository, string branch, string? baseBranch, bool ephemeral, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitCommandOutcome> RemoveWorktreeAsync(GitRepository repository, Worktree worktree, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
