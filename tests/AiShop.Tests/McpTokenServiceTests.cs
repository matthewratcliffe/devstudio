using System.Net;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Common;
using AiShop.Domain.Mcp;
using AiShop.Infrastructure.Mcp;
using AiShop.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiShop.Tests;

public class McpTokenServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aishop-mcp-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<McpServer> _servers;

    public McpTokenServiceTests() =>
        _servers = new JsonEntityStore<McpServer>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<McpServer>>.Instance);

    private (McpTokenService Service, TokenHandler Handler) Create(string body = """{"access_token":"tok-1","expires_in":3600}""")
    {
        var handler = new TokenHandler(body);
        return (new McpTokenService(new StubFactory(handler), _servers, NullLogger<McpTokenService>.Instance), handler);
    }

    private static McpServer OAuthServer() => new()
    {
        Name = "secure",
        Transport = McpTransport.Http,
        Url = "https://example.com/mcp",
        AuthMode = McpAuthMode.ClientCredentials,
        TokenUrl = "https://issuer.example.com/token",
        ClientId = "abc",
        ClientSecret = "shh",
        Scopes = "mcp:read mcp:invoke",
    };

    [Fact]
    public async Task Servers_without_auth_get_no_token()
    {
        var (service, handler) = Create();

        var token = await service.GetAccessTokenAsync(new McpServer { AuthMode = McpAuthMode.None });

        Assert.Null(token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_pasted_bearer_token_is_used_as_is()
    {
        var (service, handler) = Create();

        var token = await service.GetAccessTokenAsync(new McpServer
        {
            AuthMode = McpAuthMode.BearerToken,
            AccessToken = "pasted-token",
        });

        Assert.Equal("pasted-token", token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task The_client_credentials_grant_is_posted_with_the_configured_values()
    {
        var (service, handler) = Create();
        var server = await _servers.UpsertAsync(OAuthServer());

        var token = await service.GetAccessTokenAsync(server);

        Assert.Equal("tok-1", token);
        Assert.Equal("https://issuer.example.com/token", handler.LastUrl);
        Assert.Contains("grant_type=client_credentials", handler.LastBody);
        Assert.Contains("client_id=abc", handler.LastBody);
        Assert.Contains("client_secret=shh", handler.LastBody);
        Assert.Contains("scope=mcp", handler.LastBody);
    }

    [Fact]
    public async Task A_cached_token_is_reused_until_it_is_close_to_expiry()
    {
        var (service, handler) = Create();
        var server = await _servers.UpsertAsync(OAuthServer());

        await service.GetAccessTokenAsync(server);
        await service.GetAccessTokenAsync(server);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task An_expired_token_is_fetched_again()
    {
        var (service, handler) = Create();
        var server = OAuthServer();
        server.AccessToken = "stale";
        server.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5); // inside the refresh leeway
        await _servers.UpsertAsync(server);

        var token = await service.GetAccessTokenAsync(server);

        Assert.Equal("tok-1", token);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_rejected_grant_reports_the_status_and_yields_no_token()
    {
        var handler = new TokenHandler("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized);
        var service = new McpTokenService(new StubFactory(handler), _servers, NullLogger<McpTokenService>.Instance);
        var server = await _servers.UpsertAsync(OAuthServer());

        var result = await service.TestAsync(server);

        Assert.False(result.Succeeded);
        Assert.Contains("401", result.Detail);
        Assert.Null(await service.GetAccessTokenAsync(server));
    }

    [Fact]
    public async Task A_response_without_a_token_is_an_error_rather_than_a_silent_pass()
    {
        var handler = new TokenHandler("""{"nothing":"here"}""");
        var service = new McpTokenService(new StubFactory(handler), _servers, NullLogger<McpTokenService>.Instance);

        var result = await service.TestAsync(OAuthServer());

        Assert.False(result.Succeeded);
        Assert.Contains("access_token", result.Detail);
    }

    private sealed class TokenHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
