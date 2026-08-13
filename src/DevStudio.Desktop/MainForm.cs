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
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 4000 };

    private bool _quitting;

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
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, DesktopPaths.WebViewProfile);
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
        _updates.Dispose();
        base.OnFormClosing(e);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open devStudio", null, (_, _) => ShowWindow());
        menu.Items.Add("Open in browser", null, (_, _) => OpenInBrowser(_server.Url));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check tools…", null, (_, _) => ShowPreflight());
        menu.Items.Add("Open data folder", null, (_, _) => OpenInBrowser(DesktopPaths.DataRoot));
        menu.Items.Add("Show server log", null, (_, _) => ShowLog());
        menu.Items.Add("Show update log", null, (_, _) => OpenInBrowser(UpdateLog.Path));
        _updateItem = menu.Items.Add("Check for updates…", null, async (_, _) => await CheckForUpdatesAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        return menu;
    }

    private void Quit()
    {
        _quitting = true;

        // Anything already downloaded is installed after this process exits, so the next launch is
        // the new version and no running agent was interrupted to get there.
        _updates.ApplyWhenClosed();

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
            var answer = MessageBox.Show(
                this,
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
            MessageBox.Show(
                this,
                "This copy was not installed by the devStudio installer, so it cannot update itself.",
                "devStudio — updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        var found = await _updates.CheckAsync();

        if (found is null)
        {
            MessageBox.Show(
                this,
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

        MessageBox.Show(
            this,
            ToolPreflight.Describe(results),
            "devStudio — tools on this machine",
            MessageBoxButtons.OK,
            ToolPreflight.HasBlockingGap(results) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ShowLog() => MessageBox.Show(this, _server.Tail(), "devStudio — server log");

    private static void OpenInBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {target}.{Environment.NewLine}{ex.Message}");
        }
    }

    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}
