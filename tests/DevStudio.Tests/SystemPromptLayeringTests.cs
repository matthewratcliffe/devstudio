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

public class SystemPromptLayeringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-prompt-" + Guid.NewGuid().ToString("n"));
    private readonly IEntityStore<Project> _projects;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly WorkspaceService _service;

    public SystemPromptLayeringTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });

        _projects = Store<Project>(options);
        _globals = Store<GlobalSettings>(options);

        _service = new WorkspaceService(
            new StubGit(),
            Store<GitRepository>(options),
            Store<Skill>(options),
            Store<McpServer>(options),
            new StubTokens(),
            _projects,
            _globals,
            options,
            NullLogger<WorkspaceService>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task Standards_come_before_the_project_which_comes_before_the_agent()
    {
        await _globals.UpsertAsync(new GlobalSettings { Instructions = "GLOBAL-RULE" });
        var project = await _projects.UpsertAsync(new Project { Name = "Client", Instructions = "PROJECT-RULE" });
        var agent = new Agent { SystemPrompt = "AGENT-RULE" };

        var prompt = await _service.ComposeSystemPromptAsync(agent, project.Id);

        Assert.Contains("GLOBAL-RULE", prompt);
        Assert.Contains("PROJECT-RULE", prompt);
        Assert.Contains("AGENT-RULE", prompt);
        Assert.True(prompt.IndexOf("GLOBAL-RULE", StringComparison.Ordinal) < prompt.IndexOf("PROJECT-RULE", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("PROJECT-RULE", StringComparison.Ordinal) < prompt.IndexOf("AGENT-RULE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_agent_is_told_which_local_tools_it_already_has()
    {
        var prompt = await _service.ComposeSystemPromptAsync(new Agent(), null);

        // Left unsaid, an agent goes looking for a hosted connector and reports it as unauthorised.
        Assert.Contains("glab", prompt);
        Assert.Contains("gh", prompt);
        Assert.Contains("glab mr diff", prompt);
    }

    [Fact]
    public async Task Standards_apply_to_a_session_with_no_project_at_all()
    {
        await _globals.UpsertAsync(new GlobalSettings { Instructions = "GLOBAL-RULE" });

        var prompt = await _service.ComposeSystemPromptAsync(new Agent { SystemPrompt = "AGENT-RULE" }, null);

        Assert.Contains("GLOBAL-RULE", prompt);
    }

    [Fact]
    public async Task A_project_can_opt_out_of_the_standards()
    {
        await _globals.UpsertAsync(new GlobalSettings { Instructions = "GLOBAL-RULE" });
        var project = await _projects.UpsertAsync(new Project
        {
            Name = "Rebel",
            Instructions = "PROJECT-RULE",
            InheritGlobalInstructions = false,
        });

        var prompt = await _service.ComposeSystemPromptAsync(new Agent(), project.Id);

        Assert.DoesNotContain("GLOBAL-RULE", prompt);
        Assert.Contains("PROJECT-RULE", prompt);
    }

    [Fact]
    public async Task Global_files_are_listed_for_the_agent_to_find()
    {
        await _globals.UpsertAsync(new GlobalSettings
        {
            Instructions = "GLOBAL-RULE",
            Files = [new StoredFile { FileName = "coding-standards.md", IsText = true }],
        });

        var prompt = await _service.ComposeSystemPromptAsync(new Agent(), null);

        Assert.Contains("coding-standards.md", prompt);
        Assert.Contains("./global-files", prompt);
    }

    [Fact]
    public async Task Global_files_are_staged_into_the_workspace()
    {
        var library = new FileLibraryService(_projects, _globals, Options.Create(new OrchestratorOptions { DataPath = _root }));
        await _globals.UpsertAsync(new GlobalSettings());

        using var content = new MemoryStream("# standards"u8.ToArray());
        await library.SaveAsync(FileScope.Global, "coding-standards.md", content, "text/markdown");

        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        await _service.MaterialiseGlobalFilesAsync(workspace);

        var staged = Path.Combine(workspace, "global-files", "coding-standards.md");
        Assert.True(File.Exists(staged));
        Assert.Equal("# standards", await File.ReadAllTextAsync(staged));
    }

    /// <summary>No MCP servers in these tests, so no tokens are ever requested.</summary>
    private sealed class StubTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));
    }

    private sealed class StubGit : IGitService
    {
        public Task<GitRepository> CloneAsync(
            string remoteUrl,
            string? name,
            SourceControlProvider sourceControl = SourceControlProvider.GitHub,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> LocalRepositoryRoots => [];

        public Task<LocalBrowseResult> BrowseLocalAsync(string? path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> AttachLocalAsync(string path, string? name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> RenameAsync(GitRepository repository, string? name, CancellationToken ct = default) =>
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

        public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandOutcome(true, string.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
