namespace DevStudio.Application.Abstractions;

/// <summary>What a rotation left behind, so the UI can say how long the old token still works.</summary>
/// <param name="Token">The token now in force.</param>
/// <param name="RetiredValidUntil">
/// When the token it replaced stops being accepted, or null if it was cut off on the spot.
/// </param>
public sealed record McpTokenRotation(string Token, DateTimeOffset? RetiredValidUntil);

/// <summary>
/// Owns the single secret that guards this app's own MCP endpoints. Nobody types it in: it is
/// generated on first use, kept on the data volume, handed to agent CLIs when their workspace
/// config is written, and checked on every MCP request. The point is only to keep something that
/// reached the port from talking to the orchestrator — a person never presents it by hand.
/// </summary>
public interface IMcpAccessTokenProvider
{
    /// <summary>The token in force, generating and persisting one the first time it is asked for.</summary>
    string Current { get; }

    /// <summary>
    /// When the token replaced by the last rotation stops being accepted. Null when nothing is being
    /// honoured on its way out, which is the normal state.
    /// </summary>
    DateTimeOffset? RetiredValidUntil { get; }

    /// <summary>
    /// Whether a presented credential is accepted: the current token, or one recently retired and
    /// still inside its grace window. Compared in constant time so a caller cannot learn a token a
    /// character at a time from how long the answer takes.
    /// </summary>
    bool Matches(string? presented);

    /// <summary>
    /// Issues a new token. Workspace config is rewritten every turn, so a turn that has not started
    /// yet gets the new one regardless; the grace window exists only for turns already in flight,
    /// which hold the old value in a file the running CLI has already read.
    /// </summary>
    /// <param name="immediately">
    /// True cuts the old token off at once, which is what a leaked data volume calls for: any turn
    /// mid-flight loses its MCP tools and reports the 401. False — the default — keeps the old token
    /// working for as long as a turn is allowed to run, so nothing in flight notices.
    /// </param>
    McpTokenRotation Rotate(bool immediately = false);
}
