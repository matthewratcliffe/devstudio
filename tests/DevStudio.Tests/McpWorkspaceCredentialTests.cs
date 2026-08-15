using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Skills;
using DevStudio.Infrastructure.Mcp;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The orchestrator's own MCP endpoints refuse anything without its token, so the whole scheme rests
/// on that token reaching the workspace config without anyone being asked for it.
/// </summary>
public class McpWorkspaceCredentialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-mcpcred-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;
    private readonly IOptions<OrchestratorOptions> _options;
    private readonly JsonEntityStore<McpServer> _servers;
    private readonly McpAccessTokenProvider _accessToken;
    private readonly WorkspaceService _service;

    public McpWorkspaceCredentialTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);

        _options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });
        _servers = Store<McpServer>();
        _accessToken = new McpAccessTokenProvider(_options, NullLogger<McpAccessTokenProvider>.Instance);

        var tokens = new McpTokenService(
            new UnusedClients(),
            _servers,
            _accessToken,
            NullLogger<McpTokenService>.Instance);

        // Git is only reached when a whole workspace is provisioned; writing .mcp.json never touches it.
        _service = new WorkspaceService(
            null!,
            Store<GitRepository>(),
            Store<Skill>(),
            _servers,
            tokens,
            Store<Project>(),
            Store<GlobalSettings>(),
            _options,
            NullLogger<WorkspaceService>.Instance);
    }

    private JsonEntityStore<T> Store<T>() where T : class, IEntity =>
        new(_options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task The_built_in_server_reaches_the_agent_with_the_managed_token_attached()
    {
        var server = await _servers.UpsertAsync(new McpServer
        {
            Name = "orchestrator",
            Transport = McpTransport.Http,
            Url = "http://localhost:7080/mcp",
            IsBuiltIn = true,
        });

        await _service.MaterialiseMcpAsync(new Agent { McpServerIds = [server.Id] }, _workspace);

        Assert.Equal(
            $"Bearer {_accessToken.Current}",
            Header(await File.ReadAllTextAsync(Path.Combine(_workspace, ".mcp.json"))));
    }

    [Fact]
    public async Task A_rotated_token_is_what_the_next_turn_is_given()
    {
        var server = await _servers.UpsertAsync(new McpServer
        {
            Name = "orchestrator",
            Transport = McpTransport.Http,
            Url = "http://localhost:7080/mcp",
            IsBuiltIn = true,
        });

        await _service.MaterialiseMcpAsync(new Agent { McpServerIds = [server.Id] }, _workspace);
        var rotated = _accessToken.Rotate().Token;
        await _service.MaterialiseMcpAsync(new Agent { McpServerIds = [server.Id] }, _workspace);

        // Nothing on the record had to be rewritten for this: the token is read when the config is,
        // and the config is written before every turn.
        Assert.Equal($"Bearer {rotated}", Header(await File.ReadAllTextAsync(Path.Combine(_workspace, ".mcp.json"))));
    }

    [Fact]
    public async Task The_config_a_running_turn_already_read_still_authenticates()
    {
        var server = await _servers.UpsertAsync(new McpServer
        {
            Name = "orchestrator",
            Transport = McpTransport.Http,
            Url = "http://localhost:7080/mcp",
            IsBuiltIn = true,
        });

        await _service.MaterialiseMcpAsync(new Agent { McpServerIds = [server.Id] }, _workspace);
        var inFlight = Header(await File.ReadAllTextAsync(Path.Combine(_workspace, ".mcp.json")))!;

        _accessToken.Rotate();

        // The CLI is holding this value and cannot be told otherwise, so a rotation must not be what
        // ends its turn. Rewriting the file would not help either — it was read at process start.
        Assert.True(_accessToken.Matches(inFlight["Bearer ".Length..]));
    }

    private static string? Header(string config) =>
        JsonNode.Parse(config)?["mcpServers"]?["orchestrator"]?["headers"]?["Authorization"]?.GetValue<string>();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>A built-in server never reaches an issuer, so any HTTP call here is a failure.</summary>
    private sealed class UnusedClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
