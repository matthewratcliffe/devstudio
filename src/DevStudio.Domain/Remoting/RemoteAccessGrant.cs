using DevStudio.Domain.Common;

namespace DevStudio.Domain.Remoting;

public enum RemoteGrantStatus
{
    /// <summary>Asked for, waiting on somebody at this machine to say yes.</summary>
    Pending = 0,
    Approved = 1,
    Denied = 2,

    /// <summary>Approved once and withdrawn since. The key is refused from the moment it is set.</summary>
    Revoked = 3,
}

/// <summary>
/// One other installation's permission to run work on this machine. Held by the side being
/// connected to — the dialling side keeps a <see cref="RemoteInstance"/>.
///
/// The key handed out is a JWT whose <c>jti</c> is this record's id, so revoking is a status change
/// here rather than anything the far side has to be told about: the next call it makes is refused.
/// </summary>
public sealed class RemoteAccessGrant : Entity
{
    /// <summary>What the far side calls itself, as typed by whoever set it up over there.</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>Machine name the far side reported, which is the part it did not get to choose.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>The far side's own instance id, so a re-request from the same install is recognised.</summary>
    public string RemoteInstanceId { get; set; } = string.Empty;

    /// <summary>Address the request arrived from, worth seeing before approving it.</summary>
    public string RemoteAddress { get; set; } = string.Empty;

    public string? Version { get; set; }

    public RemoteGrantStatus Status { get; set; } = RemoteGrantStatus.Pending;

    /// <summary>
    /// Short code shown on both machines while the request is pending. It proves the request being
    /// approved is the one just made, which matters because approval is the only thing standing
    /// between anything that can reach the port and a shell on this machine.
    /// </summary>
    public string VerificationCode { get; set; } = string.Empty;

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>When the issued key stops being accepted regardless of status.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Last time this grant was used, so a stale one can be spotted and withdrawn.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// A pending request is only good for a short while. Left open indefinitely it becomes a thing
    /// somebody approves weeks later without remembering what asked for it.
    /// </summary>
    public bool IsExpiredRequest =>
        Status == RemoteGrantStatus.Pending && DateTimeOffset.UtcNow - CreatedAt > TimeSpan.FromMinutes(15);
}
