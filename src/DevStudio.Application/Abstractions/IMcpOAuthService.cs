using DevStudio.Domain.Mcp;

namespace DevStudio.Application.Abstractions;

/// <summary>Where to send the browser, or why it cannot be sent.</summary>
public sealed record McpOAuthStart(bool Succeeded, string Detail, string? AuthorizationUrl = null);

/// <summary>The outcome of the redirect back from the issuer.</summary>
public sealed record McpOAuthCompletion(
    bool Succeeded,
    string Detail,
    string? ServerId = null,
    string? ServerName = null);

/// <summary>
/// Runs the user-delegated half of OAuth for MCP servers: register this app with the issuer if it
/// allows it, send somebody to sign in, then swap the code it returns for a token pair.
///
/// The authorization code flow needs a browser and a person, which is why it lives apart from
/// <see cref="IMcpTokenService"/> — that one only ever performs grants that can run unattended.
/// Once the sign-in is done, though, the refresh token it leaves behind is the token service's to
/// use, and no further browser trip is needed until it is revoked.
/// </summary>
public interface IMcpOAuthService
{
    /// <summary>
    /// Prepares a sign-in: fills in any endpoints not yet known, registers a client when the issuer
    /// offers dynamic registration, and returns the URL to send the browser to.
    /// <paramref name="redirectUri"/> must be the callback this app answers on, and is submitted
    /// with the registration, so it cannot be changed afterwards without registering again.
    /// </summary>
    Task<McpOAuthStart> BeginAsync(McpServer server, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Finishes the flow from the query the issuer redirected back with, saving the tokens onto the
    /// server record. The state is what ties the response to the request that started it.
    /// </summary>
    Task<McpOAuthCompletion> CompleteAsync(
        string? state,
        string? code,
        string? error,
        string? errorDescription,
        CancellationToken ct = default);

    /// <summary>Forgets the tokens for a server, so the next run has to sign in again.</summary>
    Task SignOutAsync(McpServer server, CancellationToken ct = default);
}
