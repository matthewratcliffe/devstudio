namespace DevStudio.Desktop;

/// <summary>
/// Where the desktop build keeps its state, and the environment the server child is started with.
///
/// Nothing lives beside the executable: an update installs a new copy of the application and removes
/// the old one, so anything kept there would be thrown away with it.
/// </summary>
public static class DesktopPaths
{
    /// <summary>
    /// State root, per platform. On Windows this is deliberately a sibling of the install directory
    /// (<c>%LOCALAPPDATA%\devStudio</c>) rather than a child of it; elsewhere the application is
    /// installed outside the data directory anyway.
    /// </summary>
    public static string DataRoot { get; } = Environment.GetEnvironmentVariable("DEVSTUDIO_DATA")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            OperatingSystem.IsWindows() ? "devStudio-data" : "devStudio");

    /// <summary>Holds one profile directory per version. See <see cref="WebViewProfiles"/>.</summary>
    public static string WebViewProfileRoot => Path.Combine(DataRoot, "webview");

    /// <summary>
    /// The profile this build uses. Versioned, because the cache in it is keyed on an origin that
    /// never changes and would otherwise go on serving the previous build's pages to this one.
    /// </summary>
    public static string WebViewProfile => WebViewProfiles.DirectoryFor(WebViewProfileRoot, DesktopVersion.Current);

    public static string SettingsFile => Path.Combine(DataRoot, "desktop.json");

    public static string LogFile => Path.Combine(DataRoot, "server.log");

    /// <summary>Repository roots offered by the folder picker. The whole home directory, by default.</summary>
    public static string DefaultRepositoryRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Configuration for the server child. Every value mirrors a key the container sets through
    /// compose; the difference is that a desktop install has no volumes and no container boundary.
    /// </summary>
    public static Dictionary<string, string> ServerEnvironment(int port, bool listenOnLocalNetwork = false)
    {
        var data = Path.Combine(DataRoot, "data");

        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            // Loopback unless the user has asked for the local network, in which case every
            // interface — that is the whole of the setting. See <see cref="NetworkSettings"/>.
            ["ASPNETCORE_URLS"] = $"http://{(listenOnLocalNetwork ? "0.0.0.0" : "127.0.0.1")}:{port}",

            // The built-in MCP server advertises its own URL to agents, so it has to know the port
            // that was actually free, not the default one.
            ["Orchestrator__HttpPort"] = port.ToString(),

            ["Orchestrator__DataPath"] = data,
            ["Orchestrator__RepositoriesPath"] = Path.Combine(data, "repos"),
            ["Orchestrator__WorktreesPath"] = Path.Combine(data, "worktrees"),
            ["Orchestrator__ScratchPath"] = Path.Combine(data, "scratch"),

            // Empty means the real home: the desktop build shares the CLI logins already sitting in
            // ~/.claude and ~/.codex instead of keeping a second set.
            ["Orchestrator__HomePath"] = string.Empty,

            // There is no container here, so the CLIs keep their own sandboxes: codex runs
            // workspace-write instead of danger-full-access, and claude leaves its bash sandbox on.
            ["Orchestrator__ContainerIsTheSandbox"] = "false",

            // The shell has Velopack and installs updates itself, so the web UI does not also need
            // to nag about them.
            ["Orchestrator__UpdateCheckEnabled"] = "false",

            // No bind mounts to declare — the picker opens on the user's home directory and can
            // reach anything under it.
            ["Orchestrator__LocalRepositoryRoots__0"] = DefaultRepositoryRoot,

            // Checkouts are not all under the home directory: on Windows they are as likely to be on
            // a second drive. The desktop build is already running as the user, so nothing is gained
            // by hiding the rest of their own filesystem from a folder picker they drove there.
            ["Orchestrator__AllowAllLocalDrives"] = "true",
        };
    }
}
