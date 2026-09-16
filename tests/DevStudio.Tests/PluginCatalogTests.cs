using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// What is installed is whatever the CLIs themselves recorded, so these pin the files this app
/// reads. Getting one wrong does not fail loudly: the plugin simply never appears for an agent to
/// switch on, and the agent quietly runs without it.
/// </summary>
public class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-plugins-" + Guid.NewGuid().ToString("n"));

    public PluginCatalogTests() => Directory.CreateDirectory(_root);

    private string Home(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private PluginCatalog CatalogFor(string home)
    {
        var options = Options.Create(new OrchestratorOptions { HomePath = home, DataPath = Path.Combine(_root, "data") });

        return new PluginCatalog(
            new JsonEntityStore<ProviderAccount>(options, NullLogger<JsonEntityStore<ProviderAccount>>.Instance),
            options,
            NullLogger<PluginCatalog>.Instance);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Claude_installs_are_read_with_whether_they_are_on()
    {
        var home = Home("claude");
        Write(
            Path.Combine(home, ".claude", "plugins", "installed_plugins.json"),
            """
            {
              "version": 2,
              "plugins": {
                "frontend-design@claude-plugins-official": [{ "scope": "user" }],
                "github@claude-plugins-official": [{ "scope": "user" }]
              }
            }
            """);
        Write(
            Path.Combine(home, ".claude", "settings.json"),
            """
            { "enabledPlugins": { "frontend-design@claude-plugins-official": true } }
            """);

        var plugins = await CatalogFor(home).GetAllAsync();

        Assert.Equal(2, plugins.Count);
        Assert.All(plugins, p => Assert.Equal(AiProvider.Claude, p.Key.Provider));
        Assert.True(plugins.Single(p => p.Key.ShortName == "frontend-design").EnabledByDefault);
        Assert.False(plugins.Single(p => p.Key.ShortName == "github").EnabledByDefault);
        Assert.Equal("claude-plugins-official", plugins[0].Key.Marketplace);
    }

    /// <summary>A plugin enabled in settings but not in the install record is still a plugin.</summary>
    [Fact]
    public async Task A_claude_plugin_known_only_to_the_settings_file_is_still_offered()
    {
        var home = Home("synced");
        Write(
            Path.Combine(home, ".claude", "settings.json"),
            """
            { "enabledPlugins": { "security-guidance@claude-plugins-official": true } }
            """);

        var plugins = await CatalogFor(home).GetAllAsync();

        Assert.Equal("claude:security-guidance@claude-plugins-official", Assert.Single(plugins).Id);
    }

    [Fact]
    public async Task Codex_installs_are_the_entries_in_its_config_and_it_says_which_are_off()
    {
        var home = Home("codex");

        // Downloaded but never installed: the cache holds everything codex has ever fetched,
        // including plugins built for one workspace, and none of that is something to switch on.
        Directory.CreateDirectory(Path.Combine(home, ".codex", "plugins", "cache", "workspace-directory", "dev-6a06", "1.0.0"));

        Write(
            Path.Combine(home, ".codex", "config.toml"),
            """
            model = "gpt-5"

            [marketplaces.openai-bundled]
            source_type = "local"

            [plugins."codex-security@openai-curated"]
            enabled = false

            [plugins."gmail@openai-curated"]
            enabled = true

            [plugins."gmail@openai-curated".mcp_servers.docs]
            default_tools_approval_mode = "prompt"
            """);

        var plugins = await CatalogFor(home).GetAllAsync();

        Assert.Equal(2, plugins.Count);
        Assert.All(plugins, p => Assert.Equal(AiProvider.Codex, p.Key.Provider));
        Assert.True(plugins.Single(p => p.Key.ShortName == "gmail").EnabledByDefault);
        Assert.False(plugins.Single(p => p.Key.ShortName == "codex-security").EnabledByDefault);
    }

    [Fact]
    public async Task Opencode_plugins_are_the_packages_its_config_names()
    {
        var home = Home("opencode");
        Write(
            Path.Combine(home, ".config", "opencode", "opencode.json"),
            """
            {
              "$schema": "https://opencode.ai/config.json",
              "plugin": ["opencode-wakatime", "@my-org/custom-plugin"]
            }
            """);

        var plugins = await CatalogFor(home).GetAllAsync();

        Assert.Equal(
            ["opencode:@my-org/custom-plugin", "opencode:opencode-wakatime"],
            plugins.Select(p => p.Id).Order().ToArray());
        Assert.All(plugins, p => Assert.True(p.EnabledByDefault));
    }

    /// <summary>
    /// A machine with nothing installed is the ordinary case, and reading it must not throw: the
    /// agent editor asks for this every time it opens.
    /// </summary>
    [Fact]
    public async Task A_machine_with_no_clis_configured_has_no_plugins()
    {
        Assert.Empty(await CatalogFor(Home("empty")).GetAllAsync());
    }

    [Fact]
    public async Task Unreadable_configuration_is_treated_as_nothing_installed()
    {
        var home = Home("broken");
        Write(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"), "{ not json");

        Assert.Empty(await CatalogFor(home).GetAllAsync());
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
