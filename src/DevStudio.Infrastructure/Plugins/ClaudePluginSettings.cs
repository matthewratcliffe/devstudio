using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Domain.Providers;

namespace DevStudio.Infrastructure.Plugins;

/// <summary>
/// Turns a session's plugin selection into the one thing claude reads for it: a settings file naming
/// each plugin and whether it is on, handed to the CLI with <c>--settings</c>.
///
/// A file of its own rather than an edit to the workspace's <c>.claude/settings.json</c>, because
/// that file belongs to the repository and to whoever else works in the checkout — a session must
/// not leave its own choices behind in it. <c>--settings</c> applies for this run only and takes
/// precedence over the user and project files, which is exactly the scope wanted.
/// </summary>
public static class ClaudePluginSettings
{
    public const string FileName = ".claude-plugins.settings.json";

    /// <summary>
    /// Writes the file and returns its path, or null when the session has nothing to say about
    /// plugins — in which case any file from a previous run is removed rather than reused.
    /// </summary>
    public static string? Write(string workspacePath)
    {
        var path = Path.Combine(workspacePath, FileName);
        var plugins = WorkspacePlugins.Read(workspacePath, AiProvider.Claude);

        if (plugins.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);

            return null;
        }

        var enabled = new JsonObject();
        foreach (var plugin in plugins)
            enabled[plugin.Name] = plugin.Enabled;

        var settings = new JsonObject { ["enabledPlugins"] = enabled };

        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return path;
    }
}
