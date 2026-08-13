using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Repositories;
using DevStudio.Application.Teams;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Scheduling;
using DevStudio.Domain.Skills;
using DevStudio.Domain.Teams;
using DevStudio.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Teams;

/// <summary>
/// Reads a git repository of shared definitions and applies it to this install. Everything the
/// repository defines is marked with the path it came from, which is what makes a second sync an
/// update rather than a pile of duplicates, and what lets a file being deleted mean the definition
/// goes too. Anything without that mark was made here and is left alone.
/// </summary>
public sealed class TeamSettingsService : ITeamSettingsService
{
    private readonly IEntityStore<TeamSettings> _settings;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly IEntityStore<Agent> _agents;
    private readonly IEntityStore<Skill> _skills;
    private readonly IEntityStore<Workflow> _workflows;
    private readonly IEntityStore<Schedule> _schedules;
    private readonly IEntityStore<CliProvider> _clis;
    private readonly IGitService _git;
    private readonly ILogger<TeamSettingsService> _logger;

    public TeamSettingsService(
        IEntityStore<TeamSettings> settings,
        IEntityStore<GitRepository> repositories,
        IEntityStore<GlobalSettings> globals,
        IEntityStore<Agent> agents,
        IEntityStore<Skill> skills,
        IEntityStore<Workflow> workflows,
        IEntityStore<Schedule> schedules,
        IEntityStore<CliProvider> clis,
        IGitService git,
        ILogger<TeamSettingsService> logger)
    {
        _settings = settings;
        _repositories = repositories;
        _globals = globals;
        _agents = agents;
        _skills = skills;
        _workflows = workflows;
        _schedules = schedules;
        _clis = clis;
        _git = git;
        _logger = logger;
    }

    public async Task<TeamSettings> GetAsync(CancellationToken ct = default) =>
        await _settings.GetAsync(TeamSettings.WellKnownId, ct) ?? new TeamSettings();

    public Task<TeamSettings> SaveAsync(TeamSettings settings, CancellationToken ct = default)
    {
        settings.Id = TeamSettings.WellKnownId;
        settings.Folder = (settings.Folder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

        return _settings.UpsertAsync(settings, ct);
    }

    public async Task<TeamSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var settings = await GetAsync(ct);
        var log = new List<string>();

        try
        {
            var folder = await ResolveFolderAsync(settings, log, ct);

            if (settings.PullBeforeSync)
                await PullAsync(settings, log, ct);

            var counts = await ImportAsync(folder, log, ct);

            settings.LastSyncedAt = DateTimeOffset.UtcNow;
            settings.LastError = null;
            settings.LastLog = log;
            await SaveAsync(settings, ct);

            var message = counts.Total == 0 && !counts.Standards
                ? "Nothing to import — the folder holds no definitions."
                : $"Imported {counts.Total} definition{(counts.Total == 1 ? "" : "s")}" +
                  $"{(counts.Removed > 0 ? $", removed {counts.Removed}" : string.Empty)}.";

            log.Add(message);
            return new TeamSyncResult(true, message, log, counts);
        }
        catch (Exception ex)
        {
            // Startup calls this with nobody watching, so a failure is reported rather than thrown.
            _logger.LogWarning(ex, "Team settings sync failed");
            log.Add(ex.Message);

            settings.LastSyncedAt = DateTimeOffset.UtcNow;
            settings.LastError = ex.Message;
            settings.LastLog = log;
            await SaveAsync(settings, CancellationToken.None);

            return new TeamSyncResult(false, ex.Message, log, new TeamSyncCounts());
        }
    }

    public async Task<TeamSyncResult> ScaffoldAsync(CancellationToken ct = default)
    {
        var settings = await GetAsync(ct);
        var log = new List<string>();

        try
        {
            var folder = await ResolveFolderAsync(settings, log, ct, create: true);

            foreach (var (relative, content) in Starters())
            {
                var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(path))
                {
                    log.Add($"{relative} already exists — left alone.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, ct);
                log.Add($"Wrote {relative}.");
            }

            log.Add("Nothing has been committed — review the files and commit them yourself.");
            return new TeamSyncResult(true, "Starter files written.", log, new TeamSyncCounts());
        }
        catch (Exception ex)
        {
            log.Add(ex.Message);
            return new TeamSyncResult(false, ex.Message, log, new TeamSyncCounts());
        }
    }

    /// <summary>
    /// The settings folder inside the checkout. The folder is configuration, so it is resolved against
    /// the repository and refused if it points outside it — a folder of <c>../../</c> would otherwise
    /// make this a reader for the whole disk.
    /// </summary>
    private async Task<string> ResolveFolderAsync(
        TeamSettings settings,
        List<string> log,
        CancellationToken ct,
        bool create = false)
    {
        if (string.IsNullOrWhiteSpace(settings.RepositoryId))
            throw new InvalidOperationException("No team settings repository is configured.");

        var repository = await _repositories.GetAsync(settings.RepositoryId, ct)
                         ?? throw new InvalidOperationException("The configured team settings repository no longer exists.");

        var candidate = string.IsNullOrWhiteSpace(settings.Folder)
            ? repository.LocalPath
            : Path.Combine(repository.LocalPath, settings.Folder.Replace('/', Path.DirectorySeparatorChar));

        if (!LocalRepositoryPaths.TryResolveWithinRoots(candidate, [repository.LocalPath], out var folder))
            throw new InvalidOperationException($"'{settings.Folder}' points outside {repository.Name}.");

        if (create)
            Directory.CreateDirectory(folder);

        if (!Directory.Exists(folder))
            throw new InvalidOperationException(
                $"'{folder}' is not in the checkout. Create the folder in {repository.Name}, or write the starter files.");

        log.Add($"Reading {repository.Name} at {folder}.");
        return folder;
    }

    private async Task PullAsync(TeamSettings settings, List<string> log, CancellationToken ct)
    {
        var repository = await _repositories.GetAsync(settings.RepositoryId!, ct);
        if (repository is null || string.IsNullOrWhiteSpace(repository.RemoteUrl))
        {
            log.Add("No origin remote, so nothing was pulled.");
            return;
        }

        var pull = await _git.RunAsync(repository.LocalPath, ["pull", "--ff-only"], ct);

        // A checkout with local work, or no network, is still worth importing as it stands — that is
        // less surprising than refusing to sync because a pull did not go through.
        log.Add(pull.Succeeded
            ? "Pulled the latest commit."
            : $"Could not pull, importing the checkout as it stands: {Trim(pull.Output)}");
    }

    private async Task<TeamSyncCounts> ImportAsync(string folder, List<string> log, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var standards = await ImportStandardsAsync(folder, log, ct);
        var skills = await ImportSkillsAsync(folder, seen, log, ct);
        var agents = await ImportAgentsAsync(folder, seen, log, ct);
        var workflows = await ImportWorkflowsAsync(folder, seen, log, ct);
        var schedules = await ImportSchedulesAsync(folder, seen, log, ct);
        var removed = await RemoveDeletedAsync(seen, log, ct);

        return new TeamSyncCounts(skills, agents, workflows, schedules, removed, standards);
    }

    private async Task<bool> ImportStandardsAsync(string folder, List<string> log, CancellationToken ct)
    {
        var path = Path.Combine(folder, TeamDefinitions.StandardsFile);
        var globals = await _globals.GetAsync(GlobalSettings.WellKnownId, ct) ?? new GlobalSettings();

        if (!File.Exists(path))
        {
            if (globals.TeamInstructions.Length == 0)
                return false;

            // The file was there last time. Leaving its text applied would make a deletion in the
            // repository do nothing, which is the one thing a sync must not do.
            globals.TeamInstructions = string.Empty;
            await _globals.UpsertAsync(globals, ct);
            log.Add($"{TeamDefinitions.StandardsFile} has gone; team standards cleared.");
            return false;
        }

        var (_, body) = TeamDefinitions.ReadFrontmatter(await File.ReadAllTextAsync(path, ct));

        if (globals.TeamInstructions == body)
        {
            log.Add("Team standards unchanged.");
            return true;
        }

        globals.TeamInstructions = body;
        await _globals.UpsertAsync(globals, ct);
        log.Add("Team standards updated.");
        return true;
    }

    private async Task<int> ImportSkillsAsync(
        string folder,
        HashSet<string> seen,
        List<string> log,
        CancellationToken ct)
    {
        var existing = await _skills.GetAllAsync(ct);
        var count = 0;

        foreach (var file in Files(folder, TeamDefinitions.SkillsFolder, "*.md"))
        {
            var relative = Relative(folder, file);
            var (front, body) = TeamDefinitions.ReadFrontmatter(await File.ReadAllTextAsync(file, ct));
            var slug = Value(front, "slug") ?? Path.GetFileNameWithoutExtension(file);

            var skill = Owned(existing, relative) ?? new Skill();
            skill.Name = Value(front, "name") ?? slug;
            skill.Slug = slug;
            skill.Description = Value(front, "description") ?? string.Empty;
            skill.Content = body;
            skill.Tags = TeamDefinitions.ReadList(Value(front, "tags"));
            skill.Enabled = Value(front, "enabled") is not "false";
            skill.TeamSourcePath = relative;

            await _skills.UpsertAsync(skill, ct);
            seen.Add(relative);
            count++;
        }

        if (count > 0)
            log.Add($"{count} skill{(count == 1 ? "" : "s")}.");

        return count;
    }

    private async Task<int> ImportAgentsAsync(
        string folder,
        HashSet<string> seen,
        List<string> log,
        CancellationToken ct)
    {
        var existing = await _agents.GetAllAsync(ct);

        // Read after the skills were imported, so an agent can name a skill from the same commit.
        var skills = await _skills.GetAllAsync(ct);
        var clis = await _clis.GetAllAsync(ct);
        var count = 0;

        foreach (var file in Files(folder, TeamDefinitions.AgentsFolder, "*.json"))
        {
            var relative = Relative(folder, file);
            var definition = await ReadAsync<TeamAgentFile>(file, relative, log, ct);
            if (definition is null)
                continue;

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                log.Add($"{relative}: no name, skipped.");
                continue;
            }

            var agent = Owned(existing, relative) ?? new Agent();
            agent.Name = definition.Name.Trim();
            agent.Description = definition.Description;
            agent.Provider = definition.Provider;
            agent.Model = Blank(definition.Model);
            agent.Effort = Blank(definition.Effort);
            agent.OpeningModel = Blank(definition.OpeningModel);
            agent.OpeningEffort = Blank(definition.OpeningEffort);
            agent.OpeningTurns = Math.Max(0, definition.OpeningTurns);
            agent.PermissionMode = definition.PermissionMode;
            agent.SystemPrompt = definition.SystemPrompt;
            agent.DefaultPrompt = definition.DefaultPrompt;
            agent.UseWorktree = definition.UseWorktree;
            agent.Environment = new Dictionary<string, string>(definition.Environment);
            agent.ExtraArguments = Blank(definition.ExtraArguments);
            agent.Accent = string.IsNullOrWhiteSpace(definition.Accent) ? "cyan" : definition.Accent;
            agent.Enabled = definition.Enabled;
            agent.TeamSourcePath = relative;

            // A repository cannot know this install's ids, so it names things and they are matched
            // here. Anything unmatched is reported rather than quietly dropped.
            agent.SkillIds = [.. definition.Skills
                .Select(name => Match(skills, name, s => s.Slug, s => s.Name))
                .Where(skill => skill is not null)
                .Select(skill => skill!.Id)];

            foreach (var missing in definition.Skills.Where(name => Match(skills, name, s => s.Slug, s => s.Name) is null))
                log.Add($"{relative}: no skill called '{missing}'.");

            if (definition.Provider == AiProvider.Custom)
            {
                var cli = clis.FirstOrDefault(c => string.Equals(c.Name, definition.Cli, StringComparison.OrdinalIgnoreCase));
                agent.CliProviderId = cli?.Id;

                if (cli is null)
                    log.Add($"{relative}: no CLI provider called '{definition.Cli}' on this install.");
            }
            else
            {
                agent.CliProviderId = null;
            }

            await _agents.UpsertAsync(agent, ct);
            seen.Add(relative);
            count++;
        }

        if (count > 0)
            log.Add($"{count} agent{(count == 1 ? "" : "s")}.");

        return count;
    }

    private async Task<int> ImportWorkflowsAsync(
        string folder,
        HashSet<string> seen,
        List<string> log,
        CancellationToken ct)
    {
        var existing = await _workflows.GetAllAsync(ct);
        var agents = await _agents.GetAllAsync(ct);
        var count = 0;

        foreach (var file in Files(folder, TeamDefinitions.WorkflowsFolder, "*.json"))
        {
            var relative = Relative(folder, file);
            var definition = await ReadAsync<TeamWorkflowFile>(file, relative, log, ct);
            if (definition is null)
                continue;

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                log.Add($"{relative}: no name, skipped.");
                continue;
            }

            var workflow = Owned(existing, relative) ?? new Workflow();
            workflow.Name = definition.Name.Trim();
            workflow.Description = definition.Description;
            workflow.Enabled = definition.Enabled;
            workflow.TeamSourcePath = relative;

            workflow.Inputs =
            [
                .. definition.Inputs.Select(input => new WorkflowInput
                {
                    Name = input.Name,
                    Description = input.Description,
                    DefaultValue = input.Default,
                    Required = input.Required,
                }),
            ];

            workflow.Steps = [];

            foreach (var step in definition.Steps.OrderBy(s => s.Order))
            {
                var agent = Match(agents, step.Agent, a => a.Name);

                if (agent is null)
                    log.Add($"{relative}: step '{step.Name}' names agent '{step.Agent}', which is not here.");

                workflow.Steps.Add(new WorkflowStep
                {
                    Name = step.Name,
                    Order = step.Order,
                    AgentId = agent?.Id ?? string.Empty,
                    PromptTemplate = step.Prompt,
                    ContinueOnError = step.ContinueOnError,
                    ReuseWorkspace = step.ReuseWorkspace,
                    TimeoutMinutes = step.TimeoutMinutes,
                });
            }

            await _workflows.UpsertAsync(workflow, ct);
            seen.Add(relative);
            count++;
        }

        if (count > 0)
            log.Add($"{count} workflow{(count == 1 ? "" : "s")}.");

        return count;
    }

    private async Task<int> ImportSchedulesAsync(
        string folder,
        HashSet<string> seen,
        List<string> log,
        CancellationToken ct)
    {
        var existing = await _schedules.GetAllAsync(ct);
        var agents = await _agents.GetAllAsync(ct);
        var workflows = await _workflows.GetAllAsync(ct);
        var count = 0;

        foreach (var file in Files(folder, TeamDefinitions.SchedulesFolder, "*.json"))
        {
            var relative = Relative(folder, file);
            var definition = await ReadAsync<TeamScheduleFile>(file, relative, log, ct);
            if (definition is null)
                continue;

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                log.Add($"{relative}: no name, skipped.");
                continue;
            }

            var targetId = definition.Target == ScheduleTarget.Agent
                ? Match(agents, definition.TargetName, a => a.Name)?.Id
                : Match(workflows, definition.TargetName, w => w.Name)?.Id;

            if (targetId is null)
            {
                // A schedule with nothing to fire would sit in the list looking healthy and do
                // nothing at its due time, so it is refused outright.
                log.Add($"{relative}: no {definition.Target.ToString().ToLowerInvariant()} called '{definition.TargetName}', skipped.");
                continue;
            }

            var known = Owned(existing, relative);
            var schedule = known ?? new Schedule();
            schedule.Name = definition.Name.Trim();
            schedule.Description = definition.Description;
            schedule.Kind = definition.Kind;
            schedule.CronExpression = definition.Cron;
            schedule.IntervalMinutes = definition.IntervalMinutes;
            schedule.TimeZoneId = string.IsNullOrWhiteSpace(definition.TimeZone) ? "UTC" : definition.TimeZone;
            schedule.Target = definition.Target;
            schedule.TargetId = targetId;
            schedule.Prompt = definition.Prompt;
            schedule.Inputs = new Dictionary<string, string>(definition.Inputs);
            schedule.SkipIfRunning = definition.SkipIfRunning;
            schedule.TeamSourcePath = relative;

            // Whether a schedule is on is a local decision — somebody turned it off here for a
            // reason, and a sync arriving overnight should not turn it back on. Only the first import
            // takes the file's word for it.
            if (known is null)
                schedule.Enabled = definition.Enabled;

            await _schedules.UpsertAsync(schedule, ct);
            seen.Add(relative);
            count++;
        }

        if (count > 0)
            log.Add($"{count} schedule{(count == 1 ? "" : "s")}.");

        return count;
    }

    /// <summary>
    /// Removes what the repository used to define and no longer does. Only ever touches records
    /// carrying a source path, which is what keeps local definitions out of it.
    /// </summary>
    private async Task<int> RemoveDeletedAsync(HashSet<string> seen, List<string> log, CancellationToken ct)
    {
        var removed = 0;

        foreach (var skill in (await _skills.GetAllAsync(ct)).Where(s => Orphan(s.TeamSourcePath, seen)))
        {
            await _skills.DeleteAsync(skill.Id, ct);
            log.Add($"Removed skill '{skill.Name}' — its file has gone.");
            removed++;
        }

        foreach (var schedule in (await _schedules.GetAllAsync(ct)).Where(s => Orphan(s.TeamSourcePath, seen)))
        {
            await _schedules.DeleteAsync(schedule.Id, ct);
            log.Add($"Removed schedule '{schedule.Name}' — its file has gone.");
            removed++;
        }

        foreach (var workflow in (await _workflows.GetAllAsync(ct)).Where(w => Orphan(w.TeamSourcePath, seen)))
        {
            await _workflows.DeleteAsync(workflow.Id, ct);
            log.Add($"Removed workflow '{workflow.Name}' — its file has gone.");
            removed++;
        }

        // Agents last: a workflow or schedule pointing at one is gone by now, so nothing is left
        // referring to an agent that is not there.
        foreach (var agent in (await _agents.GetAllAsync(ct)).Where(a => Orphan(a.TeamSourcePath, seen)))
        {
            await _agents.DeleteAsync(agent.Id, ct);
            log.Add($"Removed agent '{agent.Name}' — its file has gone.");
            removed++;
        }

        return removed;
    }

    private static bool Orphan(string? source, HashSet<string> seen) =>
        source is { Length: > 0 } && !seen.Contains(source);

    private async Task<T?> ReadAsync<T>(string file, string relative, List<string> log, CancellationToken ct)
        where T : class
    {
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<T>(stream, TeamDefinitions.Json, ct);
        }
        catch (JsonException ex)
        {
            // One malformed file must not lose the rest of the commit.
            log.Add($"{relative}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Files of one kind, in a stable order so a log reads the same way twice.</summary>
    private static IEnumerable<string> Files(string folder, string subfolder, string pattern)
    {
        var path = Path.Combine(folder, subfolder);

        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>Forward slashes, so a path recorded on Windows still matches the same file on Linux.</summary>
    private static string Relative(string folder, string file) =>
        Path.GetRelativePath(folder, file).Replace('\\', '/');

    private static T? Owned<T>(IReadOnlyList<T> existing, string relative) where T : class =>
        existing.FirstOrDefault(item => string.Equals(
            SourceOf(item), relative, StringComparison.OrdinalIgnoreCase));

    private static string? SourceOf<T>(T item) => item switch
    {
        Agent agent => agent.TeamSourcePath,
        Skill skill => skill.TeamSourcePath,
        Workflow workflow => workflow.TeamSourcePath,
        Schedule schedule => schedule.TeamSourcePath,
        _ => null,
    };

    /// <summary>Finds an entity by any of the names it can be referred to by, ignoring case.</summary>
    private static T? Match<T>(IReadOnlyList<T> items, string? name, params Func<T, string?>[] keys) where T : class =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : items.FirstOrDefault(item => keys.Any(key =>
                string.Equals(key(item), name.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static string? Value(IReadOnlyDictionary<string, string> front, string key) =>
        front.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Trim(string output) =>
        output.Length <= 300 ? output.Trim() : output.Trim()[..300] + "…";

    /// <summary>
    /// One worked example of each kind, which is also the documentation: the format is easier to read
    /// off a file that works than out of a schema.
    /// </summary>
    private static IEnumerable<(string Path, string Content)> Starters()
    {
        yield return ("README.md",
            """
            # Team settings

            devStudio imports this folder into every install that points at it: agents, workflows,
            skills, schedules and the standards every session inherits. Edit the files, commit, and
            each machine picks the change up on its next sync.

            - `standards.md` — standing instructions for every session.
            - `agents/*.json` — agent definitions. Skills are named, not referenced by id.
            - `skills/*.md` — a skill per file, with `name`, `description` and `tags` in frontmatter.
            - `workflows/*.json` — steps naming the agent that runs them.
            - `schedules/*.json` — cron triggers naming the agent or workflow they fire.

            Anything created in the UI instead of here stays local to that install and is never
            touched by a sync. Deleting a file here does remove what it defined, everywhere.
            """);

        yield return (TeamDefinitions.StandardsFile,
            """
            Read the existing code before changing it and match what is already there. Keep changes
            focused on what was asked. Run the tests before claiming something works, and say plainly
            if they fail.
            """);

        yield return ($"{TeamDefinitions.SkillsFolder}/conventional-commits.md",
            """
            ---
            name: Conventional commits
            description: Use when writing commit messages or opening a pull request.
            tags: git, workflow
            ---

            Write commit subjects as `type(scope): summary` using the conventional commit types
            (feat, fix, docs, refactor, test, chore). Keep the subject under 72 characters and explain
            the reasoning in the body, not the mechanics of the diff.
            """);

        yield return ($"{TeamDefinitions.AgentsFolder}/builder.json",
            """
            {
              "name": "Team builder",
              "description": "Implements a change in an isolated worktree.",
              "provider": "Claude",
              "model": "sonnet",
              "effort": "medium",
              "openingModel": "opus",
              "openingEffort": "high",
              "openingTurns": 2,
              "permissionMode": "AcceptEdits",
              "systemPrompt": "You are working inside an isolated git worktree. Make focused changes and explain what you did.",
              "useWorktree": true,
              "skills": ["conventional-commits"],
              "accent": "cyan"
            }
            """);

        yield return ($"{TeamDefinitions.WorkflowsFolder}/build-then-review.json",
            """
            {
              "name": "Build then review",
              "description": "One agent implements the change, then reviews its own work.",
              "inputs": [
                { "name": "task", "description": "What needs building", "required": true }
              ],
              "steps": [
                {
                  "name": "Implement",
                  "order": 1,
                  "agent": "Team builder",
                  "prompt": "{{task}}\n\nImplement this, then summarise every file you changed."
                },
                {
                  "name": "Review",
                  "order": 2,
                  "agent": "Team builder",
                  "prompt": "Review the working tree against the original task: {{task}}\n\nList anything wrong or risky, then fix it."
                }
              ]
            }
            """);

        yield return ($"{TeamDefinitions.SchedulesFolder}/weekday-triage.json",
            """
            {
              "name": "Weekday triage",
              "description": "Looks over the repository every weekday morning.",
              "kind": "Cron",
              "cron": "0 9 * * 1-5",
              "timeZone": "UTC",
              "target": "Agent",
              "targetName": "Team builder",
              "prompt": "Look over the open work and report anything that needs a person to decide.",
              "enabled": false
            }
            """);
    }
}
