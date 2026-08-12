using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Agents;
using AiShop.Domain.Globals;
using AiShop.Domain.Mcp;
using AiShop.Domain.Providers;
using AiShop.Domain.Skills;
using AiShop.Domain.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiShop.Infrastructure.Seed;

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
    /// Makes sure the built-in orchestrator server exists and points at the right port. It cannot be
    /// deleted from the UI, and if it goes missing some other way it comes back on the next start.
    /// </summary>
    private async Task EnsureOrchestratorMcpAsync(CancellationToken ct)
    {
        var url = $"http://localhost:{_options.HttpPort}/mcp";
        var existing = (await _mcpServers.GetAllAsync(ct)).FirstOrDefault(s => s.IsBuiltIn);

        if (existing is not null)
        {
            // Repair only what must stay true; the user's own choices are left alone.
            if (existing.Url == url && existing.Transport == McpTransport.Http)
                return;

            existing.Url = url;
            existing.Transport = McpTransport.Http;
            await _mcpServers.UpsertAsync(existing, ct);
            _logger.LogInformation("Repaired the built-in orchestrator MCP server");
            return;
        }

        await _mcpServers.UpsertAsync(new McpServer
        {
            Name = "orchestrator",
            Description = "This orchestrator: list agents and sessions, read another session's transcript, steer a run, leave notes, start work.",
            Transport = McpTransport.Http,
            Url = url,
            IsBuiltIn = true,
            IsDefault = false,
            Enabled = true,
        }, ct);

        _logger.LogInformation("Restored the built-in orchestrator MCP server");
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
