using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Users;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Skills;
using DevStudio.Domain.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Seed;

/// <summary>
/// Creates the storage layout on first boot and drops in one agent per provider plus an example
/// skill and workflow, so a fresh volume is not an empty screen.
/// </summary>
public sealed class SeedHostedService : IHostedService
{
    private readonly IEntityStore<Agent> _agents;
    private readonly IEntityStore<Skill> _skills;
    private readonly IEntityStore<Workflow> _workflows;
    private readonly IEntityStore<McpServer> _mcpServers;
    private readonly IEntityStore<ProviderAccount> _accounts;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly IUserService _users;
    private readonly ISourceControlHosts _hosts;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SeedHostedService> _logger;

    public SeedHostedService(
        IEntityStore<Agent> agents,
        IEntityStore<Skill> skills,
        IEntityStore<Workflow> workflows,
        IEntityStore<McpServer> mcpServers,
        IEntityStore<ProviderAccount> accounts,
        IEntityStore<GlobalSettings> globals,
        IUserService users,
        ISourceControlHosts hosts,
        IOptions<OrchestratorOptions> options,
        ILogger<SeedHostedService> logger)
    {
        _agents = agents;
        _skills = skills;
        _workflows = workflows;
        _mcpServers = mcpServers;
        _accounts = accounts;
        _globals = globals;
        _users = users;
        _hosts = hosts;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[]
                 {
                     _options.DataPath,
                     _options.RepositoriesPath,
                     _options.WorktreesPath,
                     _options.ScratchPath,
                     Path.Combine(_options.DataPath, "projects"),
                     Path.Combine(_options.DataPath, "global", "files"),
                     Path.Combine(_options.HomePath, ".claude"),
                     Path.Combine(_options.HomePath, ".codex"),
                 })
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create {Path}", path);
            }
        }

        // Before anything else: with no account the sign-in page has nothing to accept, and the app
        // is unreachable rather than merely empty.
        await _users.EnsureSeedAccountAsync(cancellationToken);

        if ((await _accounts.GetAllAsync(cancellationToken)).Count == 0)
            await SeedAccountsAsync(cancellationToken);

        if (await _globals.GetAsync(GlobalSettings.WellKnownId, cancellationToken) is null)
            await SeedStandardsAsync(cancellationToken);

        if ((await _agents.GetAllAsync(cancellationToken)).Count == 0)
            await SeedAgentsAsync(cancellationToken);

        if ((await _skills.GetAllAsync(cancellationToken)).Count == 0)
            await SeedSkillsAsync(cancellationToken);

        // The forge hostnames must be in memory before anything shells out to gh or glab.
        await _hosts.LoadAsync(cancellationToken);

        // Checked every start, not just first boot: agents lose their view of each other without it.
        await EnsureOrchestratorMcpAsync(cancellationToken);

        if ((await _workflows.GetAllAsync(cancellationToken)).Count == 0)
            await SeedWorkflowAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// One account per provider pointing at the container home, so any login that already exists
    /// keeps working. Extra accounts get their own directory when you add them.
    /// </summary>
    private async Task SeedAccountsAsync(CancellationToken ct)
    {
        foreach (var provider in new[] { AiProvider.Claude, AiProvider.Codex })
        {
            await _accounts.UpsertAsync(new ProviderAccount
            {
                Name = "Default",
                Description = $"The {provider} login held in the container home directory.",
                Provider = provider,
                HomePath = _options.HomePath,
                IsDefault = true,
            }, ct);
        }
    }

    private async Task SeedStandardsAsync(CancellationToken ct)
    {
        await _globals.UpsertAsync(new GlobalSettings
        {
            Instructions = """
                           Read the existing code before changing it and match what is already there.
                           Keep changes focused on what was asked. Run the project's tests before you
                           claim something works, and say plainly if they fail.
                           """,
        }, ct);
    }

    private async Task SeedAgentsAsync(CancellationToken ct)
    {
        await _agents.UpsertAsync(new Agent
        {
            Name = "Claude builder",
            Description = "General-purpose implementation agent driving the claude CLI.",
            Provider = AiProvider.Claude,
            PermissionMode = PermissionMode.AcceptEdits,
            SystemPrompt = "You are working inside an isolated git worktree. Make focused changes and explain what you did.",
            Accent = "cyan",
        }, ct);

        await _agents.UpsertAsync(new Agent
        {
            Name = "Codex reviewer",
            Description = "Read-only reviewer driving the codex CLI.",
            Provider = AiProvider.Codex,
            PermissionMode = PermissionMode.Plan,
            SystemPrompt = "Review the code you are given. Report concrete problems with file and line references. Do not edit files.",
            Accent = "magenta",
        }, ct);
    }

    private async Task SeedSkillsAsync(CancellationToken ct)
    {
        await _skills.UpsertAsync(new Skill
        {
            Name = "Conventional commits",
            Slug = "conventional-commits",
            Description = "Use when writing commit messages or opening a pull request.",
            Content = """
                      Write commit subjects as `type(scope): summary` using the conventional commit types
                      (feat, fix, docs, refactor, test, chore). Keep the subject under 72 characters and
                      explain the reasoning in the body, not the mechanics of the diff.

                      When opening a PR with `gh pr create`, mirror the same summary in the title and put
                      the reasoning plus a test plan in the body.
                      """,
            Tags = ["git", "workflow"],
        }, ct);
    }

    /// <summary>
    /// Makes sure both built-in servers exist and point at the right port. Neither can be deleted
    /// from the UI, and if one goes missing some other way it comes back on the next start.
    /// </summary>
    private async Task EnsureOrchestratorMcpAsync(CancellationToken ct)
    {
        await EnsureBuiltInMcpAsync(
            "orchestrator",
            "This orchestrator: list agents and sessions, read another session's transcript, steer a run, leave notes, start work.",
            "/mcp",
            isDefault: false,
            ct);

        // Present but not attached: most sessions are not drawing anything, and a tool nobody asked
        // for still costs context and still tempts a model into claiming it made a picture. Tick it
        // on the agent that needs it. It is a separate server rather than a flag on the one above so
        // that being able to draw does not also mean being able to start and steer other sessions.
        await EnsureBuiltInMcpAsync(
            "images",
            "Generate an image from a description. Backed by whichever image service is configured on the Logins page.",
            "/mcp/images",
            isDefault: false,
            ct);
    }

    private async Task EnsureBuiltInMcpAsync(
        string name,
        string description,
        string path,
        bool isDefault,
        CancellationToken ct)
    {
        var url = $"http://localhost:{_options.HttpPort}{path}";

        // Matched by name, not just by the built-in flag: there is more than one of these now.
        var existing = (await _mcpServers.GetAllAsync(ct))
            .FirstOrDefault(s => s.IsBuiltIn && s.Name == name);

        if (existing is not null)
        {
            // Repair only what must stay true; the user's own choices — including whether this one
            // is still attached by default — are left alone.
            if (existing.Url == url && existing.Transport == McpTransport.Http)
                return;

            existing.Url = url;
            existing.Transport = McpTransport.Http;
            await _mcpServers.UpsertAsync(existing, ct);
            _logger.LogInformation("Repaired the built-in {Name} MCP server", name);
            return;
        }

        await _mcpServers.UpsertAsync(new McpServer
        {
            Name = name,
            Description = description,
            Transport = McpTransport.Http,
            Url = url,
            IsBuiltIn = true,
            IsDefault = isDefault,
            Enabled = true,
        }, ct);

        _logger.LogInformation("Restored the built-in {Name} MCP server", name);
    }

    private async Task SeedWorkflowAsync(CancellationToken ct)
    {
        var agents = await _agents.GetAllAsync(ct);
        var builder = agents.FirstOrDefault(a => a.Provider == AiProvider.Claude);
        var reviewer = agents.FirstOrDefault(a => a.Provider == AiProvider.Codex);

        if (builder is null || reviewer is null)
            return;

        await _workflows.UpsertAsync(new Workflow
        {
            Name = "Build then review",
            Description = "One agent implements the change, a second reviews it, and the first applies the review.",
            Inputs =
            [
                new WorkflowInput { Name = "task", Description = "What needs building", Required = true },
            ],
            Steps =
            [
                new WorkflowStep
                {
                    Name = "Implement",
                    Order = 1,
                    AgentId = builder.Id,
                    PromptTemplate = "{{task}}\n\nImplement this, then summarise every file you changed.",
                },
                new WorkflowStep
                {
                    Name = "Review",
                    Order = 2,
                    AgentId = reviewer.Id,
                    PromptTemplate = "A colleague made this change:\n\n{{steps.implement}}\n\nReview the working tree and list anything that is wrong or risky.",
                },
                new WorkflowStep
                {
                    Name = "Fix",
                    Order = 3,
                    AgentId = builder.Id,
                    PromptTemplate = "Original task: {{task}}\n\nReview feedback:\n\n{{steps.review}}\n\nAddress every valid point, then commit the result.",
                },
            ],
        }, ct);
    }
}
