using System.Net;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Mcp;
using DevStudio.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class McpProbeServiceTests
{
    private static McpServer HttpServer() => new()
    {
        Name = "freshdesk",
        Transport = McpTransport.Http,
        Url = "https://mcp.example.com/",
        Headers = new Dictionary<string, string> { ["X-API-Key"] = "key-1" },
    };

    private static (McpProbeService Service, McpHandler Handler) Create(params string[] responses) =>
        Create(new NoTokens(), responses);

    private static (McpProbeService Service, McpHandler Handler) Create(IMcpTokenService tokens, params string[] responses)
    {
        var handler = new McpHandler(responses);
        var service = new McpProbeService(
            new StubFactory(handler),
            tokens,
            Options.Create(new OrchestratorOptions()),
            NullLogger<McpProbeService>.Instance);

        return (service, handler);
    }

    private const string InitializeOk =
        """{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"freshdesk-mcp","version":"2.1"}}}""";

    [Fact]
    public async Task Listing_tools_reports_the_server_and_its_tools()
    {
        var (service, _) = Create(
            InitializeOk,
            "{}",
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"get_ticket","description":"Reads a ticket","inputSchema":{"type":"object"}}]}}""");

        var result = await service.ListToolsAsync(HttpServer());

        Assert.True(result.Succeeded);
        Assert.Equal("freshdesk-mcp", result.ServerName);
        var tool = Assert.Single(result.Tools);
        Assert.Equal("get_ticket", tool.Name);
        Assert.Contains("\"type\"", tool.InputSchema);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_with_its_status_and_body()
    {
        var (service, _) = Create();

        var result = await service.ListToolsAsync(HttpServer());

        Assert.False(result.Succeeded);
        Assert.Contains("401", result.Detail);
        Assert.Contains("unauthorized", result.Detail);
    }

    [Fact]
    public async Task A_grant_that_failed_is_reported_instead_of_the_servers_401()
    {
        // Without this the request goes out with no credential at all and the server's 401 gets the
        // blame, which sends the operator looking at the wrong end of the problem.
        var (service, handler) = Create(new FailingTokens("The token endpoint returned 401."));

        var result = await service.ListToolsAsync(HttpServer());

        Assert.False(result.Succeeded);
        Assert.Contains("No token could be obtained", result.Detail);
        Assert.Contains("token endpoint returned 401", result.Detail);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_401_against_a_server_with_no_auth_mode_explains_that_the_cli_signs_in_itself()
    {
        var server = HttpServer();
        server.AuthMode = McpAuthMode.None;

        var (service, _) = Create();

        var result = await service.ListToolsAsync(server);

        Assert.False(result.Succeeded);
        Assert.Contains("401", result.Detail);
        Assert.Contains("auth mode is None", result.Detail);
        Assert.Contains("CLI", result.Detail);
    }

    [Fact]
    public async Task A_401_against_a_pasted_token_says_it_has_probably_expired()
    {
        var server = HttpServer();
        server.AuthMode = McpAuthMode.BearerToken;
        server.AccessToken = "stale";

        var (service, _) = Create(new StubTokens("stale"));

        var result = await service.ListToolsAsync(server);

        Assert.False(result.Succeeded);
        Assert.Contains("expire", result.Detail);
    }

    [Fact]
    public async Task Running_a_tool_sends_its_name_and_arguments()
    {
        var (service, handler) = Create(
            InitializeOk,
            "{}",
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"Ticket 42: open"}]}}""");

        var result = await service.CallToolAsync(HttpServer(), "get_ticket", """{"id":42}""");

        Assert.True(result.Succeeded);
        Assert.Equal("Ticket 42: open", result.Content);

        var sent = JsonNode.Parse(handler.Bodies[^1])!;
        Assert.Equal("tools/call", sent["method"]!.GetValue<string>());
        Assert.Equal("get_ticket", sent["params"]!["name"]!.GetValue<string>());
        Assert.Equal(42, sent["params"]!["arguments"]!["id"]!.GetValue<int>());
        Assert.Equal("key-1", handler.LastApiKey);
    }

    [Fact]
    public async Task A_tool_that_reports_an_error_is_not_a_success()
    {
        var (service, _) = Create(
            InitializeOk,
            "{}",
            """{"jsonrpc":"2.0","id":2,"result":{"isError":true,"content":[{"type":"text","text":"Ticket not found"}]}}""");

        var result = await service.CallToolAsync(HttpServer(), "get_ticket", """{"id":0}""");

        Assert.False(result.Succeeded);
        Assert.Equal("Ticket not found", result.Content);
    }

    [Fact]
    public async Task Arguments_that_are_not_an_object_are_refused_before_connecting()
    {
        var (service, handler) = Create();

        var result = await service.CallToolAsync(HttpServer(), "get_ticket", "[1,2,3]");

        Assert.False(result.Succeeded);
        Assert.Contains("JSON object", result.Detail);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Empty_arguments_run_the_tool_with_none()
    {
        var (service, handler) = Create(
            InitializeOk,
            "{}",
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ok"}]}}""");

        var result = await service.CallToolAsync(HttpServer(), "list_tickets", "   ");

        Assert.True(result.Succeeded);
        var sent = JsonNode.Parse(handler.Bodies[^1])!;
        Assert.Empty(sent["params"]!["arguments"]!.AsObject());
    }

    [Fact]
    public async Task An_sse_answer_is_understood_as_well_as_plain_json()
    {
        var (service, _) = Create(
            InitializeOk,
            "{}",
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"streamed\"}]}}\n\n");

        var result = await service.CallToolAsync(HttpServer(), "get_ticket", "{}");

        Assert.True(result.Succeeded);
        Assert.Equal("streamed", result.Content);
    }

    /// <summary>Answers each POST in turn; with nothing queued it behaves like a server rejecting the key.</summary>
    private sealed class McpHandler(string[] responses) : HttpMessageHandler
    {
        private int _index;

        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];
        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var values)
                ? values.FirstOrDefault()
                : null;

            if (_index >= responses.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"unauthorized"}"""),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[_index++]) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A grant that could not be performed at all — no token, and a reason why.</summary>
    private sealed class FailingTokens(string detail) : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(false, null, detail));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(false, null, detail));
    }

    /// <summary>A grant that worked, so the refusal comes from the server rather than the issuer.</summary>
    private sealed class StubTokens(string token) : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(token);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, token, "ok"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, token, "ok"));
    }

    private sealed class NoTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "not used"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "not used"));
    }
}
