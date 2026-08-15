using System.Net;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Common;
using DevStudio.Domain.Mcp;
using DevStudio.Infrastructure.Mcp;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class McpTokenServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-mcp-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<McpServer> _servers;

    /// <summary>Stands in for the app's own MCP token, which the built-in servers are handed.</summary>
    private static readonly IMcpAccessTokenProvider LocalToken = new StubLocalToken("ds_local");

    public McpTokenServiceTests() =>
        _servers = new JsonEntityStore<McpServer>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<McpServer>>.Instance);

    private (McpTokenService Service, TokenHandler Handler) Create(string body = """{"access_token":"tok-1","expires_in":3600}""")
    {
        var handler = new TokenHandler(body);
        return (new McpTokenService(new StubFactory(handler), _servers, LocalToken, NullLogger<McpTokenService>.Instance), handler);
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
        var service = new McpTokenService(new StubFactory(handler), _servers, LocalToken, NullLogger<McpTokenService>.Instance);
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
        var service = new McpTokenService(new StubFactory(handler), _servers, LocalToken, NullLogger<McpTokenService>.Instance);

        var result = await service.TestAsync(OAuthServer());

        Assert.False(result.Succeeded);
        Assert.Contains("access_token", result.Detail);
    }

    [Fact]
    public async Task An_oauth_server_renews_itself_from_its_refresh_token()
    {
        var (service, handler) = Create("""{"access_token":"fresh","refresh_token":"rotated","expires_in":3600}""");
        var server = await _servers.UpsertAsync(new McpServer
        {
            Name = "rovo",
            AuthMode = McpAuthMode.OAuth,
            TokenUrl = "https://issuer.example.com/token",
            ClientId = "abc",
            AccessToken = "expired",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            RefreshToken = "refresh-1",
        });

        var token = await service.GetAccessTokenAsync(server);

        Assert.Equal("fresh", token);
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
        Assert.Contains("refresh_token=refresh-1", handler.LastBody);

        // A rotated refresh token has to replace the old one or the next renewal fails.
        Assert.Equal("rotated", (await _servers.GetAsync(server.Id))!.RefreshToken);
    }

    [Fact]
    public async Task An_oauth_server_nobody_has_signed_in_to_says_so()
    {
        var (service, handler) = Create();

        var result = await service.AcquireAsync(new McpServer
        {
            Name = "rovo",
            AuthMode = McpAuthMode.OAuth,
            TokenUrl = "https://issuer.example.com/token",
        });

        Assert.False(result.Succeeded);
        Assert.Contains("signed in", result.Detail);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_revoked_refresh_token_says_to_sign_in_again()
    {
        var handler = new TokenHandler("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var service = new McpTokenService(new StubFactory(handler), _servers, LocalToken, NullLogger<McpTokenService>.Instance);
        var server = await _servers.UpsertAsync(new McpServer
        {
            Name = "rovo",
            AuthMode = McpAuthMode.OAuth,
            TokenUrl = "https://issuer.example.com/token",
            RefreshToken = "revoked",
        });

        var result = await service.AcquireAsync(server);

        Assert.False(result.Succeeded);
        Assert.Contains("sign in again", result.Detail);
    }

    [Fact]
    public async Task A_valid_oauth_token_is_used_without_contacting_the_issuer()
    {
        var (service, handler) = Create();

        var result = await service.AcquireAsync(new McpServer
        {
            AuthMode = McpAuthMode.OAuth,
            AccessToken = "still-good",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        Assert.True(result.Succeeded);
        Assert.Equal("still-good", result.Token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_built_in_server_is_handed_this_app_s_own_token()
    {
        var (service, handler) = Create();

        var result = await service.AcquireAsync(new McpServer
        {
            Name = "orchestrator",
            Transport = McpTransport.Http,
            Url = "http://localhost:7080/mcp",
            IsBuiltIn = true,
        });

        Assert.True(result.Succeeded);
        Assert.Equal("ds_local", result.Token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Acquiring_for_a_server_with_no_auth_succeeds_with_no_token()
    {
        var (service, _) = Create();

        var result = await service.AcquireAsync(new McpServer { AuthMode = McpAuthMode.None });

        Assert.True(result.Succeeded);
        Assert.Null(result.Token);
    }

    private sealed class StubLocalToken(string token) : IMcpAccessTokenProvider
    {
        public string Current => token;

        public DateTimeOffset? RetiredValidUntil => null;

        public bool Matches(string? presented) => presented == token;

        public McpTokenRotation Rotate(bool immediately = false) => new(token, null);
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
