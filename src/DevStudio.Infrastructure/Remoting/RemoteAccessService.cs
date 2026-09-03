using System.Security.Cryptography;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// The approving side of pairing. Nothing here grants access on its own — a request arrives and sits
/// there until a person at this machine says yes, because that click is the only thing between
/// anything that can reach the port and an agent running commands on this filesystem.
/// </summary>
public sealed class RemoteAccessService : IRemoteAccessService
{
    /// <summary>
    /// How long an approved key stays collectable. The requester is polling every couple of seconds,
    /// so this only has to outlast a click; keeping it short means an approval that was never
    /// collected — because the requesting machine went away — does not leave a usable key sitting in
    /// a response nobody is watching.
    /// </summary>
    private static readonly TimeSpan CollectionWindow = TimeSpan.FromMinutes(10);

    private readonly IEntityStore<RemoteAccessGrant> _grants;
    private readonly IRemoteTokenIssuer _tokens;
    private readonly ILogger<RemoteAccessService> _logger;

    public RemoteAccessService(
        IEntityStore<RemoteAccessGrant> grants,
        IRemoteTokenIssuer tokens,
        ILogger<RemoteAccessService> logger)
    {
        _grants = grants;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<RemoteAccessGrant> LodgeAsync(
        RemotePairingRequest request,
        string remoteAddress,
        CancellationToken ct = default)
    {
        var all = await _grants.GetAllAsync(ct);

        // A re-request from an instance already approved is answered with the grant it has rather
        // than a second one, so re-pairing after the far side lost its key does not leave a trail of
        // stale grants nobody can tell apart. It still has to be approved again — the key is minted
        // fresh — because the far side proving it is the same install is not the same as proving it
        // is still trusted.
        var existing = all.FirstOrDefault(g =>
            g.RemoteInstanceId == request.InstanceId &&
            g.Status is RemoteGrantStatus.Pending or RemoteGrantStatus.Approved);

        var grant = existing ?? new RemoteAccessGrant();

        grant.InstanceName = request.InstanceName;
        grant.MachineName = request.MachineName;
        grant.RemoteInstanceId = request.InstanceId;
        grant.RemoteAddress = remoteAddress;
        grant.Version = request.Version;
        grant.Status = RemoteGrantStatus.Pending;
        grant.VerificationCode = NewCode();
        grant.DecidedAt = null;
        grant.ExpiresAt = null;

        // Restarting the clock matters: a grant reused from an earlier request would otherwise be
        // judged an expired request the moment it was lodged.
        grant.CreatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Remote access requested by {Instance} ({Machine}) from {Address}",
            grant.InstanceName,
            grant.MachineName,
            remoteAddress);

        return await _grants.UpsertAsync(grant, ct);
    }

    public Task<RemoteAccessGrant?> GetAsync(string requestId, CancellationToken ct = default) =>
        _grants.GetAsync(requestId, ct);

    public async Task<IReadOnlyList<RemoteAccessGrant>> GetAllAsync(CancellationToken ct = default) =>
        (await _grants.GetAllAsync(ct))
        .OrderByDescending(g => g.UpdatedAt)
        .ToList();

    public async Task<RemoteAccessGrant> ApproveAsync(string requestId, CancellationToken ct = default)
    {
        var grant = await Required(requestId, ct);

        if (grant.Status != RemoteGrantStatus.Pending)
            throw new InvalidOperationException("That request has already been decided.");

        if (grant.IsExpiredRequest)
            throw new InvalidOperationException("That request has expired. Ask the other instance to request access again.");

        grant.Status = RemoteGrantStatus.Approved;
        grant.DecidedAt = DateTimeOffset.UtcNow;
        grant.ExpiresAt = DateTimeOffset.UtcNow.Add(RemoteTokenIssuer.Lifetime);

        _logger.LogInformation("Remote access approved for {Instance}", grant.InstanceName);

        return await _grants.UpsertAsync(grant, ct);
    }

    public async Task<RemoteAccessGrant> DenyAsync(string requestId, CancellationToken ct = default)
    {
        var grant = await Required(requestId, ct);
        grant.Status = RemoteGrantStatus.Denied;
        grant.DecidedAt = DateTimeOffset.UtcNow;

        return await _grants.UpsertAsync(grant, ct);
    }

    public async Task<RemoteAccessGrant> RevokeAsync(string grantId, CancellationToken ct = default)
    {
        var grant = await Required(grantId, ct);
        grant.Status = RemoteGrantStatus.Revoked;
        grant.DecidedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Remote access revoked for {Instance}", grant.InstanceName);

        return await _grants.UpsertAsync(grant, ct);
    }

    public Task<string?> IssueTokenAsync(RemoteAccessGrant grant, CancellationToken ct = default)
    {
        if (grant.Status != RemoteGrantStatus.Approved)
            return Task.FromResult<string?>(null);

        if (grant.DecidedAt is not { } decidedAt || DateTimeOffset.UtcNow - decidedAt > CollectionWindow)
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(_tokens.Issue(grant, out _));
    }

    public async Task TouchAsync(string grantId, CancellationToken ct = default)
    {
        if (await _grants.GetAsync(grantId, ct) is not { } grant)
            return;

        // Written at most once a minute. Every hub call would otherwise rewrite the file, and a
        // streaming turn makes a great many of those.
        if (grant.LastSeenAt is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(1))
            return;

        grant.LastSeenAt = DateTimeOffset.UtcNow;
        await _grants.UpsertAsync(grant, ct);
    }

    private async Task<RemoteAccessGrant> Required(string id, CancellationToken ct) =>
        await _grants.GetAsync(id, ct) ?? throw new InvalidOperationException("That access request no longer exists.");

    /// <summary>
    /// Six digits, from a cryptographic source. Short enough to read off one screen and type on
    /// another, which is all it is for — it identifies the request, it does not authenticate it.
    /// </summary>
    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
