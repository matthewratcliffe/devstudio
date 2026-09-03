using DevStudio.Domain.Common;

namespace DevStudio.Domain.Remoting;

/// <summary>Where the connection to a remote instance currently stands.</summary>
public enum RemoteInstanceState
{
    /// <summary>Added but never paired: there is no key yet, so nothing can be run on it.</summary>
    Unpaired = 0,

    /// <summary>Access has been asked for and the far side has not answered yet.</summary>
    AwaitingApproval = 1,

    /// <summary>Paired and reachable.</summary>
    Connected = 2,

    /// <summary>Paired but the connection is down — the far side is off, or the network is.</summary>
    Disconnected = 3,

    /// <summary>The far side refused the request, or revoked a key it had already granted.</summary>
    Denied = 4,
}

/// <summary>
/// Another DevStudio installation this one may run work on. Held only by the side that dials out:
/// the machine being connected to keeps a <see cref="RemoteAccessGrant"/> instead.
///
/// The session, its transcript and the agent that owns it all stay local. What the remote supplies
/// is the place the work actually happens — its CLIs and their logins, its MCP servers and skills,
/// its checkouts, and its filesystem.
/// </summary>
public sealed class RemoteInstance : Entity
{
    public string Name { get; set; } = "new instance";

    /// <summary>Base address of the far side, e.g. <c>http://desk.lan:7080</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Off means it stays configured but is offered nowhere and never dialled.</summary>
    public bool Enabled { get; set; } = true;

    public RemoteInstanceState State { get; set; } = RemoteInstanceState.Unpaired;

    /// <summary>
    /// The key that buys access: a JWT the far side signed and handed over when the request was
    /// approved. Long-lived by design — this is a pairing between two machines an operator owns,
    /// not a user session, and a key that expires monthly would only ever be re-approved on
    /// reflex.
    /// </summary>
    public string? AccessToken { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>Id of the pending request, while one is outstanding.</summary>
    public string? PairingRequestId { get; set; }

    /// <summary>
    /// Shown on both machines while a request is outstanding so the person approving can see they
    /// are approving the request they think they are, and not one that arrived at the same moment
    /// from somewhere else.
    /// </summary>
    public string? VerificationCode { get; set; }

    /// <summary>Name the far side reports for itself, once it has been reached.</summary>
    public string? HostName { get; set; }

    public string? HostVersion { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>Why the last attempt failed, for the UI. Cleared on a successful connection.</summary>
    public string? LastError { get; set; }
}
