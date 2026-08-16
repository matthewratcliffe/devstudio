using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DevStudio.Desktop;

/// <summary>
/// The window. It owns nothing but a web view pointed at the local server, a tray icon so closing
/// the window does not stop the agents, and the handful of menu items that only make sense on a
/// desktop install.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ServerProcess _server;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "Starting devStudio…",
        ForeColor = Color.FromArgb(200, 210, 235),
        BackColor = Color.FromArgb(11, 13, 20),
        Font = new Font("Consolas", 10f),
    };

    private readonly NotifyIcon _tray;
    private readonly UpdateWatcher _updates = new();
    private ToolStripItem? _updateItem;
    private ToolStripMenuItem? _networkItem;
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 4000 };

    private bool _quitting;
    private bool _reloading;

    /// <summary>What the page posts when the user presses Ctrl+Shift+R inside it.</summary>
    private const string HardReloadMessage = "devstudio:hard-reload";

    public MainForm(ServerProcess server)
    {
        _server = server;

        Text = "devStudio";
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(11, 13, 20);
        StartPosition = FormStartPosition.Manual;
        Icon = LoadIcon();
        var settings = DesktopSettings.Load();
        Bounds = DesktopSettings.BoundsFor(settings);

        if (settings is { Maximised: true })
            WindowState = FormWindowState.Maximized;

        Controls.Add(_view);
        Controls.Add(_status);

        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "devStudio",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };

        _tray.DoubleClick += (_, _) => ShowWindow();

        _watchdog.Tick += (_, _) => CheckServerAlive();
        _watchdog.Start();

        _updates.UpdateReady += version => BeginInvoke(() => AnnounceUpdate(version));
        _updates.Start();
    }

    /// <summary>Called when a second launch is refused, so the running copy comes to the front.</summary>
    public void ShowWindow()
    {
        Show();
        BringForward();
    }

    /// <summary>
    /// Raises the window, leaving a maximised one maximised.
    /// </summary>
    private void BringForward()
    {
        if (!Visible)
            Show();

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        Activate();
        BringToFront();
    }

    /// <summary>
    /// A message box the user can actually see, which is why nothing here calls MessageBox.Show
    /// directly. Tray menu items run while some other window owns the foreground, and Windows will
    /// not raise a background process above the active one: the box opens behind this window, and
    /// because it is modal to this window, every click on the app then goes nowhere. An app that
    /// stops responding after one menu item is this, every time.
    /// </summary>
    private DialogResult Say(
        string text,
        string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None)
    {
        BringForward();
        return MessageBox.Show(this, text, caption, buttons, icon);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            var profile = DesktopPaths.WebViewProfile;

            // The profile the previous version used is now unreachable — the directory is named
            // after the version — and it is a browser profile, so it is tens of megabytes of cache
            // that nothing would ever collect.
            foreach (var gone in WebViewProfiles.Prune(DesktopPaths.WebViewProfileRoot, DesktopVersion.Current))
                UpdateLog.Instance.Write($"Removed the web view profile left behind by {gone}.");

            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await _view.EnsureCoreWebView2Async(environment);

            // A desktop app with a right-click "Reload frame" menu reads as a browser, not an app.
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Links meant for the wider web — docs, a pull request an agent opened — belong in the
            // real browser, where the user is already signed in to everything.
            _view.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenInBrowser(args.Uri);
            };

            // Ctrl+Shift+R inside the page: the key never reaches this form while the web view has
            // focus, so the page asks for the reload instead. Only the shell can clear the HTTP
            // cache the request would otherwise be answered from.
            _view.CoreWebView2.WebMessageReceived += async (_, args) =>
            {
                if (Message(args) == HardReloadMessage)
                    await HardReloadAsync();
            };

            // A versioned profile is already empty on its first launch, so this catches what
            // versioning cannot: a development build, which keeps its version and its directory
            // across every rebuild.
            if (WebViewProfiles.CacheIsStale(profile, DesktopVersion.BuildStamp))
            {
                UpdateLog.Instance.Write($"First run of {DesktopVersion.BuildStamp} in this profile; clearing the cache.");
                await ClearCacheAsync();
                WebViewProfiles.RecordCache(profile, DesktopVersion.BuildStamp);
            }
        }
        catch (Exception ex)
        {
            Fail($"The WebView2 runtime could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return;
        }

        if (!await _server.WaitUntilReadyAsync(TimeSpan.FromSeconds(90)))
        {
            Fail(
                "The devStudio server did not start." + Environment.NewLine + Environment.NewLine +
                _server.Tail());

            return;
        }

        _view.CoreWebView2.Navigate(_server.Url);
        _status.Visible = false;
        _view.Visible = true;
    }

    /// <summary>
    /// The same shortcut a browser uses, for the same reason, in a window that has no other way to
    /// ask. This only fires while the shell has focus — the web view swallows the key when it has
    /// it, and posts <see cref="HardReloadMessage"/> instead.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.R))
        {
            _ = HardReloadAsync();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Throws the cache away and loads the page again. Nothing running is affected: the agents are
    /// in the server process, and this only touches the window looking at it.
    /// </summary>
    private async Task HardReloadAsync()
    {
        if (_reloading || _view.CoreWebView2 is null)
            return;

        _reloading = true;

        try
        {
            await ClearCacheAsync();
            _view.CoreWebView2.Reload();
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>
    /// Everything a stale page could be served from. Cookies and local storage are deliberately not
    /// in the list — clearing those signs the user out of the providers they connected.
    /// </summary>
    private async Task ClearCacheAsync()
    {
        try
        {
            await _view.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache
                | CoreWebView2BrowsingDataKinds.CacheStorage
                | CoreWebView2BrowsingDataKinds.ServiceWorkers);
        }
        catch (Exception ex)
        {
            UpdateLog.Instance.Write($"Could not clear the web view cache: {ex.Message}");
        }
    }

    /// <summary>A message that is not a string throws rather than returning null, and one may not be.</summary>
    private static string? Message(CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            return args.TryGetWebMessageAsString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Closing the window leaves the agents running; the tray icon is the way back.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        DesktopSettings.From(this).Save();

        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray.Visible = false;

        // Every real exit lands here — the tray's Quit, a Windows shutdown, a sign-out, the task
        // manager. The handover belongs on this path rather than on Quit alone: an update handed
        // over nowhere is an update that re-downloads itself every launch and never installs.
        //
        // A session can easily end before the background loop has looked even once, so closing gets
        // its own bounded check first. It is capped at a delta and half a minute; the window is
        // already going away, and the wait cursor is the only sign anything happened.
        UseWaitCursor = true;
        _updates.PrepareForExit();
        _updates.ApplyWhenClosed();
        _updates.Dispose();
        base.OnFormClosing(e);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open devStudio", null, (_, _) => ShowWindow());
        menu.Items.Add("Open in browser", null, (_, _) => OpenInBrowser(_server.Url));

        // The shortcut is the one people already know, but a window with no browser chrome gives no
        // sign it exists, and this is the item somebody goes looking for when the UI looks wrong.
        menu.Items.Add("Reload the window (Ctrl+Shift+R)", null, async (_, _) => await HardReloadAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check tools…", null, (_, _) => ShowPreflight());

        // The one piece of the server's configuration a desktop user has a reason to change, so it
        // is here rather than in a settings file they would have to be told about.
        _networkItem = new ToolStripMenuItem("Listen on local network", null, (_, _) => ToggleLocalNetwork())
        {
            Checked = _server.Network.ListenOnLocalNetwork,
            // The environment variable is the last word, and a tick that cannot be changed is worse
            // than no tick at all.
            Enabled = !NetworkSettings.IsForcedByEnvironment,
            ToolTipText = NetworkSettings.IsForcedByEnvironment
                ? $"Set by {NetworkSettings.OverrideVariable}."
                : "Reach devStudio from a phone or another machine on this network.",
        };

        menu.Items.Add(_networkItem);
        menu.Items.Add("Open data folder", null, (_, _) => OpenInBrowser(DesktopPaths.DataRoot));
        menu.Items.Add("Show server log", null, (_, _) => ShowLog());
        menu.Items.Add("Show update log", null, (_, _) => OpenInBrowser(UpdateLog.Path));
        _updateItem = menu.Items.Add("Check for updates…", null, async (_, _) => await CheckForUpdatesAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        return menu;
    }

    /// <summary>
    /// Turns network access on or off. It is applied when the server child starts, so the change
    /// lands on the next launch rather than now: restarting the server here would take every agent
    /// mid-turn with it, which is a steep price for a menu tick.
    /// </summary>
    private void ToggleLocalNetwork()
    {
        var current = NetworkSettings.Load();
        var wanted = !current.ListenOnLocalNetwork;

        if (wanted)
        {
            // Worth saying plainly. This is a desktop install: it runs as the user, reaches every
            // drive on the machine, and the seeded account is still admin/admin until somebody
            // changes it. On loopback none of that is reachable by anyone else.
            var answer = Say(
                "devStudio will listen on every network interface, so anything on this network can " +
                "reach it — and it runs as you, with your files and your CLI logins." +
                Environment.NewLine + Environment.NewLine +
                "Change the admin password first if you have not already, and expect the firewall to " +
                "ask whether to allow the connection." +
                Environment.NewLine + Environment.NewLine +
                "Turn it on? It takes effect the next time devStudio starts.",
                "devStudio — listen on local network",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return;
        }

        try
        {
            (current with { ListenOnLocalNetwork = wanted }).Save();
        }
        catch (Exception ex)
        {
            Say($"Could not save the setting.{Environment.NewLine}{ex.Message}", "devStudio");
            return;
        }

        if (_networkItem is not null)
            _networkItem.Checked = wanted;

        Say(
            wanted
                ? "devStudio will listen on this network when it next starts." +
                  Environment.NewLine + Environment.NewLine +
                  AddressesToExpect()
                : "devStudio will listen on 127.0.0.1 alone when it next starts, so only this " +
                  "machine can reach it.",
            "devStudio — listen on local network",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>
    /// The URLs another machine would use. The port is the one this run took, which is the one the
    /// next launch takes too unless something else has claimed it in between.
    /// </summary>
    private string AddressesToExpect()
    {
        var addresses = NetworkSettings.LocalAddresses();

        return addresses.Count == 0
            ? "This machine has no active network connection to be reached on yet."
            : "Other machines will use:" + Environment.NewLine +
              string.Join(Environment.NewLine, addresses.Select(a => $"  http://{a}:{_server.Port}/"));
    }

    private void Quit()
    {
        _quitting = true;

        // Anything already downloaded is installed after this process exits, so the next launch is
        // the new version and no running agent was interrupted to get there. OnFormClosing does the
        // handover, so it happens whether or not the exit came through here.
        Close();
        Application.Exit();
    }

    /// <summary>
    /// A downloaded update is news, not an interruption: it says so once, and the menu item keeps
    /// saying so. Restarting is offered, never taken.
    /// </summary>
    private void AnnounceUpdate(string version)
    {
        if (_updateItem is not null)
            _updateItem.Text = $"Update {version} ready — restart to finish";

        _tray.BalloonTipTitle = $"devStudio {version} is ready";
        _tray.BalloonTipText = "It will be installed when you quit. Nothing running is affected.";
        _tray.ShowBalloonTip(8000);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updates.ReadyVersion is { } ready)
        {
            var answer = Say(
                $"devStudio {ready} is downloaded and will be installed when you quit." +
                Environment.NewLine + Environment.NewLine +
                "Restart now instead? Any agent mid-turn is stopped.",
                "devStudio — update ready",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
                _updates.ApplyAndRestart();

            return;
        }

        if (!_updates.CanUpdate)
        {
            Say(
                "This copy was not installed by the devStudio installer, so it cannot update itself.",
                "devStudio — updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        var found = await _updates.CheckAsync();

        if (found is null)
        {
            Say(
                $"devStudio {_updates.CurrentVersion} is up to date.",
                "devStudio — updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    /// <summary>
    /// The server is a separate process, so it can die on its own — a port taken, a corrupt state
    /// file. Saying so beats a window that simply stops responding to clicks.
    /// </summary>
    private void CheckServerAlive()
    {
        if (!_server.HasExited)
            return;

        _watchdog.Stop();

        Fail(
            "The devStudio server stopped." + Environment.NewLine + Environment.NewLine +
            _server.Tail());
    }

    private void Fail(string message)
    {
        _view.Visible = false;
        _status.Visible = true;
        _status.Text = message;
        ShowWindow();
    }

    private void ShowPreflight()
    {
        var results = ToolPreflight.Check();

        Say(
            ToolPreflight.Describe(results),
            "devStudio — tools on this machine",
            MessageBoxButtons.OK,
            ToolPreflight.HasBlockingGap(results) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ShowLog() => Say(_server.Tail(), "devStudio — server log");

    private void OpenInBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Say($"Could not open {target}.{Environment.NewLine}{ex.Message}", "devStudio");
        }
    }

    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}
