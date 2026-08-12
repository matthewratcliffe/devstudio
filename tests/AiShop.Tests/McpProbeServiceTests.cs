using System.Net;
using System.Text.Json.Nodes;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Mcp;
using AiShop.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiShop.Tests;

public class McpProbeServiceTests
{
    private static McpServer HttpServer() => new()
    {
        Name = "freshdesk",
        Transport = McpTransport.Http,
        Url = "https://mcp.example.com/",
        Headers = new Dictionary<string, string> { ["X-API-Key"] = "key-1" },
    };

    private static (McpProbeService Service, McpHandler Handler) Create(params string[] responses)
    {
        var handler = new McpHandler(responses);
        var service = new McpProbeService(
            new StubFactory(handler),
            new NoTokens(),
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

    private sealed class NoTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "not used"));
    }
}
