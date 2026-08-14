using DevStudio.Domain.Mcp;

namespace DevStudio.Application.Abstractions;

public sealed record McpTokenResult(bool Succeeded, string? Token, string Detail);

/// <summary>
/// Obtains bearer tokens for MCP servers that need one: the client-credentials grant for servers
/// that authenticate as a service, and the stored result of a user's OAuth sign-in — renewed from
/// its refresh token — for those that only issue delegated tokens.
/// </summary>
public interface IMcpTokenService
{
    /// <summary>
    /// Returns a usable token, refreshing it first if the cached one has expired. Null when the
    /// server needs no token.
    /// </summary>
    Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default);

    /// <summary>
    /// The same work as <see cref="GetAccessTokenAsync"/>, but saying what went wrong when no token
    /// comes back. Callers that report to a person should prefer this: a server that rejects the
    /// request because the grant failed and one that was never given a credential both produce a
    /// bare 401 otherwise, and those need very different fixes.
    /// </summary>
    Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default);

    /// <summary>Fetches a token now and reports what happened, for the UI's Test button.</summary>
    Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default);
}
