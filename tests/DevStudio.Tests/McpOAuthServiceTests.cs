using System.Collections.Specialized;
using System.Net;
using System.Web;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Mcp;
using DevStudio.Infrastructure.Mcp;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class McpOAuthServiceTests : IDisposable
{
    private const string RedirectUri = "http://localhost:7080/mcp/oauth/callback";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-oauth-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<McpServer> _servers;

    public McpOAuthServiceTests() =>
        _servers = new JsonEntityStore<McpServer>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<McpServer>>.Instance);

    private (McpOAuthService Service, OAuthHandler Handler) Create(StubDiscovery? discovery = null)
    {
        var handler = new OAuthHandler();
        var service = new McpOAuthService(
            new StubFactory(handler),
            _servers,
            discovery ?? new StubDiscovery(new McpAuthDiscovery(false, "not asked")),
            NullLogger<McpOAuthService>.Instance);

        return (service, handler);
    }

    private async Task<McpServer> SaveServerAsync(Action<McpServer>? adjust = null)
    {
        var server = new McpServer
        {
            Name = "rovo",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/v1/sse",
            AuthMode = McpAuthMode.OAuth,
            AuthorizationUrl = "https://issuer.example.com/authorize",
            TokenUrl = "https://issuer.example.com/token",
            RegistrationUrl = "https://issuer.example.com/register",
        };

        adjust?.Invoke(server);
        return await _servers.UpsertAsync(server);
    }

    private static NameValueCollection QueryOf(string url) =>
        HttpUtility.ParseQueryString(new Uri(url).Query);

    [Fact]
    public async Task An_unsaved_server_cannot_be_signed_in_to()
    {
        var (service, _) = Create();

        var result = await service.BeginAsync(new McpServer { Id = string.Empty }, RedirectUri);

        Assert.False(result.Succeeded);
        Assert.Contains("Save the server", result.Detail);
    }

    [Fact]
    public async Task Beginning_registers_a_client_when_none_has_been_entered()
    {
        var (service, handler) = Create();
        var server = await SaveServerAsync();

        var result = await service.BeginAsync(server, RedirectUri);

        Assert.True(result.Succeeded);
        Assert.Equal("https://issuer.example.com/register", handler.Requests[0].Url);

        // The redirect the issuer will be asked to honour has to be the one it was registered with.
        Assert.Contains(RedirectUri, handler.Requests[0].Body);

        var saved = await _servers.GetAsync(server.Id);
        Assert.Equal("registered-client", saved!.ClientId);
    }

    [Fact]
    public async Task A_client_id_that_is_already_set_is_not_registered_again()
    {
        var (service, handler) = Create();
        var server = await SaveServerAsync(s => s.ClientId = "mine");

        var result = await service.BeginAsync(server, RedirectUri);

        Assert.True(result.Succeeded);
        Assert.Empty(handler.Requests);
        Assert.Equal("mine", QueryOf(result.AuthorizationUrl!)["client_id"]);
    }

    [Fact]
    public async Task The_authorize_url_carries_pkce_and_the_configured_scopes()
    {
        var (service, _) = Create();
        var server = await SaveServerAsync(s =>
        {
            s.ClientId = "mine";
            s.Scopes = "read:jira-work offline_access";
        });

        var result = await service.BeginAsync(server, RedirectUri);
        var query = QueryOf(result.AuthorizationUrl!);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.Equal("read:jira-work offline_access", query["scope"]);

        // The verifier itself must never travel with the authorization request.
        Assert.Null(query["code_verifier"]);
    }

    [Fact]
    public async Task Endpoints_are_discovered_when_the_record_has_none()
    {
        var discovery = new StubDiscovery(new McpAuthDiscovery(
            true,
            "found",
            RequiresAuth: true,
            TokenUrl: "https://found.example.com/token",
            AuthorizationUrl: "https://found.example.com/authorize",
            RegistrationUrl: "https://found.example.com/register"));

        var (service, _) = Create(discovery);
        var server = await SaveServerAsync(s =>
        {
            s.AuthorizationUrl = string.Empty;
            s.TokenUrl = string.Empty;
            s.RegistrationUrl = string.Empty;
            s.ClientId = "mine";
        });

        var result = await service.BeginAsync(server, RedirectUri);

        Assert.True(result.Succeeded);
        Assert.StartsWith("https://found.example.com/authorize", result.AuthorizationUrl);
        Assert.Equal("https://found.example.com/token", (await _servers.GetAsync(server.Id))!.TokenUrl);
    }

    [Fact]
    public async Task A_server_with_no_registration_endpoint_and_no_client_id_says_what_is_missing()
    {
        var (service, _) = Create();
        var server = await SaveServerAsync(s => s.RegistrationUrl = string.Empty);

        var result = await service.BeginAsync(server, RedirectUri);

        Assert.False(result.Succeeded);
        Assert.Contains("client id", result.Detail);
    }

    [Fact]
    public async Task Completing_exchanges_the_code_and_stores_both_tokens()
    {
        var (service, handler) = Create();
        var server = await SaveServerAsync(s => s.ClientId = "mine");

        var start = await service.BeginAsync(server, RedirectUri);
        var state = QueryOf(start.AuthorizationUrl!)["state"]!;

        var result = await service.CompleteAsync(state, "the-code", null, null);

        Assert.True(result.Succeeded);

        var exchange = handler.Requests[^1];
        Assert.Equal("https://issuer.example.com/token", exchange.Url);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
        Assert.Contains("code=the-code", exchange.Body);
        Assert.Contains("code_verifier=", exchange.Body);

        var saved = await _servers.GetAsync(server.Id);
        Assert.Equal("access-1", saved!.AccessToken);
        Assert.Equal("refresh-1", saved.RefreshToken);
        Assert.NotNull(saved.AuthorizedAt);
        Assert.Equal(McpAuthMode.OAuth, saved.AuthMode);
    }

    [Fact]
    public async Task A_state_that_was_never_issued_is_refused()
    {
        var (service, handler) = Create();

        var result = await service.CompleteAsync("invented", "the-code", null, null);

        Assert.False(result.Succeeded);
        Assert.Contains("expired", result.Detail);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_state_cannot_be_replayed()
    {
        var (service, _) = Create();
        var server = await SaveServerAsync(s => s.ClientId = "mine");

        var start = await service.BeginAsync(server, RedirectUri);
        var state = QueryOf(start.AuthorizationUrl!)["state"]!;

        Assert.True((await service.CompleteAsync(state, "the-code", null, null)).Succeeded);

        var replay = await service.CompleteAsync(state, "the-code", null, null);

        Assert.False(replay.Succeeded);
        Assert.Contains("expired", replay.Detail);
    }

    [Fact]
    public async Task An_issuer_that_refuses_reports_its_own_reason()
    {
        var (service, _) = Create();
        var server = await SaveServerAsync(s => s.ClientId = "mine");

        var start = await service.BeginAsync(server, RedirectUri);
        var state = QueryOf(start.AuthorizationUrl!)["state"]!;

        var result = await service.CompleteAsync(state, null, "access_denied", "The user said no");

        Assert.False(result.Succeeded);
        Assert.Contains("access_denied", result.Detail);
        Assert.Contains("The user said no", result.Detail);
    }

    [Fact]
    public async Task Signing_out_clears_the_tokens()
    {
        var (service, _) = Create();
        var server = await SaveServerAsync(s =>
        {
            s.AccessToken = "access-1";
            s.RefreshToken = "refresh-1";
            s.AuthorizedAt = DateTimeOffset.UtcNow;
        });

        await service.SignOutAsync(server);

        var saved = await _servers.GetAsync(server.Id);
        Assert.Equal(string.Empty, saved!.AccessToken);
        Assert.Equal(string.Empty, saved.RefreshToken);
        Assert.Null(saved.AuthorizedAt);

        // The caller's own copy is cleared too, so the page does not keep showing a stale sign-in.
        Assert.Equal(string.Empty, server.AccessToken);
    }

    private sealed record Sent(string Url, string Body);

    /// <summary>Registration answers with a client id; the token endpoint answers with a token pair.</summary>
    private sealed class OAuthHandler : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(url, body));

            var json = url.Contains("/register", StringComparison.Ordinal)
                ? """{"client_id":"registered-client"}"""
                : """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubDiscovery(McpAuthDiscovery result) : IMcpAuthDiscovery
    {
        public Task<McpAuthDiscovery> DiscoverAsync(string mcpUrl, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
