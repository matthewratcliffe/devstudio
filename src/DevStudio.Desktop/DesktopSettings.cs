using System.Text.Json;

namespace DevStudio.Desktop;

/// <summary>Window position, so the app opens where it was left rather than in the corner.</summary>
internal sealed record DesktopSettings(int X, int Y, int Width, int Height, bool Maximised)
{
    public static DesktopSettings From(Form form)
    {
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;

        return new DesktopSettings(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            form.WindowState == FormWindowState.Maximized);
    }

    public static DesktopSettings? Load()
    {
        try
        {
            return File.Exists(DesktopPaths.SettingsFile)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(DesktopPaths.SettingsFile))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Saved bounds, but only where they still land on a screen. A window restored onto a monitor
    /// that has since been unplugged is invisible, and reads as the app failing to launch.
    /// </summary>
    public static Rectangle BoundsFor(DesktopSettings? settings)
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1400, 920);
        var fallback = new Rectangle(
            work.X + 80,
            work.Y + 60,
            Math.Min(1280, work.Width - 160),
            Math.Min(860, work.Height - 120));

        if (settings is null)
            return fallback;

        var bounds = new Rectangle(settings.X, settings.Y, settings.Width, settings.Height);

        if (bounds.Width < 600 || bounds.Height < 400)
            return fallback;

        return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)) ? bounds : fallback;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DesktopPaths.DataRoot);
            File.WriteAllText(DesktopPaths.SettingsFile, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Losing the window position is not worth interrupting a shutdown for.
        }
    }
}
