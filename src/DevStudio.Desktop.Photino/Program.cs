using System.Net;
using DevStudio.Desktop;
using Photino.NET;
using Velopack;

namespace DevStudio.Desktop.Photino;

/// <summary>
/// The macOS and Linux shell. Same shape as the Windows one — start the web app on loopback, show it
/// in the system web view — but built on Photino, which wraps WKWebView on macOS and WebKitGTK on
/// Linux. Neither platform gets a tray icon, so closing the window stops the server with it.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Velopack's hooks run during install, update and uninstall, and expect to go first.
        VelopackApp.Build().SetLogger(UpdateLog.Instance).Run();

        if (args.Contains("--check-tools"))
        {
            Console.WriteLine(ToolPreflight.Describe(ToolPreflight.Check()));
            return 0;
        }

        if (args.Contains("--update"))
            return UpdateNow();

        // There is no tray menu on these platforms, so the setting is a flag. Setting it is its own
        // run: it is applied when the server child starts, and this one has not started it yet.
        if (args.Contains("--listen-local-network") || args.Contains("--loopback-only"))
            return SetNetworkAccess(args.Contains("--listen-local-network"));

        // One server per machine. The lock file also carries the port, so a second launch can point
        // a browser at the copy that is already running instead of starting a rival on another port.
        using var only = SingleInstance.Acquire();

        if (!only.Acquired)
        {
            Console.WriteLine(only.RunningUrl is { } running
                ? $"devStudio is already running at {running}"
                : "devStudio is already running.");

            return 0;
        }

        using var server = new ServerProcess();

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            return ShowFailure("devStudio could not start", ex.Message);
        }

        only.Publish(server.Url);

        foreach (var url in server.NetworkUrls)
            Console.WriteLine($"Also reachable at {url}");

        var ready = server.WaitUntilReadyAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();

        if (!ready)
        {
            return ShowFailure(
                "The devStudio server did not start",
                WebUtility.HtmlEncode(server.Tail()),
                MissingToolsHtml());
        }

        // Updates download in the background and are installed after this process exits, so a new
        // version never lands on top of a running agent. There is no tray icon here to announce it,
        // so the window title carries the news.
        using var updates = new UpdateWatcher();
        var window = NewWindow().Load(new Uri(server.Url));

        updates.UpdateReady += version =>
            window.SetTitle($"devStudio — {version} installs when you quit");

        updates.Start();

        window.WaitForClose();
        updates.ApplyWhenClosed();

        return 0;
    }

    /// <summary>
    /// Loopback only, or every interface. Worth being blunt about what the second one means: this
    /// runs as the user, with their files and their CLI logins, and the seeded account is
    /// admin/admin until somebody changes it.
    /// </summary>
    private static int SetNetworkAccess(bool listenOnLocalNetwork)
    {
        if (NetworkSettings.IsForcedByEnvironment)
        {
            Console.WriteLine(
                $"{NetworkSettings.OverrideVariable} is set in the environment and overrides the " +
                "saved setting. Unset it to control this from here.");

            return 1;
        }

        try
        {
            (NetworkSettings.Load() with { ListenOnLocalNetwork = listenOnLocalNetwork }).Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not save the setting: {ex.Message}");
            return 1;
        }

        if (!listenOnLocalNetwork)
        {
            Console.WriteLine("devStudio will listen on 127.0.0.1 alone the next time it starts.");
            return 0;
        }

        Console.WriteLine(
            "devStudio will listen on every interface the next time it starts, so anything on this " +
            "network can reach it. Change the admin password if you have not already.");

        foreach (var address in NetworkSettings.LocalAddresses())
            Console.WriteLine($"  http://{address}:7080/ (or the port it takes, if 7080 is busy)");

        return 0;
    }

    private static PhotinoWindow NewWindow()
    {
        var window = new PhotinoWindow()
            .SetTitle("devStudio")
            .SetUseOsDefaultSize(false)
            .SetWidth(1280)
            .SetHeight(860)
            .SetResizable(true)
            .Center()
            // A desktop app with a right-click "Reload frame" menu reads as a browser, not an app.
            .SetContextMenuEnabled(false);

        var icon = Path.Combine(AppContext.BaseDirectory, "app.png");
        return File.Exists(icon) ? window.SetIconFile(icon) : window;
    }

    /// <summary>
    /// Failures get a window too. A shell that exits silently because a tool is missing looks like a
    /// crash, and there is no tray icon here to go and ask.
    /// </summary>
    private static int ShowFailure(string heading, params string?[] details)
    {
        var body = string.Join("", details.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => $"<pre>{d}</pre>"));

        NewWindow()
            .SetTitle($"devStudio — {heading}")
            .LoadRawString(
                $$"""
                  <html>
                    <head><meta charset="utf-8"></head>
                    <body style="background:#0b0d14;color:#c8d2f5;font:14px ui-monospace,Consolas,monospace;padding:2rem">
                      <h2 style="color:#7ee7f5">{{WebUtility.HtmlEncode(heading)}}</h2>
                      {{body}}
                    </body>
                  </html>
                  """)
            .WaitForClose();

        return 1;
    }

    /// <summary>Nothing is bundled on a desktop install, so a missing prerequisite is the first suspect.</summary>
    private static string? MissingToolsHtml()
    {
        var results = ToolPreflight.Check();

        return ToolPreflight.HasBlockingGap(results)
            ? WebUtility.HtmlEncode(ToolPreflight.Describe(results))
            : null;
    }

    /// <summary>
    /// Applies an update immediately, for someone who would rather not wait for the next quit.
    /// Nothing should be running when it is used — it restarts the app.
    /// </summary>
    private static int UpdateNow()
    {
        using var updates = new UpdateWatcher();

        if (!updates.CanUpdate)
        {
            Console.WriteLine("This copy was not installed by the devStudio installer, so it cannot update itself.");
            return 1;
        }

        var version = updates.CheckAsync().GetAwaiter().GetResult();

        if (version is null)
        {
            Console.WriteLine($"devStudio {updates.CurrentVersion} is up to date.");
            return 0;
        }

        Console.WriteLine($"Installing devStudio {version}…");
        updates.ApplyAndRestart();
        return 0;
    }

}
