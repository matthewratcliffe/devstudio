using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Domain.Plugins;
using DevStudio.Domain.Providers;

namespace DevStudio.Infrastructure.Plugins;

/// <summary>One plugin decision for a session: the CLI's own name for it, and on or off.</summary>
public sealed record WorkspacePlugin(PluginKey Key, bool Enabled)
{
    public string Name => Key.Name;
}

/// <summary>
/// The plugin selection a session runs with, staged into its workspace by the provisioner and read
/// back by whichever CLI adapter is about to start.
///
/// It is written provider-neutrally rather than in any one CLI's format for the same reason the
/// system prompt is: the decision belongs to the agent, and each CLI turns it into its own config
/// at the last moment — claude into a settings file, codex into config overrides, opencode into the
/// project config its server reads.
/// </summary>
public static class WorkspacePlugins
{
    public const string FileName = ".devstudio-plugins.json";

    public static string PathFor(string workspacePath) => Path.Combine(workspacePath, FileName);

    /// <summary>
    /// Writes the selection, or removes the file when there is nothing to say — an agent with no
    /// plugin decisions must not leave last session's file behind for the CLI to obey.
    /// </summary>
    public static void Write(string workspacePath, IReadOnlyList<WorkspacePlugin> plugins)
    {
        var path = PathFor(workspacePath);

        if (plugins.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);

            return;
        }

        var entries = new JsonArray();

        foreach (var plugin in plugins)
        {
            entries.Add(new JsonObject
            {
                ["provider"] = plugin.Key.Provider.ToString(),
                ["name"] = plugin.Name,
                ["enabled"] = plugin.Enabled,
            });
        }

        var document = new JsonObject { ["plugins"] = entries };

        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// What one CLI should turn on and off for this session. An unreadable file yields nothing,
    /// which leaves the CLI with whatever its own configuration already says — the same place it
    /// would be if this app had never written one.
    /// </summary>
    public static IReadOnlyList<WorkspacePlugin> Read(string workspacePath, AiProvider provider)
    {
        var path = PathFor(workspacePath);

        if (!File.Exists(path))
            return [];

        JsonArray? entries;
        try
        {
            entries = JsonNode.Parse(File.ReadAllText(path))?["plugins"] as JsonArray;
        }
        catch (Exception)
        {
            return [];
        }

        if (entries is null)
            return [];

        var plugins = new List<WorkspacePlugin>();

        foreach (var entry in entries)
        {
            if (entry is not JsonObject item)
                continue;

            var name = item["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!Enum.TryParse<AiProvider>(item["provider"]?.GetValue<string>(), ignoreCase: true, out var owner)
                || owner != provider)
            {
                continue;
            }

            plugins.Add(new WorkspacePlugin(
                new PluginKey(provider, name!),
                item["enabled"]?.GetValue<bool>() ?? true));
        }

        return plugins;
    }
}
