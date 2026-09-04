using System.Reflection;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Remoting;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Skills;
using DevStudio.Application.Common;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// This machine. Everything is the service that was already registered — the point of the interface
/// is that the local path stays exactly what it was, and remoting is the special case rather than
/// the other way round.
/// </summary>
public sealed class LocalExecutionHost : IExecutionHost
{
    private readonly IEntityStore<McpServer> _mcpServers;
    private readonly IEntityStore<Skill> _skills;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly IEntityStore<ProviderAccount> _accounts;
    private readonly IEntityStore<CliProvider> _cliProviders;
    private readonly IGitService _git;
    private readonly OrchestratorOptions _options;

    public LocalExecutionHost(
        IProviderCliRegistry clis,
        IWorkspaceService workspaces,
        IAccountService accounts,
        IWorkspaceFileService files,
        ITerminalService terminals,
        IEntityStore<McpServer> mcpServers,
        IEntityStore<Skill> skills,
        IEntityStore<GitRepository> repositories,
        IEntityStore<ProviderAccount> providerAccounts,
        IEntityStore<CliProvider> cliProviders,
        IGitService git,
        IOptions<OrchestratorOptions> options)
    {
        Clis = clis;
        Workspaces = workspaces;
        Accounts = accounts;
        Files = files;
        Terminals = terminals;
        _mcpServers = mcpServers;
        _skills = skills;
        _repositories = repositories;
        _accounts = providerAccounts;
        _cliProviders = cliProviders;
        _git = git;
        _options = options.Value;
    }

    public string? RemoteInstanceId => null;
    public string DisplayName => "This machine";

    public IProviderCliRegistry Clis { get; }
    public IWorkspaceService Workspaces { get; }
    public IAccountService Accounts { get; }
    public IWorkspaceFileService Files { get; }
    public ITerminalService Terminals { get; }

    public async Task<RemoteHostConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var cliProviders = await _cliProviders.GetAllAsync(ct);
        var clis = await Clis.GetAllAsync(ct);

        var descriptors = new List<RemoteCliDescriptor>();

        foreach (var cli in clis)
        {
            descriptors.Add(new RemoteCliDescriptor(
                cli.Provider,
                cli.DefinitionId,
                cli.DisplayName,
                await ModelsFor(cli, cliProviders, ct),
                EffortsFor(cli, cliProviders)));
        }

        return new RemoteHostConfig(
            Environment.MachineName,
            HostVersion(),
            descriptors,
            (await _mcpServers.GetAllAsync(ct))
                .Where(s => s.Enabled)
                .Select(s => new RemoteNamedItem(s.Id, s.Name, s.IsDefault))
                .ToList(),
            (await _skills.GetAllAsync(ct))
                .Select(s => new RemoteNamedItem(s.Id, s.Name))
                .ToList(),
            (await _repositories.GetAllAsync(ct))
                .Select(r => new RemoteNamedItem(r.Id, r.Name, false, r.DefaultBranch))
                .ToList(),
            // Detail carries which CLI the login belongs to, so the account picker can be filtered
            // without a second round trip. A user-defined CLI is keyed by its definition id, since
            // several of them are all "Custom" and their logins are not interchangeable.
            (await _accounts.GetAllAsync(ct))
                .Select(a => new RemoteNamedItem(
                    a.Id,
                    a.Name,
                    a.IsDefault,
                    a.CliProviderId is { Length: > 0 } cli ? $"Custom:{cli}" : a.Provider.ToString()))
                .ToList(),
            OperatingSystem.IsWindows());
    }

    /// <summary>Read the way the sidebar reads it, so both agree on what is running.</summary>
    private static string? HostVersion() =>
        typeof(LocalExecutionHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LocalExecutionHost).Assembly.GetName().Version?.ToString(3);

    public async Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryId, CancellationToken ct = default)
    {
        var repository = await _repositories.GetAsync(repositoryId, ct);

        return repository is null ? [] : await _git.ListBranchesAsync(repository, ct);
    }

    /// <summary>
    /// Same rule the pickers already used: whatever the CLI can be asked for live, then the
    /// configured suggestions it did not already mention.
    /// </summary>
    private async Task<IReadOnlyList<string>> ModelsFor(
        IProviderCli cli,
        IReadOnlyList<CliProvider> custom,
        CancellationToken ct)
    {
        var configured = Configured(cli, custom, static (o, p) => p switch
        {
            AiProvider.Claude => o.ClaudeModels,
            AiProvider.Codex => o.CodexModels,
            AiProvider.Opencode => o.OpencodeModels,
            _ => [],
        }, static c => c.Models);

        try
        {
            var live = await cli.GetAvailableModelsAsync(ct);

            return live.Count == 0
                ? configured
                : live.Concat(configured.Where(m => !live.Contains(m, StringComparer.OrdinalIgnoreCase))).ToList();
        }
        catch
        {
            // A CLI that is not installed or not running should narrow the list, not break the page.
            return configured;
        }
    }

    private IReadOnlyList<string> EffortsFor(IProviderCli cli, IReadOnlyList<CliProvider> custom) =>
        Configured(cli, custom, static (o, p) => p switch
        {
            AiProvider.Claude => o.ClaudeEfforts,
            AiProvider.Codex => o.CodexEfforts,
            AiProvider.Opencode => o.OpencodeEfforts,
            _ => [],
        }, static c => c.Efforts);

    private IReadOnlyList<string> Configured(
        IProviderCli cli,
        IReadOnlyList<CliProvider> custom,
        Func<OrchestratorOptions, AiProvider, IReadOnlyList<string>> builtIn,
        Func<CliProvider, IReadOnlyList<string>> defined) =>
        cli.DefinitionId is { Length: > 0 } id
            ? custom.FirstOrDefault(c => c.Id == id) is { } definition ? defined(definition) : []
            : builtIn(_options, cli.Provider);
}

/// <summary>
/// Another machine, reached over its hub. Each service here is a proxy that turns a call into a hub
/// invocation; nothing is executed on this side.
/// </summary>
public sealed class RemoteExecutionHost : IExecutionHost
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;
    private readonly ISharedEnvironment? _shared;

    /// <summary>
    /// Cached because every picker on a page asks for it and it is a round trip to another machine.
    /// Short-lived so a CLI installed or an account added over there shows up without a restart.
    /// </summary>
    private static readonly TimeSpan ConfigFreshness = TimeSpan.FromSeconds(30);

    private RemoteHostConfig? _config;
    private DateTimeOffset _configAt;
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public RemoteExecutionHost(
        RemoteInstance instance,
        IRemoteConnectionPool pool,
        IWorkspaceService localWorkspaces,
        ILogger logger,
        ISharedEnvironment? shared = null)
    {
        _instance = instance;
        _pool = pool;
        _shared = shared;

        Workspaces = new RemoteWorkspaceService(instance, pool, localWorkspaces);
        Accounts = new RemoteAccountService(instance, pool);
        Files = new RemoteWorkspaceFileService(instance, pool);
        Terminals = new RemoteTerminalService(instance, pool, logger);
    }

    public string? RemoteInstanceId => _instance.Id;
    public string DisplayName => _instance.Name;

    public IWorkspaceService Workspaces { get; }
    public IAccountService Accounts { get; }
    public IWorkspaceFileService Files { get; }
    public ITerminalService Terminals { get; }

    /// <summary>
    /// Built from the config, so resolving a CLI needs the far side to have answered once. Every
    /// caller has already awaited <see cref="GetConfigAsync"/> by the time it picks one.
    /// </summary>
    public IProviderCliRegistry Clis =>
        new RemoteProviderCliRegistry(
            _instance,
            _pool,
            _config ?? throw new InvalidOperationException($"'{_instance.Name}' has not been asked what it offers yet."),
            _shared);

    public async Task<RemoteHostConfig> GetConfigAsync(CancellationToken ct = default)
    {
        await _configGate.WaitAsync(ct);
        try
        {
            if (_config is not null && DateTimeOffset.UtcNow - _configAt < ConfigFreshness)
                return _config;

            var connection = await _pool.GetAsync(_instance, ct);
            _config = await connection.InvokeAsync<RemoteHostConfig>(RemoteHubMethods.GetConfig, ct);
            _configAt = DateTimeOffset.UtcNow;

            return _config;
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryId, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        return await connection.InvokeAsync<IReadOnlyList<string>>(RemoteHubMethods.GetBranches, repositoryId, ct);
    }
}
