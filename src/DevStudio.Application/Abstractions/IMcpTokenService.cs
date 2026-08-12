using DevStudio.Domain.Mcp;

namespace DevStudio.Application.Abstractions;

public sealed record McpTokenResult(bool Succeeded, string? Token, string Detail);

/// <summary>
/// Obtains bearer tokens for MCP servers that need one. Only the client-credentials grant is
/// performed here — user-delegated OAuth belongs to the CLI, which runs its own flow.
/// </summary>
public interface IMcpTokenService
{
    /// <summary>
    /// Returns a usable token, refreshing it first if the cached one has expired. Null when the
    /// server needs no token.
    /// </summary>
    Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default);

    /// <summary>Fetches a token now and reports what happened, for the UI's Test button.</summary>
    Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default);
}
