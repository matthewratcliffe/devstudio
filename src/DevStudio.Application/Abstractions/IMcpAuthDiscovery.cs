namespace DevStudio.Application.Abstractions;

/// <summary>
/// What an MCP endpoint says about its own authentication, gathered from the challenge it returns
/// and the metadata documents that challenge points at.
/// </summary>
public sealed record McpAuthDiscovery(
    bool Succeeded,
    string Detail,
    bool RequiresAuth = false,
    string? ResourceMetadataUrl = null,
    string? Resource = null,
    string? Issuer = null,
    string? TokenUrl = null,
    string? AuthorizationUrl = null,
    string? RegistrationUrl = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? GrantTypes = null)
{
    public bool SupportsClientCredentials =>
        GrantTypes?.Contains("client_credentials", StringComparer.OrdinalIgnoreCase) ?? false;

    /// <summary>The whole scope list, ready for the space-separated field on the server record.</summary>
    public string ScopeString => Scopes is null ? string.Empty : string.Join(' ', Scopes);
}

/// <summary>
/// Asks an HTTP or SSE MCP endpoint how it wants to be authenticated, so the operator does not have
/// to find the issuer's metadata by hand. Follows RFC 9728 (protected resource metadata) from the
/// 401 challenge through to the authorization server's own document.
/// </summary>
public interface IMcpAuthDiscovery
{
    Task<McpAuthDiscovery> DiscoverAsync(string mcpUrl, CancellationToken ct = default);
}
