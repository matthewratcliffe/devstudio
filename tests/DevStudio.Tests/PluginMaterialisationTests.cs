using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Plugins;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Skills;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Plugins;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// An agent's plugin choice only means anything if the session is told about every installed plugin,
/// on or off. Leave the unwanted ones out and whatever a login enabled globally comes along with
/// every agent, which is the failure this feature exists to stop.
/// </summary>
public class PluginMaterialisationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-pluginwrite-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;

    public PluginMaterialisationTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    private WorkspaceService ServiceWith(params InstalledPlugin[] installed)
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });

        return new WorkspaceService(
            null!,
            Store<GitRepository>(options),
            Store<Skill>(options),
            Store<McpServer>(options),
            new StubTokens(),
            new StubPluginCatalog(installed),
            Store<Project>(options),
            Store<GlobalSettings>(options),
            new StubStandardsFilesSyncService(),
            options,
            NullLogger<WorkspaceService>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    private Dictionary<string, bool> Written()
    {
        var entries = JsonNode.Parse(File.ReadAllText(WorkspacePlugins.PathFor(_workspace)))!["plugins"]!.AsArray();

        return entries.ToDictionary(
            e => e!["name"]!.GetValue<string>(),
            e => e!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task What_the_agent_chose_is_on_and_everything_else_installed_is_off()
    {
        var service = ServiceWith(
            StubPluginCatalog.Installed(AiProvider.Claude, "github@claude-plugins-official"),
            StubPluginCatalog.Installed(AiProvider.Claude, "security-guidance@claude-plugins-official", enabled: true));

        await service.MaterialisePluginsAsync(
            new Agent { Provider = AiProvider.Claude, PluginIds = ["claude:github@claude-plugins-official"] },
            _workspace);

        var written = Written();
        Assert.True(written["github@claude-plugins-official"]);
        Assert.False(written["security-guidance@claude-plugins-official"]);
    }

    /// <summary>Each CLI has its own plugins; one agent drives one CLI.</summary>
    [Fact]
    public async Task Only_the_agents_own_cli_is_configured()
    {
        var service = ServiceWith(
            StubPluginCatalog.Installed(AiProvider.Claude, "github@claude-plugins-official"),
            StubPluginCatalog.Installed(AiProvider.Codex, "gmail@openai-curated"));

        await service.MaterialisePluginsAsync(
            new Agent { Provider = AiProvider.Codex, PluginIds = ["codex:gmail@openai-curated"] },
            _workspace);

        Assert.Equal(["gmail@openai-curated"], Written().Keys.ToArray());
    }

    /// <summary>
    /// A machine that does not have the plugin yet — a remote, or one installed since — still gets
    /// told to switch it on, so the agent's definition does not silently lose it.
    /// </summary>
    [Fact]
    public async Task A_chosen_plugin_that_is_not_installed_here_is_still_switched_on()
    {
        await ServiceWith().MaterialisePluginsAsync(
            new Agent { Provider = AiProvider.Claude, PluginIds = ["claude:linear@claude-plugins-official"] },
            _workspace);

        Assert.True(Written()["linear@claude-plugins-official"]);
    }

    [Fact]
    public async Task A_custom_cli_has_nothing_to_configure()
    {
        File.WriteAllText(WorkspacePlugins.PathFor(_workspace), "{}");

        await ServiceWith(StubPluginCatalog.Installed(AiProvider.Claude, "github@claude-plugins-official"))
            .MaterialisePluginsAsync(new Agent { Provider = AiProvider.Custom }, _workspace);

        // Left behind, the previous session's file would be obeyed by the next CLI to read it.
        Assert.False(File.Exists(WorkspacePlugins.PathFor(_workspace)));
    }

    [Fact]
    public void Claude_is_handed_the_selection_as_a_settings_file()
    {
        WorkspacePlugins.Write(_workspace, [
            new WorkspacePlugin(new PluginKey(AiProvider.Claude, "github@claude-plugins-official"), true),
            new WorkspacePlugin(new PluginKey(AiProvider.Claude, "linear@claude-plugins-official"), false),
        ]);

        var path = ClaudePluginSettings.Write(_workspace);

        var enabled = JsonNode.Parse(File.ReadAllText(path!))!["enabledPlugins"]!.AsObject();
        Assert.True(enabled["github@claude-plugins-official"]!.GetValue<bool>());
        Assert.False(enabled["linear@claude-plugins-official"]!.GetValue<bool>());
    }

    [Fact]
    public void Opencode_gets_the_enabled_packages_and_keeps_the_rest_of_its_config()
    {
        File.WriteAllText(
            Path.Combine(_workspace, OpencodePluginConfig.FileName),
            """
            { "model": "anthropic/claude-sonnet-5", "plugin": ["opencode-wakatime"] }
            """);

        WorkspacePlugins.Write(_workspace, [
            new WorkspacePlugin(new PluginKey(AiProvider.Opencode, "opencode-helicone-session"), true),
            new WorkspacePlugin(new PluginKey(AiProvider.Opencode, "opencode-wakatime"), false),
        ]);

        var config = JsonNode.Parse(File.ReadAllText(OpencodePluginConfig.Write(_workspace)!))!.AsObject();

        Assert.Equal("anthropic/claude-sonnet-5", config["model"]!.GetValue<string>());
        Assert.Equal(
            ["opencode-helicone-session"],
            config["plugin"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray());
    }

    /// <summary>Nothing staged means nothing to say: a workspace's own config is left alone.</summary>
    [Fact]
    public void Opencode_config_is_untouched_when_the_session_has_no_plugin_selection()
    {
        var path = Path.Combine(_workspace, OpencodePluginConfig.FileName);
        File.WriteAllText(path, """{ "plugin": ["opencode-wakatime"] }""");

        Assert.Null(OpencodePluginConfig.Write(_workspace));
        Assert.Contains("opencode-wakatime", File.ReadAllText(path));
    }

    private sealed class StubTokens : IMcpTokenService
    {
        public Task<string?> GetAccessTokenAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<McpTokenResult> AcquireAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));

        public Task<McpTokenResult> TestAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpTokenResult(true, null, "stub"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
