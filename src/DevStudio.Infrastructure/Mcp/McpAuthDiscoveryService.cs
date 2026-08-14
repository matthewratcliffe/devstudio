using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Mcp;

/// <summary>
/// Works out how an MCP endpoint wants to be authenticated by asking it. An unauthenticated
/// initialize draws a 401 whose WWW-Authenticate header names the resource metadata document; that
/// names the authorization server; that names the token endpoint and the grants on offer.
///
/// Everything here is read-only discovery against well-known paths, and no credentials are sent, so
/// a wrong URL costs nothing but the round trip.
/// </summary>
public sealed class McpAuthDiscoveryService : IMcpAuthDiscovery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const string InitializeBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"," +
        "\"capabilities\":{},\"clientInfo\":{\"name\":\"devstudio\",\"version\":\"1.0.0\"}}}";

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<McpAuthDiscoveryService> _logger;

    public McpAuthDiscoveryService(IHttpClientFactory clients, ILogger<McpAuthDiscoveryService> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public async Task<McpAuthDiscovery> DiscoverAsync(string mcpUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(mcpUrl?.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return new McpAuthDiscovery(false, "Enter an absolute http or https URL first.");
        }

        var client = _clients.CreateClient(nameof(McpAuthDiscoveryService));
        client.Timeout = Timeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var challenge = await ChallengeAsync(client, url, timeout.Token);

            if (challenge is null)
            {
                return new McpAuthDiscovery(
                    true,
                    "The endpoint answered without demanding a token, so it needs no authentication. Leave the mode on None.");
            }

            // The challenge header is authoritative; the well-known path is the fallback for servers
            // that return a bare 401. Both are defined by RFC 9728.
            var wellKnown = WellKnownResourceUrl(url);
            var metadataUrl = challenge.Length > 0 ? challenge : wellKnown;
            var resource = await ReadJsonAsync(client, metadataUrl, timeout.Token);

            if (resource is null && !string.Equals(metadataUrl, wellKnown, StringComparison.Ordinal))
            {
                metadataUrl = wellKnown;
                resource = await ReadJsonAsync(client, metadataUrl, timeout.Token);
            }

            if (resource is null)
            {
                return new McpAuthDiscovery(
                    false,
                    "The endpoint requires a token but published no resource metadata, so its issuer has to be configured by hand.",
                    RequiresAuth: true);
            }

            var scopes = Strings(resource["scopes_supported"]);
            var issuer = Strings(resource["authorization_servers"]).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(issuer))
            {
                return new McpAuthDiscovery(
                    false,
                    "The resource metadata names no authorization server.",
                    RequiresAuth: true,
                    ResourceMetadataUrl: metadataUrl,
                    Resource: resource["resource"]?.GetValue<string>(),
                    Scopes: scopes);
            }

            var server = await ReadIssuerMetadataAsync(client, issuer, timeout.Token);

            if (server is null)
            {
                return new McpAuthDiscovery(
                    false,
                    $"Found the issuer ({issuer}) but could not read its metadata.",
                    RequiresAuth: true,
                    ResourceMetadataUrl: metadataUrl,
                    Resource: resource["resource"]?.GetValue<string>(),
                    Issuer: issuer,
                    Scopes: scopes);
            }

            var grants = Strings(server["grant_types_supported"]);

            return new McpAuthDiscovery(
                true,
                Describe(grants),
                RequiresAuth: true,
                ResourceMetadataUrl: metadataUrl,
                Resource: resource["resource"]?.GetValue<string>(),
                Issuer: server["issuer"]?.GetValue<string>() ?? issuer,
                TokenUrl: server["token_endpoint"]?.GetValue<string>(),
                AuthorizationUrl: server["authorization_endpoint"]?.GetValue<string>(),
                RegistrationUrl: server["registration_endpoint"]?.GetValue<string>(),
                Scopes: scopes,
                GrantTypes: grants);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpAuthDiscovery(false, "The endpoint did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new McpAuthDiscovery(false, $"Could not reach the endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovering auth for {Url} failed", mcpUrl);
            return new McpAuthDiscovery(false, ex.Message);
        }
    }

    /// <summary>
    /// Sends an initialize with no credentials. Returns the resource metadata URL from the challenge,
    /// an empty string when the server challenged without naming one, or null when it did not
    /// challenge at all.
    /// </summary>
    private static async Task<string?> ChallengeAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return null;

        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (ResourceMetadataFrom(header.Parameter) is { Length: > 0 } found)
                return found;
        }

        return string.Empty;
    }

    /// <summary>Pulls resource_metadata="..." out of a challenge's parameters.</summary>
    private static string? ResourceMetadataFrom(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return null;

        const string key = "resource_metadata=";
        var start = parameter.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var value = parameter[(start + key.Length)..].TrimStart();

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        var comma = value.IndexOf(',');
        return (comma < 0 ? value : value[..comma]).Trim();
    }

    /// <summary>
    /// RFC 9728 puts the document under the well-known prefix with the resource's path appended, not
    /// under the resource's own directory.
    /// </summary>
    private static string WellKnownResourceUrl(Uri url) =>
        $"{url.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource{url.AbsolutePath.TrimEnd('/')}";

    /// <summary>
    /// Tries the issuer's metadata the ways it is published in the wild: the path-aware well-known
    /// form, the plain suffix form, and OpenID discovery.
    /// </summary>
    private static async Task<JsonObject?> ReadIssuerMetadataAsync(HttpClient client, string issuer, CancellationToken ct)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var url))
            return null;

        var authority = url.GetLeftPart(UriPartial.Authority);
        var path = url.AbsolutePath.TrimEnd('/');
        var trimmed = issuer.TrimEnd('/');

        string[] candidates =
        [
            $"{authority}/.well-known/oauth-authorization-server{path}",
            $"{trimmed}/.well-known/oauth-authorization-server",
            $"{authority}/.well-known/openid-configuration{path}",
            $"{trimmed}/.well-known/openid-configuration",
        ];

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (await ReadJsonAsync(client, candidate, ct) is { } document && document["token_endpoint"] is not null)
                return document;
        }

        return null;
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one line the operator needs: whether this app can get a token on its own, or whether the
    /// sign-in has to happen at a browser.
    /// </summary>
    private static string Describe(IReadOnlyList<string> grants)
    {
        var service = grants.Contains("client_credentials", StringComparer.OrdinalIgnoreCase);
        var delegated = grants.Contains("authorization_code", StringComparer.OrdinalIgnoreCase);

        if (service && delegated)
        {
            return "This issuer offers client credentials, so it can be configured here — but it also "
                 + "offers the user-delegated flow, which is what most hosted servers actually require. "
                 + "If a service token is refused, leave the mode on None and let the CLI sign in.";
        }

        if (service)
            return "This issuer offers client credentials, so it can be configured here.";

        return delegated
            ? "This issuer only supports user-delegated OAuth, which needs a person at a browser. Leave "
              + "the mode on None and let the CLI run the sign-in, or paste a token you obtained yourself."
            : "The issuer was found, but it advertises no grant this app can run on its own.";
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var text) ? text : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList()
            : [];
}
