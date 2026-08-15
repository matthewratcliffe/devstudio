using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Teams;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Scheduling;
using DevStudio.Domain.Skills;
using DevStudio.Domain.Teams;
using DevStudio.Domain.Workflows;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Teams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// A team keeps its agents, workflows, skills, schedules and standards in a repository so they are
/// reviewed and versioned like code. Two properties matter more than anything else here: a second sync
/// updates rather than duplicates, and nothing anybody made locally is touched.
/// </summary>
public class TeamSettingsSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-team-" + Guid.NewGuid().ToString("n"));
    private readonly string _checkout;
    private readonly string _folder;
    private readonly IOptions<OrchestratorOptions> _options;
    private readonly TeamSettingsService _service;
    private readonly IEntityStore<Agent> _agents;
    private readonly IEntityStore<Skill> _skills;
    private readonly IEntityStore<Workflow> _workflows;
    private readonly IEntityStore<Schedule> _schedules;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly IEntityStore<TeamSettings> _settings;

    public TeamSettingsSyncTests()
    {
        _checkout = Path.Combine(_root, "checkout");
        _folder = Path.Combine(_checkout, "devstudio");
        Directory.CreateDirectory(_folder);

        _options = Options.Create(new OrchestratorOptions { DataPath = _root });
        _agents = Store<Agent>();
        _skills = Store<Skill>();
        _workflows = Store<Workflow>();
        _schedules = Store<Schedule>();
        _globals = Store<GlobalSettings>();
        _settings = Store<TeamSettings>();

        var repositories = Store<GitRepository>();

        // No origin remote, so the sync has nothing to pull and never shells out to git.
        var repository = repositories.UpsertAsync(new GitRepository
        {
            Name = "team-settings",
            LocalPath = _checkout,
            IsLocal = true,
        }).GetAwaiter().GetResult();

        _settings.UpsertAsync(new TeamSettings
        {
            RepositoryId = repository.Id,
            Folder = "devstudio",
            PullBeforeSync = false,
        }).GetAwaiter().GetResult();

        _service = new TeamSettingsService(
            _settings,
            repositories,
            _globals,
            _agents,
            _skills,
            _workflows,
            _schedules,
            Store<CliProvider>(),
            new UnusedGit(),
            NullLogger<TeamSettingsService>.Instance);
    }

    private JsonEntityStore<T> Store<T>() where T : class, IEntity =>
        new(_options, NullLogger<JsonEntityStore<T>>.Instance);

    private async Task WriteAsync(string relative, string content)
    {
        var path = Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private void Delete(string relative) =>
        File.Delete(Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar)));

    private async Task WriteEverythingAsync()
    {
        await WriteAsync("standards.md", "Match the code that is already there.");

        await WriteAsync("skills/conventional-commits.md",
            """
            ---
            name: Conventional commits
            description: Use when writing commit messages.
            tags: git, workflow
            ---

            Write commit subjects as `type(scope): summary`.
            """);

        await WriteAsync("agents/builder.json",
            """
            {
              "name": "Team builder",
              "provider": "Claude",
              "model": "sonnet",
              "openingModel": "fable",
              "openingEffort": "high",
              "openingTurns": 2,
              "permissionMode": "AcceptEdits",
              "skills": ["conventional-commits"]
            }
            """);

        await WriteAsync("workflows/build.json",
            """
            {
              "name": "Build",
              "steps": [
                { "name": "Implement", "order": 1, "agent": "Team builder", "prompt": "{{task}}" }
              ]
            }
            """);

        await WriteAsync("schedules/nightly.json",
            """
            {
              "name": "Nightly",
              "cron": "0 2 * * *",
              "target": "Agent",
              "targetName": "Team builder",
              "prompt": "Look over the open work.",
              "enabled": true
            }
            """);
    }

    [Fact]
    public async Task Every_kind_of_definition_is_imported()
    {
        await WriteEverythingAsync();

        var result = await _service.SyncAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new TeamSyncCounts(1, 1, 1, 1, 0, true), result.Counts);

        var settings = await _globals.GetAsync(GlobalSettings.WellKnownId);
        Assert.Equal("Match the code that is already there.", settings!.TeamInstructions);

        var agent = Assert.Single(await _agents.GetAllAsync());
        Assert.Equal("Team builder", agent.Name);
        Assert.Equal("agents/builder.json", agent.TeamSourcePath);
        Assert.Equal("fable", agent.OpeningModel);
        Assert.Equal(2, agent.OpeningTurns);

        // Named in the file, resolved to the id this install gave the skill.
        var skill = Assert.Single(await _skills.GetAllAsync());
        Assert.Equal([skill.Id], agent.SkillIds);
        Assert.Equal(["git", "workflow"], skill.Tags);
        Assert.Equal("Write commit subjects as `type(scope): summary`.", skill.Content);

        var workflow = Assert.Single(await _workflows.GetAllAsync());
        Assert.Equal(agent.Id, workflow.Steps[0].AgentId);

        var schedule = Assert.Single(await _schedules.GetAllAsync());
        Assert.Equal(agent.Id, schedule.TargetId);
        Assert.True(schedule.Enabled);
    }

    [Fact]
    public async Task Syncing_twice_updates_instead_of_duplicating()
    {
        await WriteEverythingAsync();
        await _service.SyncAsync();
        var first = Assert.Single(await _agents.GetAllAsync());

        await WriteAsync("agents/builder.json",
            """
            { "name": "Team builder", "provider": "Claude", "model": "haiku", "skills": [] }
            """);

        await _service.SyncAsync();

        var second = Assert.Single(await _agents.GetAllAsync());
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("haiku", second.Model);
        Assert.Empty(second.SkillIds);
    }

    [Fact]
    public async Task A_definition_whose_file_has_gone_is_removed()
    {
        await WriteEverythingAsync();
        await _service.SyncAsync();

        Delete("schedules/nightly.json");
        Delete("standards.md");

        var result = await _service.SyncAsync();

        Assert.Empty(await _schedules.GetAllAsync());
        Assert.Equal(1, result.Counts.Removed);
        Assert.Equal(string.Empty, (await _globals.GetAsync(GlobalSettings.WellKnownId))!.TeamInstructions);
    }

    [Fact]
    public async Task Anything_made_locally_is_left_alone()
    {
        var mine = await _agents.UpsertAsync(new Agent { Name = "Mine", Model = "opus" });
        var myScheduleName = "My schedule";
        await _schedules.UpsertAsync(new Schedule { Name = myScheduleName, TargetId = mine.Id });

        await WriteEverythingAsync();
        await _service.SyncAsync();
        Delete("agents/builder.json");
        await _service.SyncAsync();

        var agent = Assert.Single(await _agents.GetAllAsync());
        Assert.Equal(mine.Id, agent.Id);
        Assert.Equal("opus", agent.Model);
        Assert.Contains(await _schedules.GetAllAsync(), s => s.Name == myScheduleName);
    }

    [Fact]
    public async Task Turning_a_team_schedule_off_survives_the_next_sync()
    {
        await WriteEverythingAsync();
        await _service.SyncAsync();

        var schedule = Assert.Single(await _schedules.GetAllAsync());
        schedule.Enabled = false;
        await _schedules.UpsertAsync(schedule);

        await _service.SyncAsync();

        // A sync arriving overnight must not start something somebody deliberately stopped.
        Assert.False(Assert.Single(await _schedules.GetAllAsync()).Enabled);
    }

    [Fact]
    public async Task A_schedule_with_no_target_here_is_refused_rather_than_left_looking_healthy()
    {
        await WriteAsync("schedules/orphan.json",
            """
            { "name": "Orphan", "target": "Agent", "targetName": "Nobody" }
            """);

        var result = await _service.SyncAsync();

        Assert.Empty(await _schedules.GetAllAsync());
        Assert.Contains(result.Log, line => line.Contains("Nobody"));
    }

    [Fact]
    public async Task One_broken_file_does_not_lose_the_rest_of_the_commit()
    {
        await WriteEverythingAsync();
        await WriteAsync("agents/broken.json", "{ this is not json");

        var result = await _service.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Counts.Agents);
        Assert.Contains(result.Log, line => line.Contains("broken.json"));
    }

    [Fact]
    public async Task A_folder_pointing_out_of_the_repository_is_refused()
    {
        var settings = await _service.GetAsync();
        settings.Folder = "../../elsewhere";
        await _service.SaveAsync(settings);

        var result = await _service.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("outside", result.Message);
    }

    [Fact]
    public async Task Without_a_repository_the_feature_is_simply_off()
    {
        await _service.SaveAsync(new TeamSettings { RepositoryId = null });

        var result = await _service.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No team settings repository", result.Message);
    }

    [Fact]
    public async Task Starter_files_are_written_once_and_then_import_cleanly()
    {
        var written = await _service.ScaffoldAsync();
        Assert.True(written.Succeeded, written.Message);

        var again = await _service.ScaffoldAsync();
        Assert.Contains(again.Log, line => line.Contains("already exists"));

        // The examples are also the documentation of the format, so they had better parse.
        var result = await _service.SyncAsync();
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Counts.Agents);
        Assert.Equal(1, result.Counts.Skills);
        Assert.Equal(1, result.Counts.Workflows);
        Assert.Equal(1, result.Counts.Schedules);
        Assert.True(result.Counts.Standards);

        // The example schedule says so, and a repository must not start work on a machine by itself.
        Assert.False(Assert.Single(await _schedules.GetAllAsync()).Enabled);
    }

    [Fact]
    public void Frontmatter_is_split_from_the_body_and_a_plain_file_is_all_body()
    {
        var (front, body) = TeamDefinitions.ReadFrontmatter("---\nname: A skill\ntags: [git, ci]\n---\n\nDo the thing.\n");

        Assert.Equal("A skill", front["name"]);
        Assert.Equal(["git", "ci"], TeamDefinitions.ReadList(front["tags"]));
        Assert.Equal("Do the thing.", body);

        var (none, all) = TeamDefinitions.ReadFrontmatter("Just markdown.");
        Assert.Empty(none);
        Assert.Equal("Just markdown.", all);
    }

    /// <summary>A repository with no remote is never pulled, so none of this should be reached.</summary>
    private sealed class UnusedGit : IGitService
    {
        public IReadOnlyList<string> LocalRepositoryRoots => [];

        public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
            throw new NotSupportedException();

        public Task<string> GetStatusAsync(string workingDirectory, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Worktree> CreateWorktreeAsync(GitRepository repository, string branch, string? baseBranch, bool ephemeral, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitCommandOutcome> RemoveWorktreeAsync(GitRepository repository, Worktree worktree, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
