using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Mcp;

/// <summary>
/// The authorization code flow with PKCE, plus the dynamic client registration that hosted MCP
/// servers expect to precede it. Nothing here is specific to any one issuer: the endpoints all come
/// from discovery, and a server that publishes no registration endpoint simply needs a client id
/// entered by hand before a sign-in can start.
/// </summary>
public sealed class McpOAuthService : IMcpOAuthService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a started sign-in stays valid. Long enough to log in, short enough to expire.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _clients;
    private readonly IEntityStore<McpServer> _servers;
    private readonly IMcpAuthDiscovery _discovery;
    private readonly ILogger<McpOAuthService> _logger;

    /// <summary>Sign-ins that have been started but not yet returned from, keyed by state.</summary>
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public McpOAuthService(
        IHttpClientFactory clients,
        IEntityStore<McpServer> servers,
        IMcpAuthDiscovery discovery,
        ILogger<McpOAuthService> logger)
    {
        _clients = clients;
        _servers = servers;
        _discovery = discovery;
        _logger = logger;
    }

    private sealed record Pending(string ServerId, string Verifier, string RedirectUri, DateTimeOffset StartedAt);

    public async Task<McpOAuthStart> BeginAsync(McpServer server, string redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.Id))
            return new McpOAuthStart(false, "Save the server before signing in to it.");

        if (string.IsNullOrWhiteSpace(redirectUri))
            return new McpOAuthStart(false, "This app could not work out its own callback address.");

        // Work from the saved copy: the sign-in finishes on a different request, and anything written
        // only to the page's in-memory copy would be lost when the tokens are saved.
        var record = await _servers.GetAsync(server.Id, ct) ?? server;

        if (string.IsNullOrWhiteSpace(record.AuthorizationUrl) || string.IsNullOrWhiteSpace(record.TokenUrl))
        {
            var found = await _discovery.DiscoverAsync(record.Url, ct);

            if (found.AuthorizationUrl is { Length: > 0 } authorization)
                record.AuthorizationUrl = authorization;

            if (found.TokenUrl is { Length: > 0 } token)
                record.TokenUrl = token;

            if (found.RegistrationUrl is { Length: > 0 } registration)
                record.RegistrationUrl = registration;

            if (string.IsNullOrWhiteSpace(record.Scopes) && found.ScopeString is { Length: > 0 } scopes)
                record.Scopes = scopes;
        }

        if (string.IsNullOrWhiteSpace(record.AuthorizationUrl) || string.IsNullOrWhiteSpace(record.TokenUrl))
        {
            return new McpOAuthStart(
                false,
                "This server publishes no authorization or token endpoint, so its OAuth details have to be filled in by hand.");
        }

        if (string.IsNullOrWhiteSpace(record.ClientId))
        {
            if (string.IsNullOrWhiteSpace(record.RegistrationUrl))
            {
                return new McpOAuthStart(
                    false,
                    "This issuer does not offer dynamic registration, so a client id has to be created with the "
                  + "provider and entered above before signing in.");
            }

            var registered = await RegisterAsync(record, redirectUri, ct);
            if (!registered.Succeeded)
                return new McpOAuthStart(false, registered.Detail);
        }

        var verifier = RandomUrlSafe(32);
        var state = RandomUrlSafe(32);

        Prune();
        _pending[state] = new Pending(record.Id, verifier, redirectUri, DateTimeOffset.UtcNow);

        await _servers.UpsertAsync(record, ct);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = record.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };

        if (!string.IsNullOrWhiteSpace(record.Scopes))
            query["scope"] = record.Scopes;

        // RFC 8707. Issuers that serve several APIs need telling which one the token is for, and
        // those that do not simply ignore it.
        if (!string.IsNullOrWhiteSpace(record.Audience))
            query["resource"] = record.Audience!;

        var separator = record.AuthorizationUrl.Contains('?') ? '&' : '?';
        var url = record.AuthorizationUrl + separator + Encode(query);

        return new McpOAuthStart(true, "Sign in at the page that opens, then come back here.", url);
    }

    public async Task<McpOAuthCompletion> CompleteAsync(
        string? state,
        string? code,
        string? error,
        string? errorDescription,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            var detail = string.IsNullOrWhiteSpace(errorDescription) ? error! : $"{error}: {errorDescription}";

            // Still clear the pending entry: this state will never be presented again.
            if (!string.IsNullOrWhiteSpace(state))
                _pending.TryRemove(state!, out _);

            return new McpOAuthCompletion(false, $"The issuer refused the sign-in ({detail}).");
        }

        if (string.IsNullOrWhiteSpace(state) || !_pending.TryRemove(state!, out var pending))
            return new McpOAuthCompletion(false, "This sign-in has expired or was already completed. Start it again.");

        if (DateTimeOffset.UtcNow - pending.StartedAt > PendingLifetime)
            return new McpOAuthCompletion(false, "This sign-in took too long and has expired. Start it again.");

        if (string.IsNullOrWhiteSpace(code))
            return new McpOAuthCompletion(false, "The issuer returned no authorization code.");

        var server = await _servers.GetAsync(pending.ServerId, ct);
        if (server is null)
            return new McpOAuthCompletion(false, "The server this sign-in belongs to has since been deleted.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = server.ClientId,
            ["code_verifier"] = pending.Verifier,
        };

        if (!string.IsNullOrWhiteSpace(server.ClientSecret))
            form["client_secret"] = server.ClientSecret;

        var result = await PostTokenAsync(server, form, ct);
        if (!result.Succeeded)
            return new McpOAuthCompletion(false, result.Detail, server.Id, server.Name);

        server.AuthMode = McpAuthMode.OAuth;
        server.AuthorizedAt = DateTimeOffset.UtcNow;
        await _servers.UpsertAsync(server, ct);

        return new McpOAuthCompletion(true, $"'{server.Name}' is authorised. {result.Detail}", server.Id, server.Name);
    }

    public async Task SignOutAsync(McpServer server, CancellationToken ct = default)
    {
        var record = await _servers.GetAsync(server.Id, ct) ?? server;

        record.AccessToken = string.Empty;
        record.RefreshToken = string.Empty;
        record.AccessTokenExpiresAt = null;
        record.AuthorizedAt = null;
        record.AuthorizedAccount = null;

        await _servers.UpsertAsync(record, ct);

        server.AccessToken = string.Empty;
        server.RefreshToken = string.Empty;
        server.AccessTokenExpiresAt = null;
        server.AuthorizedAt = null;
        server.AuthorizedAccount = null;
    }

    /// <summary>
    /// Registers this app with the issuer (RFC 7591) and writes the credentials it hands back onto
    /// the record. Public clients get no secret, which is why PKCE carries the flow.
    /// </summary>
    private async Task<McpTokenResult> RegisterAsync(McpServer server, string redirectUri, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["client_name"] = $"DevStudio ({server.Name})",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };

        if (!string.IsNullOrWhiteSpace(server.Scopes))
            body["scope"] = server.Scopes;

        var client = _clients.CreateClient(nameof(McpOAuthService));
        client.Timeout = Timeout;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.RegistrationUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new McpTokenResult(false, null, $"Registering with the issuer failed ({(int)response.StatusCode}). {Summarise(text)}");

            if (JsonNode.Parse(text) is not JsonObject document || document["client_id"]?.GetValue<string>() is not { Length: > 0 } clientId)
                return new McpTokenResult(false, null, "The issuer registered no client id.");

            server.ClientId = clientId;

            if (document["client_secret"]?.GetValue<string>() is { Length: > 0 } secret)
                server.ClientSecret = secret;

            return new McpTokenResult(true, null, "Registered with the issuer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Registering with {Url} failed", server.RegistrationUrl);
            return new McpTokenResult(false, null, $"Could not reach the registration endpoint: {ex.Message}");
        }
    }

    /// <summary>Posts a grant and writes the tokens onto the record. Shared by the code and refresh grants.</summary>
    private async Task<McpTokenResult> PostTokenAsync(McpServer server, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = _clients.CreateClient(nameof(McpOAuthService));
        client.Timeout = Timeout;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.TokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new McpTokenResult(false, null, $"The token endpoint returned {(int)response.StatusCode}. {Summarise(text)}");

            if (JsonNode.Parse(text) is not JsonObject document ||
                document["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
            {
                return new McpTokenResult(false, null, "The token endpoint returned no access_token.");
            }

            server.AccessToken = token;

            // A rotated refresh token replaces the old one; an omitted one means keep what we have.
            if (document["refresh_token"]?.GetValue<string>() is { Length: > 0 } refresh)
                server.RefreshToken = refresh;

            server.AccessTokenExpiresAt = document["expires_in"] is { } expires && expires.AsValue().TryGetValue<int>(out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;

            var lifetime = server.AccessTokenExpiresAt is { } at
                ? $"The token is valid until {at.ToLocalTime():HH:mm}"
                : "The token reports no expiry";

            var renewal = string.IsNullOrWhiteSpace(server.RefreshToken)
                ? ", and the issuer sent no refresh token, so this will need doing again when it lapses."
                : ", and it will be renewed from the refresh token after that.";

            return new McpTokenResult(true, token, lifetime + renewal);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Token request to {Url} failed", server.TokenUrl);
            return new McpTokenResult(false, null, $"Could not reach the token endpoint: {ex.Message}");
        }
    }

    /// <summary>Drops sign-ins nobody came back from, so an abandoned flow does not sit in memory.</summary>
    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - PendingLifetime;

        foreach (var pair in _pending)
        {
            if (pair.Value.StartedAt < cutoff)
                _pending.TryRemove(pair.Key, out _);
        }
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Encode(Dictionary<string, string> query) =>
        string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var text = body.Length <= 200 ? body : body[..200];
        return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
