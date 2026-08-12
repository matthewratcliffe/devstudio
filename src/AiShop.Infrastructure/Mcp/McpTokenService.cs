using System.Net.Http.Headers;
using System.Text.Json;
using AiShop.Application.Abstractions;
using AiShop.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace AiShop.Infrastructure.Mcp;

/// <summary>
/// Runs the OAuth 2.0 client-credentials grant for MCP servers that authenticate as a service.
/// Tokens are cached on the server record and refreshed a minute before they lapse, so a long-lived
/// agent session never hands over a token that expires mid-call.
/// </summary>
public sealed class McpTokenService : IMcpTokenService
{
    /// <summary>Refresh this far ahead of expiry rather than racing it.</summary>
    private static readonly TimeSpan Leeway = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _clients;
    private readonly IEntityStore<McpServer> _servers;
    private readonly ILogger<McpTokenService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public McpTokenService(
        IHttpClientFactory clients,
        IEntityStore<McpServer> servers,
        ILogger<McpTokenService> logger)
    {
        _clients = clients;
        _servers = servers;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default)
    {
        switch (server.AuthMode)
        {
            case McpAuthMode.BearerToken:
                return string.IsNullOrWhiteSpace(server.AccessToken) ? null : server.AccessToken;

            case McpAuthMode.ClientCredentials:
                break;

            default:
                return null;
        }

        if (!string.IsNullOrWhiteSpace(server.AccessToken) &&
            server.AccessTokenExpiresAt is { } expiry &&
            expiry - Leeway > DateTimeOffset.UtcNow)
        {
            return server.AccessToken;
        }

        // One refresh at a time: several sessions starting together would otherwise all fetch.
        await _lock.WaitAsync(ct);
        try
        {
            var latest = await _servers.GetAsync(server.Id, ct) ?? server;
            if (!string.IsNullOrWhiteSpace(latest.AccessToken) &&
                latest.AccessTokenExpiresAt is { } fresh &&
                fresh - Leeway > DateTimeOffset.UtcNow)
            {
                return latest.AccessToken;
            }

            var result = await RequestTokenAsync(latest, ct);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Could not get a token for MCP server {Server}: {Detail}", latest.Name, result.Detail);
                return null;
            }

            await _servers.UpsertAsync(latest, ct);
            server.AccessToken = latest.AccessToken;
            server.AccessTokenExpiresAt = latest.AccessTokenExpiresAt;
            return result.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default)
    {
        if (server.AuthMode == McpAuthMode.BearerToken)
        {
            return string.IsNullOrWhiteSpace(server.AccessToken)
                ? new McpTokenResult(false, null, "No token has been pasted in.")
                : new McpTokenResult(true, server.AccessToken, "A token is set and will be sent as a bearer header.");
        }

        if (server.AuthMode != McpAuthMode.ClientCredentials)
            return new McpTokenResult(true, null, "This server needs no token.");

        var result = await RequestTokenAsync(server, ct);
        if (result.Succeeded)
            await _servers.UpsertAsync(server, ct);

        return result;
    }

    /// <summary>Performs the grant and writes the result onto <paramref name="server"/>.</summary>
    private async Task<McpTokenResult> RequestTokenAsync(McpServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.TokenUrl) || string.IsNullOrWhiteSpace(server.ClientId))
            return new McpTokenResult(false, null, "A token URL and client id are required.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = server.ClientId,
        };

        if (!string.IsNullOrWhiteSpace(server.ClientSecret))
            form["client_secret"] = server.ClientSecret;

        if (!string.IsNullOrWhiteSpace(server.Scopes))
            form["scope"] = server.Scopes;

        if (!string.IsNullOrWhiteSpace(server.Audience))
            form["audience"] = server.Audience!;

        var client = _clients.CreateClient(nameof(McpTokenService));
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(HttpMethod.Post, server.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new McpTokenResult(false, null, $"The token endpoint returned {(int)response.StatusCode}. {Summarise(body)}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
                return new McpTokenResult(false, null, "The response contained no access_token.");

            server.AccessToken = token.GetString()!;
            server.AccessTokenExpiresAt = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;

            var lifetime = server.AccessTokenExpiresAt is { } at
                ? $"valid until {at.ToLocalTime():HH:mm}"
                : "no expiry reported";

            return new McpTokenResult(true, server.AccessToken, $"Got a token ({lifetime}).");
        }
        catch (HttpRequestException ex)
        {
            return new McpTokenResult(false, null, $"Could not reach the token endpoint: {ex.Message}");
        }
        catch (JsonException)
        {
            return new McpTokenResult(false, null, "The token endpoint did not return JSON.");
        }
        catch (TaskCanceledException)
        {
            return new McpTokenResult(false, null, "The token endpoint did not answer in time.");
        }
    }

    private static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var text = body.Length <= 200 ? body : body[..200];
        return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
