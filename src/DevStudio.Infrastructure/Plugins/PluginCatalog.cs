using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Plugins;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Plugins;

/// <summary>
/// Reads what each CLI on this machine has installed, straight out of the files the CLIs keep for
/// themselves. Nothing is written here and nothing is mirrored into this app's own storage: a
/// plugin is installed by its CLI, and a second list of them would only ever be one install behind.
///
/// Each login has its own home — that is what switching account means here — so every home is read
/// and the results are merged. A plugin installed for one login and not another is still offered,
/// because the agent that uses it may well be the one pinned to that login; the origin says which
/// home it came from so the choice is an informed one.
/// </summary>
public sealed class PluginCatalog : IPluginCatalog
{
    private readonly IEntityStore<ProviderAccount> _accounts;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<PluginCatalog> _logger;

    public PluginCatalog(
        IEntityStore<ProviderAccount> accounts,
        IOptions<OrchestratorOptions> options,
        ILogger<PluginCatalog> logger)
    {
        _accounts = accounts;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<InstalledPlugin>> GetAllAsync(CancellationToken ct = default)
    {
        var found = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var home in await HomesAsync(ct))
        {
            foreach (var plugin in ReadHome(home))
            {
                // The first home to offer it owns the origin; a later one can still switch it on,
                // because enabled anywhere is enabled for the agent that runs as that login.
                if (found.TryGetValue(plugin.Id, out var existing))
                {
                    if (plugin.EnabledByDefault && !existing.EnabledByDefault)
                        found[plugin.Id] = existing with { EnabledByDefault = true };

                    continue;
                }

                found[plugin.Id] = plugin;
            }
        }

        return found.Values
            .OrderBy(p => p.Key.Provider)
            .ThenBy(p => p.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every home a CLI on this machine could be running with, the process's own included.</summary>
    private async Task<IReadOnlyList<string>> HomesAsync(CancellationToken ct)
    {
        var homes = new List<string> { _options.HomePath };

        foreach (var account in await _accounts.GetAllAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(account.HomePath))
                homes.Add(account.HomePath);
        }

        return homes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<InstalledPlugin> ReadHome(string home)
    {
        var origin = Label(home);

        return ReadClaude(home, origin)
            .Concat(ReadCodex(home, origin))
            .Concat(ReadOpencode(home, origin));
    }

    /// <summary>
    /// claude records installs in <c>~/.claude/plugins/installed_plugins.json</c>, keyed exactly the
    /// way <c>/plugin enable</c> and the <c>enabledPlugins</c> setting name them. Anything the
    /// settings file mentions is listed as well, which is what picks up a plugin that arrived from
    /// an account sync rather than from a marketplace install.
    /// </summary>
    private IEnumerable<InstalledPlugin> ReadClaude(string home, string origin)
    {
        var enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (ReadJson(Path.Combine(home, ".claude", "settings.json")) is JsonObject settings
            && settings["enabledPlugins"] is JsonObject enabledPlugins)
        {
            foreach (var (id, value) in enabledPlugins)
                enabled[id] = value?.GetValue<bool>() ?? false;
        }

        var installed = new List<string>();

        if (ReadJson(Path.Combine(home, ".claude", "plugins", "installed_plugins.json")) is JsonObject file
            && file["plugins"] is JsonObject plugins)
        {
            installed.AddRange(plugins.Select(pair => pair.Key));
        }

        foreach (var id in installed.Concat(enabled.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new InstalledPlugin(
                new PluginKey(AiProvider.Claude, id),
                origin,
                enabled.TryGetValue(id, out var on) && on);
        }
    }

    /// <summary>
    /// codex writes an install into <c>config.toml</c> as <c>[plugins."name@marketplace"]</c>, and
    /// switches one off by setting <c>enabled = false</c> there, so the config is the whole list.
    ///
    /// Its plugin cache under <c>~/.codex/plugins/cache</c> is deliberately not read: it holds
    /// everything codex has ever downloaded, including a plugin built for one workspace and the
    /// marketplaces it merely looked at, and offering those as things to switch on would bury the
    /// handful actually installed.
    /// </summary>
    private IEnumerable<InstalledPlugin> ReadCodex(string home, string origin)
    {
        foreach (var (id, enabled) in ReadCodexConfig(Path.Combine(home, ".codex", "config.toml")))
            yield return new InstalledPlugin(new PluginKey(AiProvider.Codex, id), origin, enabled);
    }

    /// <summary>
    /// opencode has no install registry: a plugin is an npm package or a local file named in the
    /// <c>plugin</c> array of its config. Files dropped into its plugin directory load
    /// unconditionally and cannot be switched off for one project, so they are deliberately not
    /// listed — offering a toggle that does nothing would be worse than offering none.
    /// </summary>
    private IEnumerable<InstalledPlugin> ReadOpencode(string home, string origin)
    {
        foreach (var file in OpencodeConfigPaths(home))
        {
            if (ReadJson(file) is not JsonObject config || config["plugin"] is not JsonArray plugins)
                continue;

            foreach (var entry in plugins)
            {
                if (entry?.GetValue<string>() is { Length: > 0 } package)
                    yield return new InstalledPlugin(new PluginKey(AiProvider.Opencode, package), origin, true);
            }
        }
    }

    private static IEnumerable<string> OpencodeConfigPaths(string home)
    {
        yield return Path.Combine(home, ".config", "opencode", "opencode.json");
        yield return Path.Combine(home, ".config", "opencode", "opencode.jsonc");
    }

    /// <summary>
    /// Pulls <c>[plugins."name@marketplace"] enabled = false</c> out of codex's config without
    /// taking on a TOML parser for it. Only that one shape is read: a section under
    /// <c>plugins.</c> and, until the next section, an <c>enabled</c> line.
    /// </summary>
    internal static IReadOnlyDictionary<string, bool> ReadCodexConfig(string path)
    {
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return found;

        string? current = null;

        foreach (var raw in SafeReadLines(path))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = CodexPluginSection(line.Trim('[', ']').Trim());

                // A section names the plugin; until codex says otherwise, being listed means on.
                if (current is not null && !found.ContainsKey(current))
                    found[current] = true;

                continue;
            }

            if (current is null || !line.StartsWith("enabled", StringComparison.Ordinal))
                continue;

            var parts = line.Split('=', 2);
            if (parts.Length == 2 && bool.TryParse(parts[1].Split('#')[0].Trim(), out var enabled))
                found[current] = enabled;
        }

        return found;
    }

    /// <summary>
    /// The plugin a section header is about, or null when it is about something else. A nested
    /// header such as <c>plugins."x".mcp_servers.docs</c> still names the plugin, so what is read
    /// is the first segment after <c>plugins</c>.
    /// </summary>
    private static string? CodexPluginSection(string header)
    {
        if (!header.StartsWith("plugins.", StringComparison.Ordinal))
            return null;

        var rest = header["plugins.".Length..].Trim();
        if (rest.Length == 0)
            return null;

        if (rest[0] != '"')
            return rest.Split('.')[0] is { Length: > 0 } bare ? bare : null;

        var end = rest.IndexOf('"', 1);

        return end > 1 ? rest[1..end] : null;
    }

    private static IEnumerable<string> SubDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A CLI's own config file, read for what it says rather than validated. Anything unreadable or
    /// half-written is treated as nothing installed: a picker that is empty for a moment is a much
    /// smaller problem than a page that will not open.
    /// </summary>
    private JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read plugin configuration at {Path}", path);
            return null;
        }
    }

    private static string Label(string home) =>
        new DirectoryInfo(home).Name is { Length: > 0 } name ? name : home;
}
