using System.Net;
using DevStudio.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevStudio.Tests;

public class McpAuthDiscoveryServiceTests
{
    private const string McpUrl = "https://mcp.example.com/v1/sse";

    private static McpAuthDiscoveryService Create(DiscoveryHandler handler) =>
        new(new StubFactory(handler), NullLogger<McpAuthDiscoveryService>.Instance);

    private const string AuthServerDocument =
        """
        {
          "issuer": "https://cf.mcp.example.com",
          "authorization_endpoint": "https://mcp.example.com/v1/authorize",
          "token_endpoint": "https://cf.mcp.example.com/v1/token",
          "registration_endpoint": "https://cf.mcp.example.com/v1/register",
          "grant_types_supported": ["authorization_code", "refresh_token"],
          "code_challenge_methods_supported": ["S256"]
        }
        """;

    [Fact]
    public async Task An_endpoint_that_does_not_challenge_needs_no_auth()
    {
        var handler = new DiscoveryHandler();
        handler.Challenge = HttpStatusCode.OK;

        var result = await Create(handler).DiscoverAsync(McpUrl);

        Assert.True(result.Succeeded);
        Assert.False(result.RequiresAuth);
    }

    [Fact]
    public async Task A_server_that_publishes_only_its_own_auth_metadata_is_still_discovered()
    {
        // The RFC 9728 protected-resource document is absent, which is how Atlassian's Rovo endpoint
        // behaves. Falling back to the authority root is the difference between a usable answer and
        // telling the operator to configure it by hand.
        var handler = new DiscoveryHandler();
        handler.Documents["https://mcp.example.com/.well-known/oauth-authorization-server"] = AuthServerDocument;

        var result = await Create(handler).DiscoverAsync(McpUrl);

        Assert.True(result.Succeeded);
        Assert.True(result.RequiresAuth);
        Assert.Equal("https://cf.mcp.example.com/v1/token", result.TokenUrl);
        Assert.Equal("https://mcp.example.com/v1/authorize", result.AuthorizationUrl);
        Assert.Equal("https://cf.mcp.example.com/v1/register", result.RegistrationUrl);
        Assert.False(result.SupportsClientCredentials);
        Assert.Contains("Sign in", result.Detail);
    }

    [Fact]
    public async Task The_resource_metadata_route_is_preferred_when_it_exists()
    {
        var handler = new DiscoveryHandler();
        handler.Documents["https://mcp.example.com/.well-known/oauth-protected-resource/v1/sse"] =
            """{"resource":"https://mcp.example.com/v1/sse","authorization_servers":["https://issuer.example.com"],"scopes_supported":["read:things"]}""";
        handler.Documents["https://issuer.example.com/.well-known/oauth-authorization-server"] = AuthServerDocument;

        var result = await Create(handler).DiscoverAsync(McpUrl);

        Assert.True(result.Succeeded);
        Assert.Equal("https://mcp.example.com/v1/sse", result.Resource);
        Assert.Equal("read:things", result.ScopeString);
        Assert.Equal("https://cf.mcp.example.com/v1/token", result.TokenUrl);
    }

    [Fact]
    public async Task A_challenge_that_names_its_metadata_document_is_followed()
    {
        var handler = new DiscoveryHandler();
        handler.ResourceMetadataUrl = "https://elsewhere.example.com/metadata";
        handler.Documents["https://elsewhere.example.com/metadata"] =
            """{"resource":"https://mcp.example.com/v1/sse","authorization_servers":["https://issuer.example.com"]}""";
        handler.Documents["https://issuer.example.com/.well-known/oauth-authorization-server"] = AuthServerDocument;

        var result = await Create(handler).DiscoverAsync(McpUrl);

        Assert.True(result.Succeeded);
        Assert.Equal("https://elsewhere.example.com/metadata", result.ResourceMetadataUrl);
    }

    [Fact]
    public async Task An_endpoint_that_publishes_nothing_says_so_rather_than_guessing()
    {
        var result = await Create(new DiscoveryHandler()).DiscoverAsync(McpUrl);

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresAuth);
        Assert.Contains("by hand", result.Detail);
    }

    [Fact]
    public async Task A_relative_url_is_refused_before_any_request()
    {
        var handler = new DiscoveryHandler();

        var result = await Create(handler).DiscoverAsync("mcp.example.com/v1/sse");

        Assert.False(result.Succeeded);
        Assert.Contains("absolute", result.Detail);
        Assert.Empty(handler.Requested);
    }

    /// <summary>
    /// Answers the initialize POST with a challenge, and GETs from a small table of documents.
    /// Anything not in the table is a 404, which is what the real gaps look like.
    /// </summary>
    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public HttpStatusCode Challenge { get; set; } = HttpStatusCode.Unauthorized;
        public string? ResourceMetadataUrl { get; set; }
        public Dictionary<string, string> Documents { get; } = [];
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(Challenge) { Content = new StringContent("{}") };

                if (Challenge == HttpStatusCode.Unauthorized)
                {
                    response.Headers.TryAddWithoutValidation(
                        "WWW-Authenticate",
                        ResourceMetadataUrl is { Length: > 0 } metadata
                            ? $"Bearer realm=\"OAuth\", resource_metadata=\"{metadata}\""
                            : "Bearer realm=\"OAuth\"");
                }

                return Task.FromResult(response);
            }

            return Task.FromResult(Documents.TryGetValue(url, out var document)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
