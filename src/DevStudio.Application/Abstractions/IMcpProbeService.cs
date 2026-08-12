using DevStudio.Domain.Mcp;

namespace DevStudio.Application.Abstractions;

public sealed record McpToolInfo(string Name, string Description, string? InputSchema = null);

public sealed record McpProbeResult(
    bool Succeeded,
    string Detail,
    IReadOnlyList<McpToolInfo> Tools,
    string? ServerName = null,
    string? ServerVersion = null);

/// <summary>
/// The outcome of running one tool. <paramref name="Content"/> is what the server sent back, already
/// flattened to text, so the UI can show it without knowing the content-block shapes.
/// </summary>
public sealed record McpToolCallResult(bool Succeeded, string Detail, string? Content = null);

/// <summary>
/// Connects to an MCP server the way a CLI would — initialize, then tools/list — so a server can be
/// checked before an agent run depends on it.
/// </summary>
public interface IMcpProbeService
{
    Task<McpProbeResult> ListToolsAsync(McpServer server, CancellationToken ct = default);

    /// <summary>
    /// Runs a single tool. Listing proves the server answers; this proves a tool actually works,
    /// which is usually where credentials that are merely accepted turn out to be insufficient.
    /// </summary>
    Task<McpToolCallResult> CallToolAsync(
        McpServer server,
        string toolName,
        string? argumentsJson,
        CancellationToken ct = default);
}
