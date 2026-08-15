using System.Net.Http.Headers;
using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Mcp;

/// <summary>
/// Hands out bearer tokens for MCP servers. Two grants run here, both unattended: client credentials
/// for servers that authenticate as a service, and the refresh grant that renews what a user's OAuth
/// sign-in left behind. The sign-in itself needs a browser and belongs to <see cref="IMcpOAuthService"/>.
///
/// Tokens are cached on the server record and refreshed a minute before they lapse, so a long-lived
/// agent session never hands over a token that expires mid-call.
///
/// The built-in servers are the exception: those are this app's own MCP endpoints, and the
/// credential for them is the managed token from <see cref="IMcpAccessTokenProvider"/> rather than
/// anything an issuer hands out. It is read here rather than stored on the record so a rotation
/// takes effect on the next session without the record having to be rewritten.
/// </summary>
public sealed class McpTokenService : IMcpTokenService
{
    /// <summary>Refresh this far ahead of expiry rather than racing it.</summary>
    private static readonly TimeSpan Leeway = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _clients;
    private readonly IEntityStore<McpServer> _servers;
    private readonly IMcpAccessTokenProvider _localToken;
    private readonly ILogger<McpTokenService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public McpTokenService(
        IHttpClientFactory clients,
        IEntityStore<McpServer> servers,
        IMcpAccessTokenProvider localToken,
        ILogger<McpTokenService> logger)
    {
        _clients = clients;
        _servers = servers;
        _localToken = localToken;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default)
    {
        var result = await AcquireAsync(server, ct);

        if (!result.Succeeded)
            _logger.LogWarning("Could not get a token for MCP server {Server}: {Detail}", server.Name, result.Detail);

        return result.Token;
    }

    public async Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default)
    {
        if (server.IsBuiltIn)
            return new McpTokenResult(true, _localToken.Current, "Using this app's own managed MCP token.");

        switch (server.AuthMode)
        {
            case McpAuthMode.None:
                return new McpTokenResult(true, null, "This server needs no token.");

            case McpAuthMode.BearerToken:
                return string.IsNullOrWhiteSpace(server.AccessToken)
                    ? new McpTokenResult(false, null, "No token has been pasted in.")
                    : new McpTokenResult(true, server.AccessToken, "Using the token that was pasted in.");

            case McpAuthMode.ClientCredentials:
            case McpAuthMode.OAuth:
                break;

            default:
                return new McpTokenResult(true, null, "This server needs no token.");
        }

        if (Usable(server))
            return new McpTokenResult(true, server.AccessToken, "The cached token is still valid.");

        // One refresh at a time: several sessions starting together would otherwise all fetch.
        await _lock.WaitAsync(ct);
        try
        {
            var latest = await _servers.GetAsync(server.Id, ct) ?? server;

            // Another caller may have renewed it while this one waited for the lock.
            if (Usable(latest))
            {
                Copy(latest, server);
                return new McpTokenResult(true, latest.AccessToken, "The cached token is still valid.");
            }

            var result = server.AuthMode == McpAuthMode.OAuth
                ? await RefreshAsync(latest, ct)
                : await RequestClientCredentialsAsync(latest, ct);

            if (!result.Succeeded)
                return result;

            await _servers.UpsertAsync(latest, ct);
            Copy(latest, server);

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default)
    {
        if (server.IsBuiltIn)
            return new McpTokenResult(true, _localToken.Current, "This app manages its own token for this server.");

        if (server.AuthMode == McpAuthMode.BearerToken)
        {
            return string.IsNullOrWhiteSpace(server.AccessToken)
                ? new McpTokenResult(false, null, "No token has been pasted in.")
                : new McpTokenResult(true, server.AccessToken, "A token is set and will be sent as a bearer header.");
        }

        if (server.AuthMode == McpAuthMode.None)
            return new McpTokenResult(true, null, "This server needs no token.");

        if (server.AuthMode == McpAuthMode.OAuth)
        {
            if (string.IsNullOrWhiteSpace(server.RefreshToken) && !Usable(server))
                return new McpTokenResult(false, null, "Nobody has signed in yet. Use Sign in to authorise this server.");

            var renewed = await AcquireAsync(server, ct);
            return renewed;
        }

        var result = await RequestClientCredentialsAsync(server, ct);
        if (result.Succeeded)
            await _servers.UpsertAsync(server, ct);

        return result;
    }

    /// <summary>True when the record holds a token that has not expired and is not about to.</summary>
    private static bool Usable(McpServer server) =>
        !string.IsNullOrWhiteSpace(server.AccessToken) &&
        server.AccessTokenExpiresAt is { } expiry &&
        expiry - Leeway > DateTimeOffset.UtcNow;

    private static void Copy(McpServer from, McpServer to)
    {
        if (ReferenceEquals(from, to))
            return;

        to.AccessToken = from.AccessToken;
        to.AccessTokenExpiresAt = from.AccessTokenExpiresAt;
        to.RefreshToken = from.RefreshToken;
    }

    /// <summary>Renews a user's sign-in from its refresh token, with no browser involved.</summary>
    private async Task<McpTokenResult> RefreshAsync(McpServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.RefreshToken))
        {
            return new McpTokenResult(
                false,
                null,
                string.IsNullOrWhiteSpace(server.AccessToken)
                    ? "Nobody has signed in to this server yet."
                    : "The sign-in has expired and the issuer left no refresh token, so it has to be done again.");
        }

        if (string.IsNullOrWhiteSpace(server.TokenUrl))
            return new McpTokenResult(false, null, "No token endpoint is configured.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = server.RefreshToken,
            ["client_id"] = server.ClientId,
        };

        if (!string.IsNullOrWhiteSpace(server.ClientSecret))
            form["client_secret"] = server.ClientSecret;

        var result = await SendAsync(server, form, ct);

        // A refresh token the issuer has revoked is not a transient failure: say what has to happen.
        return result.Succeeded
            ? result
            : new McpTokenResult(false, null, $"{result.Detail} The sign-in may have been revoked — sign in again.");
    }

    /// <summary>Performs the client-credentials grant and writes the result onto <paramref name="server"/>.</summary>
    private async Task<McpTokenResult> RequestClientCredentialsAsync(McpServer server, CancellationToken ct)
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

        return await SendAsync(server, form, ct);
    }

    /// <summary>Posts a grant to the token endpoint and stores whatever comes back.</summary>
    private async Task<McpTokenResult> SendAsync(McpServer server, Dictionary<string, string> form, CancellationToken ct)
    {
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

            // Issuers that rotate refresh tokens send a new one with every renewal; keeping the old
            // one would break the next refresh.
            if (root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String)
                server.RefreshToken = refresh.GetString()!;

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
