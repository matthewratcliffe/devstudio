using DevStudio.Domain.Common;

namespace DevStudio.Domain.Mcp;

/// <summary>How an HTTP or SSE MCP server is authenticated.</summary>
public enum McpAuthMode
{
    /// <summary>Nothing added, or whatever static headers are configured.</summary>
    None = 0,

    /// <summary>
    /// OAuth 2.0 client credentials. The orchestrator fetches and refreshes the token and writes it
    /// as a bearer header, which is what service-to-service MCP servers expect.
    /// </summary>
    ClientCredentials = 1,

    /// <summary>
    /// A bearer token you paste in yourself, for servers that issue long-lived tokens or where the
    /// CLI performs its own interactive OAuth.
    /// </summary>
    BearerToken = 2,
}

public enum McpTransport
{
    /// <summary>Launched as a child process; the CLI talks JSON-RPC over its stdio.</summary>
    Stdio = 0,
    /// <summary>Streamable HTTP endpoint.</summary>
    Http = 1,
    /// <summary>Server-sent events endpoint.</summary>
    Sse = 2,
}

/// <summary>
/// An MCP server made available to agents. Registered once here, then attached to any number of
/// agents; the workspace provisioner writes the CLI-specific config before a session starts.
/// </summary>
public sealed class McpServer : Entity
{
    public string Name { get; set; } = "new-server";
    public string Description { get; set; } = string.Empty;
    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    /// <summary>Executable for stdio servers, e.g. <c>npx</c>.</summary>
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];

    /// <summary>Endpoint for http/sse servers.</summary>
    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Environment { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Authentication for http and sse transports.</summary>
    public McpAuthMode AuthMode { get; set; } = McpAuthMode.None;

    /// <summary>Token endpoint for the client-credentials grant.</summary>
    public string TokenUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Held on the data volume alongside everything else, so treat that volume as secret material.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Space-separated scopes requested with the grant.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>Audience or resource parameter, when the issuer needs one.</summary>
    public string? Audience { get; set; }

    /// <summary>A token pasted in directly, used when the mode is BearerToken.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>When the cached token stops being usable. Null means it does not expire.</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    /// <summary>Header the token is written into. Bearer is the norm.</summary>
    public string AuthHeaderName { get; set; } = "Authorization";
    public string AuthHeaderPrefix { get; set; } = "Bearer";

    public bool Enabled { get; set; } = true;

    /// <summary>Attach to every agent without listing it explicitly.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Set on the built-in orchestrator server so the UI can protect it.</summary>
    public bool IsBuiltIn { get; set; }
}
