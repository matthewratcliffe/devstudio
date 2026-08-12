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
        VelopackApp.Build().Run();

        if (args.Contains("--check-tools"))
        {
            Console.WriteLine(ToolPreflight.Describe(ToolPreflight.Check()));
            return 0;
        }

        if (args.Contains("--update"))
            return UpdateNow();

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
