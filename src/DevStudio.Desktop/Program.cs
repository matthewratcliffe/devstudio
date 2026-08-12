using Microsoft.Web.WebView2.Core;
using Velopack;

namespace DevStudio.Desktop;

internal static class Program
{
    private const string InstanceMutex = @"Local\devStudio.Desktop.Instance";
    private const string ActivationEvent = @"Local\devStudio.Desktop.Activate";

    [STAThread]
    private static void Main()
    {
        // Velopack's hooks run during install, update and uninstall, and it expects them before
        // anything else happens — a window drawn first is a window the installer has to fight.
        VelopackApp.Build().Run();

        using var instance = new Mutex(true, InstanceMutex, out var isOnlyInstance);

        if (!isOnlyInstance)
        {
            // A second launch — from the Start menu, or a double-clicked shortcut — brings the
            // running copy forward instead of starting a second server on a second port.
            using var activation = EventWaitHandle.OpenExisting(ActivationEvent);
            activation.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        if (MissingWebView2Runtime() is { } missing)
        {
            MessageBox.Show(
                missing,
                "devStudio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        using var server = new ServerProcess();

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "devStudio could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var form = new MainForm(server);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEvent);

        ListenForActivation(activate, form);
        Application.Run(form);
    }

    /// <summary>
    /// Waits for a second launch to signal, then shows the window. A background thread rather than a
    /// timer, so the wait costs nothing while nobody is launching anything.
    /// </summary>
    private static void ListenForActivation(EventWaitHandle activation, MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (activation.WaitOne())
            {
                if (form.IsDisposed)
                    return;

                try
                {
                    form.BeginInvoke(form.ShowWindow);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "devStudio activation",
        };

        thread.Start();
    }

    /// <summary>
    /// WebView2 ships with Windows 11 and with most Windows 10 installs, but not all of them, and the
    /// failure without it is an empty window rather than an error.
    /// </summary>
    private static string? MissingWebView2Runtime()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return null;
        }
        catch (Exception)
        {
            return
                "devStudio needs the Microsoft Edge WebView2 runtime, which is not installed on this " +
                "machine." + Environment.NewLine + Environment.NewLine +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start " +
                "devStudio again.";
        }
    }
}
