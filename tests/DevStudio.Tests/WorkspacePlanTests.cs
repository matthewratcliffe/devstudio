using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
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
/// Planning a workspace is separate from building one so that the building can happen on another
/// machine. Everything the local project contributes has to be resolved here and carried across —
/// over there the project does not exist.
/// </summary>
public sealed class WorkspacePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-plan-" + Guid.NewGuid().ToString("n"));
    private readonly WorkspaceService _workspaces;
    private readonly JsonEntityStore<Project> _projects;

    public WorkspacePlanTests()
    {
        var options = Options.Create(new OrchestratorOptions
        {
            DataPath = _root,
            ScratchPath = Path.Combine(_root, "scratch"),
        });

        _projects = new JsonEntityStore<Project>(options, NullLogger<JsonEntityStore<Project>>.Instance);

        _workspaces = new WorkspaceService(
            new StubGit(),
            new JsonEntityStore<GitRepository>(options, NullLogger<JsonEntityStore<GitRepository>>.Instance),
            new JsonEntityStore<Skill>(options, NullLogger<JsonEntityStore<Skill>>.Instance),
            new JsonEntityStore<McpServer>(options, NullLogger<JsonEntityStore<McpServer>>.Instance),
            new StubMcpTokens(),
            _projects,
            new JsonEntityStore<GlobalSettings>(options, NullLogger<JsonEntityStore<GlobalSettings>>.Instance),
            new StubStandardsFilesSyncService(),
            options,
            NullLogger<WorkspaceService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The repository and base branch are chosen here, not over there. A remote host has no project
    /// to read them from, and guessing would land the agent in the wrong checkout.
    /// </summary>
    [Fact]
    public async Task A_plan_resolves_the_project_contribution_before_it_travels()
    {
        var project = await _projects.UpsertAsync(new Project
        {
            Name = "site",
            RepositoryId = "repo-1",
            BaseBranch = "develop",
        });

        var agent = new Agent { Name = "worker", ProjectId = project.Id };

        var plan = await _workspaces.PlanAsync(agent, "session-abcdef12", null, null);

        Assert.Equal("repo-1", plan.RepositoryId);
        Assert.Equal("develop", plan.BaseBranch);
        Assert.Equal(project.Id, plan.ProjectId);
    }

    [Fact]
    public async Task The_agents_own_repository_wins_over_its_projects()
    {
        var project = await _projects.UpsertAsync(new Project { Name = "site", RepositoryId = "repo-1" });
        var agent = new Agent { Name = "worker", ProjectId = project.Id, RepositoryId = "repo-2" };

        var plan = await _workspaces.PlanAsync(agent, "session-abcdef12", null, null);

        Assert.Equal("repo-2", plan.RepositoryId);
    }

    /// <summary>
    /// Uploaded project files travel with their bytes. Over there the folder they normally come from
    /// does not exist, so a path would stage nothing and the agent would silently lose its reference
    /// material.
    /// </summary>
    [Fact]
    public async Task Project_files_travel_with_their_contents()
    {
        var project = await _projects.UpsertAsync(new Project { Name = "site" });

        var filesPath = Path.Combine(_root, "projects", project.Id, "files");
        Directory.CreateDirectory(filesPath);
        await File.WriteAllTextAsync(Path.Combine(filesPath, "brief.md"), "the brief");

        var plan = await _workspaces.PlanAsync(new Agent { Name = "worker" }, "session-abcdef12", project.Id, null);

        var carried = Assert.Single(plan.ProjectFiles);
        Assert.Equal("brief.md", carried.FileName);
        Assert.Equal("the brief", System.Text.Encoding.UTF8.GetString(carried.Content));
    }

    /// <summary>Building from a plan is what a remote host does, so it has to land the files.</summary>
    [Fact]
    public async Task Building_from_a_plan_writes_the_files_it_was_given()
    {
        var plan = new WorkspacePlan
        {
            Agent = new Agent { Name = "worker" },
            SessionId = "session-abcdef12",
            ProjectFiles = [new SuppliedFile("brief.md", "the brief"u8.ToArray())],
        };

        var workspace = await _workspaces.PrepareAsync(plan);

        var staged = Path.Combine(workspace.Path, "project-files", "brief.md");
        Assert.True(File.Exists(staged));
        Assert.Equal("the brief", await File.ReadAllTextAsync(staged));
    }

    /// <summary>
    /// A file an agent has already changed beside a reference copy should not be clobbered on every
    /// turn — the same rule the local copy has always followed.
    /// </summary>
    [Fact]
    public async Task An_unchanged_file_is_left_alone()
    {
        var plan = new WorkspacePlan
        {
            Agent = new Agent { Name = "worker" },
            SessionId = "session-abcdef12",
            ProjectFiles = [new SuppliedFile("brief.md", "the brief"u8.ToArray())],
        };

        var workspace = await _workspaces.PrepareAsync(plan);
        var staged = Path.Combine(workspace.Path, "project-files", "brief.md");
        var writtenAt = File.GetLastWriteTimeUtc(staged);

        await Task.Delay(20);
        await _workspaces.PrepareAsync(plan);

        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(staged));
    }

    private sealed class StubGit : IGitService
    {
        public IReadOnlyList<string> LocalRepositoryRoots => [];

        public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandOutcome(true, string.Empty));

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

    private sealed class StubMcpTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));
    }
}
