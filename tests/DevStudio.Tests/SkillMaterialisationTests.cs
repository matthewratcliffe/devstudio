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
using DevStudio.Infrastructure.Skills;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// A pulled skill is a folder, not a file: SKILL.md tells the model to read `rules/x.md`, and if
/// only SKILL.md reaches the workspace every one of those instructions is a dead path.
/// </summary>
public class SkillMaterialisationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-skillfiles-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;
    private readonly IEntityStore<Skill> _skills;
    private readonly ISkillImporter _importer;
    private readonly WorkspaceService _service;

    public SkillMaterialisationTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);

        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });
        _skills = Store<Skill>(options);

        _importer = new SkillImporter(
            new StubRegistry(),
            _skills,
            options,
            NullLogger<SkillImporter>.Instance);

        _service = new WorkspaceService(
            new StubGit(),
            Store<GitRepository>(options),
            _skills,
            Store<McpServer>(options),
            new StubTokens(),
            Store<Project>(options),
            Store<GlobalSettings>(options),
            options,
            NullLogger<WorkspaceService>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task A_pulled_skill_arrives_with_the_files_its_instructions_name()
    {
        var imported = await _importer.ImportAsync("owner/repo", "slug");
        var agent = new Agent { SkillIds = [imported.Skill.Id] };

        await _service.MaterialiseSkillsAsync(agent, _workspace);

        var folder = Path.Combine(_workspace, ".claude", "skills", imported.Skill.Slug);
        Assert.True(File.Exists(Path.Combine(folder, "SKILL.md")));
        Assert.Equal("# Memo", await File.ReadAllTextAsync(Path.Combine(folder, "rules", "memo.md")));
        Assert.Equal("# Readme", await File.ReadAllTextAsync(Path.Combine(folder, "README.md")));
    }

    [Fact]
    public async Task The_regenerated_manifest_is_the_one_that_survives()
    {
        var imported = await _importer.ImportAsync("owner/repo", "slug");
        await _service.MaterialiseSkillsAsync(new Agent { SkillIds = [imported.Skill.Id] }, _workspace);

        var manifest = await File.ReadAllTextAsync(
            Path.Combine(_workspace, ".claude", "skills", imported.Skill.Slug, "SKILL.md"));

        // Written from the entity, so it carries the slug the workspace folder uses — a copy of the
        // registry's own SKILL.md would name the skill something the folder does not.
        Assert.Contains($"name: {imported.Skill.Slug}", manifest);
        Assert.Contains("Read `rules/memo.md`", manifest);
    }

    [Fact]
    public async Task Codex_is_told_where_the_supporting_files_are()
    {
        var imported = await _importer.ImportAsync("owner/repo", "slug");
        await _service.MaterialiseSkillsAsync(new Agent { SkillIds = [imported.Skill.Id] }, _workspace);

        // Codex has no skills folder, so AGENTS.orchestrator.md is the only place it learns the
        // reference tree exists at all.
        var agents = await File.ReadAllTextAsync(Path.Combine(_workspace, "AGENTS.orchestrator.md"));

        Assert.Contains($".claude/skills/{imported.Skill.Slug}/", agents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private sealed class StubRegistry : ISkillRegistry
    {
        public string Name => "stub";

        public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillSearchResult>>([]);

        public Task<SkillPackage?> FetchAsync(string source, string slug, CancellationToken ct = default) =>
            Task.FromResult<SkillPackage?>(new SkillPackage(
                source,
                slug,
                "React rules",
                "When writing React",
                "Read `rules/memo.md` before refactoring.",
                [],
                [new SkillFile("rules/memo.md", "# Memo"), new SkillFile("README.md", "# Readme")],
                "hash-1"));
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

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));
    }
}
