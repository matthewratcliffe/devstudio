using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Domain.Providers;

namespace DevStudio.Infrastructure.Plugins;

/// <summary>
/// Turns a session's plugin selection into opencode's project config.
///
/// opencode has no per-run flag for this and no install registry: a plugin is an npm package or a
/// local file named in the <c>plugin</c> array, and the config for the directory a session runs in
/// is the last word on what that array holds. So the array is set to exactly what the agent asked
/// for — a plugin the global config named and the agent did not is left out of the session.
///
/// Only that one key is written, so a repository keeping its own opencode settings in the workspace
/// keeps them. Plugins dropped straight into an opencode plugin directory load whatever the config
/// says, which is why this app does not offer them as something to switch.
/// </summary>
public static class OpencodePluginConfig
{
    public const string FileName = "opencode.json";

    /// <summary>
    /// Writes the workspace's config and returns its path, or null when the session has nothing to
    /// say about plugins — in which case whatever is there is left exactly as it was.
    /// </summary>
    public static string? Write(string workspacePath)
    {
        var plugins = WorkspacePlugins.Read(workspacePath, AiProvider.Opencode);
        if (plugins.Count == 0)
            return null;

        var path = Path.Combine(workspacePath, FileName);

        var config = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []
            : [];

        var enabled = new JsonArray();
        foreach (var plugin in plugins.Where(p => p.Enabled))
            enabled.Add(plugin.Name);

        config["plugin"] = enabled;

        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return path;
    }
}
