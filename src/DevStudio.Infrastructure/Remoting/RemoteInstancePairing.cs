using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// The dialling half of the handshake. Plain HTTP rather than the hub, because none of this can use
/// the hub: there is no key yet, and the hub is the thing the key is for.
///
/// The shape is deliberately a request that waits. A pairing code typed into both machines would be
/// fewer steps, but it would also mean the machine being connected to never shows anyone what asked
/// for access — and that screen, with a name and an address on it, is the moment somebody gets to
/// notice a request they did not make.
/// </summary>
public sealed class RemoteInstancePairing : IRemoteInstancePairing
{
    private readonly IEntityStore<RemoteInstance> _instances;
    private readonly IHttpClientFactory _httpClients;
    private readonly IRemoteConnectionPool _pool;
    private readonly ILogger<RemoteInstancePairing> _logger;

    public RemoteInstancePairing(
        IEntityStore<RemoteInstance> instances,
        IHttpClientFactory httpClients,
        IRemoteConnectionPool pool,
        ILogger<RemoteInstancePairing> logger)
    {
        _instances = instances;
        _httpClients = httpClients;
        _pool = pool;
        _logger = logger;
    }

    public async Task<RemoteInstance> RequestAccessAsync(string instanceId, CancellationToken ct = default)
    {
        var instance = await Required(instanceId, ct);
        var client = Client();

        try
        {
            var response = await client.PostAsJsonAsync(
                Url(instance, RemotePairingRoutes.Request),
                new RemotePairingRequest(instance.Id, LocalName(), Environment.MachineName, Version()),
                ct);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RemotePairingResponse>(ct)
                         ?? throw new InvalidOperationException("That instance answered the request with nothing.");

            instance.PairingRequestId = result.RequestId;
            instance.VerificationCode = result.VerificationCode;
            instance.State = RemoteInstanceState.AwaitingApproval;
            instance.LastError = null;

            _logger.LogInformation("Requested access to {Instance} at {Url}", instance.Name, instance.BaseUrl);
        }
        catch (Exception ex)
        {
            instance.State = RemoteInstanceState.Unpaired;
            instance.LastError = Describe(ex, instance);
        }

        return await _instances.UpsertAsync(instance, ct);
    }

    public async Task<RemoteInstance> PollAsync(string instanceId, CancellationToken ct = default)
    {
        var instance = await Required(instanceId, ct);

        if (string.IsNullOrWhiteSpace(instance.PairingRequestId))
            return instance;

        var client = Client();

        try
        {
            var path = RemotePairingRoutes.Status.Replace("{requestId}", Uri.EscapeDataString(instance.PairingRequestId));
            var response = await client.GetAsync(Url(instance, path), ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The far side has forgotten the request — most likely it was cleaned up, or its
                // volume was reset. Sending the operator round the loop again is the honest answer.
                instance.State = RemoteInstanceState.Unpaired;
                instance.PairingRequestId = null;
                instance.VerificationCode = null;
                instance.LastError = "That request is no longer known to the other instance. Request access again.";

                return await _instances.UpsertAsync(instance, ct);
            }

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RemotePairingResponse>(ct)
                         ?? throw new InvalidOperationException("That instance answered with nothing.");

            switch (result.Status)
            {
                case "approved" when !string.IsNullOrWhiteSpace(result.Token):
                    instance.AccessToken = result.Token;
                    instance.TokenExpiresAt = result.ExpiresAt;
                    instance.HostName = result.HostName;
                    instance.HostVersion = result.HostVersion;
                    instance.State = RemoteInstanceState.Connected;
                    instance.LastConnectedAt = DateTimeOffset.UtcNow;
                    instance.PairingRequestId = null;
                    instance.VerificationCode = null;
                    instance.LastError = null;

                    // Anything held from before the key arrived was unauthenticated and is no use.
                    await _pool.DropAsync(instance.Id);

                    _logger.LogInformation("Paired with {Instance}", instance.Name);
                    break;

                case "denied":
                    instance.State = RemoteInstanceState.Denied;
                    instance.PairingRequestId = null;
                    instance.VerificationCode = null;
                    instance.LastError = result.Detail ?? "The other instance refused the request.";
                    break;

                default:
                    instance.State = RemoteInstanceState.AwaitingApproval;
                    instance.VerificationCode = result.VerificationCode;
                    break;
            }
        }
        catch (Exception ex)
        {
            instance.LastError = Describe(ex, instance);
        }

        return await _instances.UpsertAsync(instance, ct);
    }

    public async Task<RemoteInstance> TestAsync(string instanceId, CancellationToken ct = default)
    {
        var instance = await Required(instanceId, ct);

        if (string.IsNullOrWhiteSpace(instance.AccessToken))
        {
            instance.State = RemoteInstanceState.Unpaired;
            instance.LastError = "Not paired yet.";

            return await _instances.UpsertAsync(instance, ct);
        }

        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instance.AccessToken);

        try
        {
            var response = await client.GetAsync(Url(instance, RemotePairingRoutes.Hello), ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Revoked over there, or the far side's signing key is gone. Either way the key we
                // hold is dead and the only way back is another approval.
                instance.State = RemoteInstanceState.Denied;
                instance.LastError = "That instance no longer accepts this key. Request access again.";

                return await _instances.UpsertAsync(instance, ct);
            }

            response.EnsureSuccessStatusCode();

            var hello = await response.Content.ReadFromJsonAsync<RemoteHelloResponse>(ct);

            instance.HostName = hello?.HostName;
            instance.HostVersion = hello?.HostVersion;
            instance.TokenExpiresAt = hello?.ExpiresAt ?? instance.TokenExpiresAt;
            instance.State = RemoteInstanceState.Connected;
            instance.LastConnectedAt = DateTimeOffset.UtcNow;
            instance.LastError = null;
        }
        catch (Exception ex)
        {
            instance.State = RemoteInstanceState.Disconnected;
            instance.LastError = Describe(ex, instance);
        }

        return await _instances.UpsertAsync(instance, ct);
    }

    public async Task<RemoteInstance> ForgetAsync(string instanceId, CancellationToken ct = default)
    {
        var instance = await Required(instanceId, ct);

        instance.AccessToken = null;
        instance.TokenExpiresAt = null;
        instance.PairingRequestId = null;
        instance.VerificationCode = null;
        instance.State = RemoteInstanceState.Unpaired;
        instance.LastError = null;

        await _pool.DropAsync(instance.Id);

        return await _instances.UpsertAsync(instance, ct);
    }

    private HttpClient Client()
    {
        var client = _httpClients.CreateClient();

        // Short. Every one of these calls is a small local-network request, and a minute of a
        // spinner because the far machine is asleep is a worse answer than an error.
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("devStudio");

        return client;
    }

    private static string Url(RemoteInstance instance, string path) =>
        $"{instance.BaseUrl.TrimEnd('/')}{path}";

    /// <summary>
    /// What this instance calls itself to the far side. There is no configured name for the local
    /// installation, and the machine name is the thing an operator will recognise on the approval
    /// screen anyway.
    /// </summary>
    private static string LocalName() => Environment.MachineName;

    private static string? Version() =>
        typeof(RemoteInstancePairing).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private async Task<RemoteInstance> Required(string id, CancellationToken ct) =>
        await _instances.GetAsync(id, ct) ?? throw new InvalidOperationException("That remote instance no longer exists.");

    /// <summary>
    /// Connection faults arrive wrapped several deep and read as "An error occurred while sending
    /// the request", which tells an operator nothing about a machine that is simply off.
    /// </summary>
    private static string Describe(Exception ex, RemoteInstance instance)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        return $"Could not reach {instance.BaseUrl}: {inner.Message}";
    }
}
