using System.Runtime.CompilerServices;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Application.Remoting;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Remoting;
using Microsoft.AspNetCore.SignalR.Client;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// A CLI that lives on another machine. Everything about it is the far side's — the executable, the
/// login it runs as, the directory it works in — and this is only the wire between the local
/// orchestrator and that.
///
/// The turn streams. Events are yielded here in the order the remote produced them, so the
/// transcript fills in as the CLI works and a remote conversation is indistinguishable from a local
/// one to watch.
/// </summary>
public sealed class RemoteProviderCli : IProviderCli
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;
    private readonly RemoteCliDescriptor _descriptor;
    private readonly ISharedEnvironment? _shared;

    public RemoteProviderCli(
        RemoteInstance instance,
        IRemoteConnectionPool pool,
        RemoteCliDescriptor descriptor,
        ISharedEnvironment? shared = null)
    {
        _instance = instance;
        _pool = pool;
        _descriptor = descriptor;
        _shared = shared;
    }

    public AiProvider Provider => _descriptor.Provider;

    /// <summary>
    /// Named for where it runs as well as what it is. A picker holding two instances' CLIs would
    /// otherwise offer two identical "Claude Code" entries.
    /// </summary>
    public string DisplayName => $"{_descriptor.DisplayName} · {_instance.Name}";

    public string? DefinitionId => _descriptor.CliProviderId;

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        HubConnection connection;

        // Connecting is the one failure worth reporting as a transcript entry rather than an
        // exception: the session is already open and the operator is watching it, and "the desk
        // machine is asleep" belongs where they are looking.
        AgentEvent? failure = null;
        try
        {
            connection = await _pool.GetAsync(_instance, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = AgentEvent.Error(ex.Message);
            connection = null!;
        }

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        // Only the variables somebody has marked as shareable travel with the turn. The far side
        // layers its own shared environment underneath these, so a machine that already has the
        // variable set locally needs nothing sent at all — which is the preferred arrangement.
        var stream = connection.StreamAsync<AgentEvent>(
            RemoteHubMethods.RunTurn,
            _descriptor.Provider,
            _descriptor.CliProviderId,
            await WithSharedEnvironmentAsync(request, ct),
            ct);

        await foreach (var evt in stream.WithCancellation(ct))
            yield return evt;
    }

    /// <summary>
    /// The turn as it goes over the wire: shareable variables underneath, so anything the agent or
    /// the turn itself set still wins.
    /// </summary>
    private async Task<TurnRequest> WithSharedEnvironmentAsync(TurnRequest request, CancellationToken ct)
    {
        if (_shared is null)
            return request;

        var shared = await _shared.ForRemoteAsync(ct);
        if (shared.Count == 0)
            return request;

        var environment = new Dictionary<string, string>(shared);
        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;

        return request with { Environment = environment };
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        try
        {
            var connection = await _pool.GetAsync(_instance, ct);

            return await connection.InvokeAsync<ProviderAuthStatus>(
                RemoteHubMethods.GetAuthStatus,
                _descriptor.Provider,
                _descriptor.CliProviderId,
                homePath,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProviderAuthStatus.Unknown(_descriptor.Provider, $"{_instance.Name} is unreachable: {ex.Message}");
        }
    }

    /// <summary>
    /// Already resolved: the descriptor was built from the remote's own answer, which for the CLIs
    /// that can be asked live is the same call this would have made.
    /// </summary>
    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(_descriptor.Models);

    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

    /// <summary>
    /// Logging in happens where the credentials land, which is over there. Offering it here would
    /// start a browser flow on the wrong machine and write the credential to the wrong home.
    /// </summary>
    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
        throw new InvalidOperationException(
            $"Sign in to this CLI on {_instance.Name} itself — its logins live on that machine.");

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() =>
        throw new InvalidOperationException(
            $"Sign out of this CLI on {_instance.Name} itself — its logins live on that machine.");
}

/// <summary>
/// The remote's CLI list, presented as a registry so the session manager resolves a CLI the same way
/// whichever machine it is going to run on.
/// </summary>
public sealed class RemoteProviderCliRegistry : IProviderCliRegistry
{
    private readonly IReadOnlyList<RemoteProviderCli> _clis;

    public RemoteProviderCliRegistry(
        RemoteInstance instance,
        IRemoteConnectionPool pool,
        RemoteHostConfig config,
        ISharedEnvironment? shared = null)
    {
        _clis = config.Clis.Select(d => new RemoteProviderCli(instance, pool, d, shared)).ToList();
        All = _clis.Where(c => c.DefinitionId is null).Cast<IProviderCli>().ToList();
    }

    public IReadOnlyList<IProviderCli> All { get; }

    public IProviderCli Get(AiProvider provider) =>
        _clis.FirstOrDefault(c => c.Provider == provider && c.DefinitionId is null)
        ?? throw new InvalidOperationException($"That instance does not offer {provider}.");

    public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default)
    {
        var match = provider == AiProvider.Custom
            ? _clis.FirstOrDefault(c => c.DefinitionId == cliProviderId)
            : _clis.FirstOrDefault(c => c.Provider == provider && c.DefinitionId is null);

        return match is null
            ? throw new InvalidOperationException("That instance no longer offers the CLI this agent uses.")
            : Task.FromResult<IProviderCli>(match);
    }

    public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IProviderCli>>(_clis.Cast<IProviderCli>().ToList());
}
