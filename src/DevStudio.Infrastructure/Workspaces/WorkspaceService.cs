using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Agents;
using DevStudio.Application.Common;
using DevStudio.Application.Globals;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Skills;
using DevStudio.Infrastructure.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Workspaces;

/// <summary>
/// Builds the directory a session runs in. Order of preference: a fresh worktree when the agent is
/// bound to a repository, then the project folder, then shared scratch.
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly IGitService _git;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly IEntityStore<Skill> _skills;
    private readonly IEntityStore<McpServer> _mcpServers;
    private readonly IMcpTokenService _mcpTokens;
    private readonly IEntityStore<Project> _projects;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly IStandardsFilesSyncService _standardsFiles;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(
        IGitService git,
        IEntityStore<GitRepository> repositories,
        IEntityStore<Skill> skills,
        IEntityStore<McpServer> mcpServers,
        IMcpTokenService mcpTokens,
        IEntityStore<Project> projects,
        IEntityStore<GlobalSettings> globals,
        IStandardsFilesSyncService standardsFiles,
        IOptions<OrchestratorOptions> options,
        ILogger<WorkspaceService> logger)
    {
        _git = git;
        _repositories = repositories;
        _skills = skills;
        _mcpServers = mcpServers;
        _mcpTokens = mcpTokens;
        _projects = projects;
        _globals = globals;
        _standardsFiles = standardsFiles;
        _options = options.Value;
        _logger = logger;
    }

    public Task<SessionWorkspace> PrepareAsync(Agent agent, string sessionId, string? projectId, CancellationToken ct = default) =>
        PrepareAsync(agent, sessionId, projectId, null, ct);

    public async Task<SessionWorkspace> PrepareAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        IReadOnlyList<string>? extraServerIds,
        CancellationToken ct = default)
    {
        projectId ??= agent.ProjectId;
        var project = projectId is null ? null : await _projects.GetAsync(projectId, ct);

        var repositoryId = agent.RepositoryId ?? project?.RepositoryId;
        SessionWorkspace workspace;
        var isLocalRepository = false;

        if (!string.IsNullOrWhiteSpace(repositoryId) &&
            await _repositories.GetAsync(repositoryId!, ct) is { } repository)
        {
            isLocalRepository = repository.IsLocal;

            if (agent.UseWorktree)
            {
                var branch = $"agent/{TemplateRenderer.Slugify(agent.Name)}/{sessionId[..8]}";
                var baseBranch = agent.BaseBranch ?? project?.BaseBranch ?? repository.DefaultBranch;
                var worktree = await _git.CreateWorktreeAsync(repository, branch, baseBranch, ephemeral: true, ct);
                worktree.SessionId = sessionId;
                await _repositories.UpsertAsync(repository, ct);
                workspace = new SessionWorkspace(worktree.Path, repository.Id, worktree, projectId);
            }
            else
            {
                workspace = new SessionWorkspace(repository.LocalPath, repository.Id, null, projectId);
            }
        }
        else if (project is not null)
        {
            var path = ProjectWorkspacePath(project.Id);
            Directory.CreateDirectory(path);
            workspace = new SessionWorkspace(path, null, null, projectId);
        }
        else
        {
            var path = Path.Combine(_options.ScratchPath, sessionId[..8]);
            Directory.CreateDirectory(path);
            workspace = new SessionWorkspace(path, null, null, null);
        }

        await TrySyncStandardsFilesAsync(ct);

        await MaterialiseSkillsAsync(agent, workspace.Path, ct);
        await MaterialiseMcpAsync(agent, workspace.Path, extraServerIds, ct);
        await MaterialiseGlobalFilesAsync(workspace.Path, ct);

        if (projectId is not null)
            await MaterialiseProjectFilesAsync(projectId, workspace.Path, ct);

        // Everything staged above is untracked. In a volume clone nobody sees it; in a checkout an
        // IDE on the host has open it reads as a dozen stray files, and gets committed by accident.
        if (isLocalRepository)
            await IgnoreStagedArtefactsAsync(workspace.Path, ct);

        return workspace;
    }

    public async Task ReleaseAsync(SessionWorkspace workspace, CancellationToken ct = default)
    {
        if (workspace.Worktree is null || !workspace.Worktree.IsEphemeral || !_options.PruneEphemeralWorktrees)
            return;

        if (workspace.RepositoryId is null)
            return;

        var repository = await _repositories.GetAsync(workspace.RepositoryId, ct);
        if (repository is null)
            return;

        // Uncommitted work would be lost, so pruning is opt-in and never silent.
        _logger.LogInformation("Pruning ephemeral worktree {Path}", workspace.Worktree.Path);
        await _git.RemoveWorktreeAsync(repository, workspace.Worktree, ct);
    }

    public async Task MaterialiseSkillsAsync(Agent agent, string workspacePath, CancellationToken ct = default)
    {
        if (agent.SkillIds.Count == 0)
            return;

        var all = await _skills.GetAllAsync(ct);
        var selected = all.Where(s => s.Enabled && agent.SkillIds.Contains(s.Id)).ToList();
        if (selected.Count == 0)
            return;

        // Claude discovers .claude/skills; Codex has no equivalent, so the same content is also
        // written to AGENTS.md where it will be read as project instructions.
        var skillsRoot = Path.Combine(workspacePath, ".claude", "skills");
        Directory.CreateDirectory(skillsRoot);

        var agentsFile = new StringBuilder();

        foreach (var skill in selected)
        {
            var slug = TemplateRenderer.Slugify(
                string.IsNullOrWhiteSpace(skill.Slug) ? skill.Name : skill.Slug);
            if (slug.Length == 0)
                slug = skill.Id;
            var directory = Path.Combine(skillsRoot, slug);
            Directory.CreateDirectory(directory);

            var frontmatter = new StringBuilder()
                .AppendLine("---")
                .AppendLine($"name: {slug}")
                .AppendLine($"description: {EscapeYaml(skill.Description)}")
                .AppendLine("---")
                .AppendLine()
                .Append(skill.Content)
                .ToString();

            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), frontmatter, ct);

            // A pulled skill is usually a folder, not a file: SKILL.md reads its rules and
            // references by relative path, so without these it arrives full of dead links.
            if (skill.BundleFileCount > 0)
                CopyTree(SkillBundle.PathFor(_options.DataPath, skill.Id), directory);

            agentsFile.AppendLine($"## Skill: {skill.Name}");
            agentsFile.AppendLine();
            agentsFile.AppendLine(skill.Description);
            agentsFile.AppendLine();
            agentsFile.AppendLine(skill.Content);
            agentsFile.AppendLine();

            // Codex reads this file, not the skills folder, so the body above is all it gets. Point
            // it at the supporting files rather than inlining a reference tree it did not ask for.
            if (skill.BundleFileCount > 0)
            {
                agentsFile.AppendLine(
                    $"Supporting files for this skill are in `.claude/skills/{slug}/`. "
                    + "Read them from there when the instructions above reference a relative path.");
                agentsFile.AppendLine();
            }
        }

        if (agentsFile.Length > 0)
        {
            var agentsPath = Path.Combine(workspacePath, "AGENTS.orchestrator.md");
            await File.WriteAllTextAsync(agentsPath, agentsFile.ToString(), ct);
        }
    }

    public async Task<IReadOnlyList<string>> MaterialiseMcpAsync(
        Agent agent,
        string workspacePath,
        IReadOnlyList<string>? extraServerIds = null,
        CancellationToken ct = default)
    {
        var all = await _mcpServers.GetAllAsync(ct);
        var selected = all
            .Where(s => s.Enabled && (s.IsDefault
                                      || agent.McpServerIds.Contains(s.Id)
                                      || (extraServerIds?.Contains(s.Id) ?? false)))
            .ToList();

        var configPath = Path.Combine(workspacePath, ".mcp.json");

        if (selected.Count == 0)
        {
            if (File.Exists(configPath))
                File.Delete(configPath);

            return [];
        }

        var servers = new JsonObject();
        foreach (var server in selected)
        {
            var entry = new JsonObject();

            if (server.Transport == McpTransport.Stdio)
            {
                entry["type"] = "stdio";
                entry["command"] = server.Command;
                entry["args"] = new JsonArray(server.Arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());

                if (server.Environment.Count > 0)
                {
                    var env = new JsonObject();
                    foreach (var pair in server.Environment)
                        env[pair.Key] = pair.Value;
                    entry["env"] = env;
                }
            }
            else
            {
                entry["type"] = server.Transport == McpTransport.Sse ? "sse" : "http";
                entry["url"] = server.Url;

                var headers = new JsonObject();
                foreach (var pair in server.Headers)
                    headers[pair.Key] = pair.Value;

                // Fetched or refreshed now, so the CLI starts with a token that is still good.
                if (await _mcpTokens.GetAccessTokenAsync(server, ct) is { Length: > 0 } token)
                {
                    var prefix = string.IsNullOrWhiteSpace(server.AuthHeaderPrefix) ? string.Empty : server.AuthHeaderPrefix + " ";
                    headers[string.IsNullOrWhiteSpace(server.AuthHeaderName) ? "Authorization" : server.AuthHeaderName] = prefix + token;
                }

                if (headers.Count > 0)
                    entry["headers"] = headers;
            }

            servers[server.Name] = entry;
        }

        var document = new JsonObject { ["mcpServers"] = servers };
        await File.WriteAllTextAsync(
            configPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return selected.Select(s => s.Name).ToList();
    }

    /// <summary>
    /// Adds the files this app stages into a workspace to that checkout's private exclude list, so
    /// they never show up as untracked work in an editor pointed at the same directory. It is the
    /// per-clone list, not .gitignore, so nothing is added to the repository itself.
    /// </summary>
    private async Task IgnoreStagedArtefactsAsync(string workspacePath, CancellationToken ct)
    {
        string[] patterns =
        [
            "/global-files/",
            "/project-files/",
            "/GUIDANCE.md",
            "/AGENTS.orchestrator.md",
            "/.mcp.json",
            "/.claude/skills/",
        ];

        try
        {
            // A worktree keeps its exclude file under the main repository, so git has to say where.
            var gitDir = await _git.RunAsync(workspacePath, ["rev-parse", "--absolute-git-dir"], ct);
            if (!gitDir.Succeeded || string.IsNullOrWhiteSpace(gitDir.Output))
                return;

            var infoPath = Path.Combine(gitDir.Output.Trim(), "info");
            Directory.CreateDirectory(infoPath);
            var excludePath = Path.Combine(infoPath, "exclude");

            var lines = File.Exists(excludePath)
                ? (await File.ReadAllLinesAsync(excludePath, ct)).ToList()
                : [];

            var missing = patterns.Where(p => !lines.Contains(p, StringComparer.Ordinal)).ToList();
            if (missing.Count == 0)
                return;

            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add("# devStudio: files staged into this workspace by the orchestrator.");
            lines.AddRange(missing);

            await File.WriteAllLinesAsync(excludePath, lines, ct);
        }
        catch (Exception ex)
        {
            // Cosmetic. A session that cannot write the exclude file still runs fine.
            _logger.LogWarning(ex, "Could not update the git exclude list for {Path}", workspacePath);
        }
    }

    /// <summary>
    /// Best-effort pull of the standards files repository before a new session materialises them.
    /// A network blip or a stale checkout must not stop a conversation from starting — the sync
    /// already reports its own failure onto <see cref="GlobalSettings"/> and never throws, but this
    /// is wrapped anyway so a defect there cannot become one here.
    /// </summary>
    private async Task TrySyncStandardsFilesAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _globals.GetAsync(GlobalSettings.WellKnownId, ct);
            if (string.IsNullOrWhiteSpace(settings?.FilesRepositoryId))
                return;

            await _standardsFiles.SyncAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standards files sync failed before session start");
        }
    }

    public async Task MaterialiseGlobalFilesAsync(string workspacePath, CancellationToken ct = default)
    {
        var settings = await _globals.GetAsync(GlobalSettings.WellKnownId, ct);
        if (settings is null || settings.Files.Count == 0)
            return;

        await CopyLibraryAsync(GlobalFilesPath(), Path.Combine(workspacePath, "global-files"));
    }

    public async Task MaterialiseProjectFilesAsync(string projectId, string workspacePath, CancellationToken ct = default)
    {
        await CopyLibraryAsync(ProjectFilesPath(projectId), Path.Combine(workspacePath, "project-files"));
    }

    private Task CopyLibraryAsync(string source, string target)
    {
        if (!Directory.Exists(source))
            return Task.CompletedTask;
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            try
            {
                // Only copy when the upload is newer, so agents can leave their own notes alongside.
                if (!File.Exists(destination) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destination))
                    File.Copy(file, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stage reference file {File}", file);
            }
        }

        return Task.CompletedTask;
    }

    public async Task WriteGuidanceAsync(
        string workspacePath,
        IEnumerable<GuidanceMessage> guidance,
        CancellationToken ct = default)
    {
        var outstanding = guidance
            .Where(g => g.Status != GuidanceStatus.Applied)
            .ToList();

        var path = Path.Combine(workspacePath, "GUIDANCE.md");

        try
        {
            if (outstanding.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);

                return;
            }

            var builder = new StringBuilder()
                .AppendLine("# Guidance")
                .AppendLine()
                .AppendLine("Course corrections sent after your current work started. They override earlier")
                .AppendLine("instructions where they conflict. Re-read this file if you have been running a while.")
                .AppendLine();

            foreach (var message in outstanding.OrderBy(g => g.CreatedAt))
            {
                builder.AppendLine($"## {message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} — {message.Source}");
                builder.AppendLine();
                builder.AppendLine(message.Text);
                builder.AppendLine();
            }

            await File.WriteAllTextAsync(path, builder.ToString(), ct);
        }
        catch (Exception ex)
        {
            // A steer that cannot be written to disk still reaches the agent over MCP and next turn.
            _logger.LogWarning(ex, "Could not write guidance to {Path}", path);
        }
    }

    public async Task<string> ComposeSystemPromptAsync(
        Agent agent,
        string? projectId,
        string? sessionId = null,
        TokenTactics tactics = TokenTactics.None,
        string? handoverModel = null,
        CancellationToken ct = default)
    {
        projectId ??= agent.ProjectId;
        var builder = new StringBuilder();

        var project = projectId is null ? null : await _projects.GetAsync(projectId, ct);

        // Least specific first: global standards, then the project, then the agent itself.
        if (project?.InheritGlobalInstructions ?? true)
        {
            var settings = await _globals.GetAsync(GlobalSettings.WellKnownId, ct);

            // The team's repository first, then whatever this install added on top of it: the local
            // rule is the narrower one, and the later line is the one a model treats as the override.
            if (settings is not null && !string.IsNullOrWhiteSpace(settings.TeamInstructions))
            {
                builder.AppendLine("# Team standards");
                builder.AppendLine("Shared by everyone working on this, from the team settings repository.");
                builder.AppendLine();
                builder.AppendLine(settings.TeamInstructions);
                builder.AppendLine();
            }

            if (settings is not null && !string.IsNullOrWhiteSpace(settings.Instructions))
            {
                builder.AppendLine("# Standards");
                builder.AppendLine("These apply to all work here unless a project says otherwise.");
                builder.AppendLine();
                builder.AppendLine(settings.Instructions);
                builder.AppendLine();
            }

            if (settings is not null && settings.Files.Count > 0)
            {
                builder.AppendLine("## Reference files");
                builder.AppendLine("In the ./global-files directory of your working directory:");
                foreach (var file in settings.Files)
                    builder.AppendLine($"- {file.FileName}");
                builder.AppendLine();
            }
        }

        if (project is not null)
        {
            builder.AppendLine($"# Project: {project.Name}");
            if (!string.IsNullOrWhiteSpace(project.Description))
                builder.AppendLine(project.Description);
            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(project.Instructions))
            {
                builder.AppendLine("## Project instructions");
                builder.AppendLine(project.Instructions);
                builder.AppendLine();
            }

            if (project.Files.Count > 0)
            {
                builder.AppendLine("## Project files");
                builder.AppendLine("These are available in the ./project-files directory of your working directory:");
                foreach (var file in project.Files)
                    builder.AppendLine($"- {file.FileName}");
                builder.AppendLine();
            }
        }

        builder.AppendLine("## Local tools");
        builder.AppendLine(
            "Work through the command line tools installed here rather than looking for a hosted " +
            "connector. `git`, `gh` (GitHub) and `glab` (GitLab) are on your PATH and already " +
            "signed in with this container's credentials, so they need no token from you.");
        builder.AppendLine();
        builder.AppendLine(
            "For a GitLab merge request that means `glab mr view <id>`, `glab mr diff <id>`, " +
            "`glab mr list`; GitHub is the same shape through `gh`. If one of them reports being " +
            "signed out, say so rather than reaching for another route — a human fixes that on the " +
            "Logins page.");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
        {
            builder.AppendLine("## Agent instructions");
            builder.AppendLine(agent.SystemPrompt);
            builder.AppendLine();
        }

        // After the instructions it qualifies and before guidance, which overrides everything: how
        // to go about the work, once what the work is has been settled.
        if (TokenMinimisation.Compose(tactics) is { Length: > 0 } minimisation)
        {
            builder.AppendLine(minimisation);
        }

        // Only offered when there is somewhere cheaper to go: an agent told it can hand over, on a
        // conversation with no second model, would spend the marker on nothing.
        if (!string.IsNullOrWhiteSpace(handoverModel))
        {
            builder.AppendLine("## Changing model");
            builder.AppendLine(
                $"This conversation can move to `{handoverModel}`, which costs less than what you "
                + "are running on now. When the work left is mechanical — carrying out an approach "
                + "already settled, applying a decision already made — write `[CHANGE MODEL]` in "
                + "your answer and the change takes effect from the next turn.");
            builder.AppendLine();
            builder.AppendLine(
                "Ask only once the thinking is done: the model that takes over is there to carry "
                + "the plan out, not to revisit it, and nothing you say afterwards moves the "
                + "conversation back. Say in a line what is left to do, so it starts from something "
                + "rather than from the marker alone.");
            builder.AppendLine();
        }

        builder.AppendLine("## Ending the conversation");
        builder.AppendLine(
            "When the task is genuinely finished and nothing further is expected from either side, "
            + "write `[END CONVERSATION]` in your answer. The session closes once your answer has "
            + "finished streaming and becomes read only from then on, so say what was done and why "
            + "it is complete before writing the marker — not after.");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            builder.AppendLine("## Guidance");
            builder.AppendLine(
                $"Your orchestrator session id is `{sessionId}`. While you work, a human or a managing " +
                "agent can send you a course correction. On any long task, check for one before you " +
                "commit to an approach and again before you finish:");
            builder.AppendLine();
            builder.AppendLine("- Read `./GUIDANCE.md` in your working directory if it exists.");
            builder.AppendLine(
                $"- If the `orchestrator` MCP server is available, call `check_guidance` with " +
                $"sessionId `{sessionId}`.");
            builder.AppendLine();
            builder.AppendLine("Guidance overrides earlier instructions where the two conflict.");
        }

        return builder.ToString().Trim();
    }

    /// <summary>Working directory for project-scoped sessions with no repository.</summary>
    public string ProjectWorkspacePath(string projectId) =>
        Path.Combine(_options.DataPath, "projects", projectId, "workspace");

    /// <summary>Where project uploads are stored.</summary>
    public string ProjectFilesPath(string projectId) =>
        Path.Combine(_options.DataPath, "projects", projectId, "files");

    /// <summary>Where the global library is stored.</summary>
    public string GlobalFilesPath() => Path.Combine(_options.DataPath, "global", "files");

    /// <summary>
    /// Recursive copy, for skill bundles — unlike the reference libraries a skill's files are a
    /// tree, and its instructions name the subfolders.
    /// </summary>
    private void CopyTree(string source, string target)
    {
        if (!Directory.Exists(source))
            return;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stage skill file {File}", file);
            }
        }
    }

    private static string EscapeYaml(string value) =>
        value.Contains(':') || value.Contains('#') ? $"\"{value.Replace("\"", "'")}\"" : value;
}
