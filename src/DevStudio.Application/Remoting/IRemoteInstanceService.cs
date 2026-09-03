using DevStudio.Domain.Remoting;

namespace DevStudio.Application.Remoting;

/// <summary>
/// The dialling side of the handshake: asking another installation for access, waiting for somebody
/// over there to approve it, and keeping the key that comes back.
/// </summary>
public interface IRemoteInstancePairing
{
    /// <summary>
    /// Asks the instance for access. Returns with the request lodged and a verification code to
    /// show; the far side has to approve before anything can be run.
    /// </summary>
    Task<RemoteInstance> RequestAccessAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an outstanding request has been decided, storing the key if it was approved.
    /// Safe to call repeatedly — this is what the UI polls while the operator walks to the other
    /// machine.
    /// </summary>
    Task<RemoteInstance> PollAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Confirms a stored key still works and refreshes what we know about the far side. Used by the
    /// "Test connection" button and on first use after a restart.
    /// </summary>
    Task<RemoteInstance> TestAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Drops the key locally. The grant on the far side is untouched — only somebody there can
    /// withdraw that, which is the whole point of it living there.
    /// </summary>
    Task<RemoteInstance> ForgetAsync(string instanceId, CancellationToken ct = default);
}

/// <summary>
/// The receiving side: requests that have arrived, and the decision on each. Approving is what mints
/// the key, so this is the only place access is ever granted.
/// </summary>
public interface IRemoteAccessService
{
    /// <summary>Lodges an incoming request and returns the record, pending a decision.</summary>
    Task<RemoteAccessGrant> LodgeAsync(RemotePairingRequest request, string remoteAddress, CancellationToken ct = default);

    Task<RemoteAccessGrant?> GetAsync(string requestId, CancellationToken ct = default);

    /// <summary>Requests and grants, newest first, for the page that shows them.</summary>
    Task<IReadOnlyList<RemoteAccessGrant>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Approves a pending request and mints its key. The key is returned once, to be collected by
    /// the requester's next poll; it is not stored in readable form afterwards.
    /// </summary>
    Task<RemoteAccessGrant> ApproveAsync(string requestId, CancellationToken ct = default);

    Task<RemoteAccessGrant> DenyAsync(string requestId, CancellationToken ct = default);

    /// <summary>Withdraws a granted key. Takes effect on the far side's next call.</summary>
    Task<RemoteAccessGrant> RevokeAsync(string grantId, CancellationToken ct = default);

    /// <summary>
    /// The key for an approved request, for the requester to collect. Returns null until approved,
    /// and after the collection window has passed.
    /// </summary>
    Task<string?> IssueTokenAsync(RemoteAccessGrant grant, CancellationToken ct = default);

    /// <summary>Notes that a grant was just used, so the page can show what is actually in use.</summary>
    Task TouchAsync(string grantId, CancellationToken ct = default);
}

/// <summary>
/// Signs and checks the keys handed to paired instances. The signing key lives on the data volume
/// beside the MCP token, and is generated the first time it is needed.
/// </summary>
public interface IRemoteTokenIssuer
{
    /// <summary>
    /// A key for one grant, valid for five years. Long because this pairs two machines an operator
    /// owns: the alternative is a re-approval prompt every few weeks, which is a prompt nobody reads
    /// by the third time. It is revocable at any moment from the granting side, which is the control
    /// that actually matters.
    /// </summary>
    string Issue(RemoteAccessGrant grant, out DateTimeOffset expiresAt);

    /// <summary>The signing key, for wiring token validation up at startup.</summary>
    byte[] SigningKey { get; }

    /// <summary>Audience and issuer this instance signs with and accepts.</summary>
    string Issuer { get; }
    string Audience { get; }
}
