using Velopack;
using Velopack.Sources;

namespace DevStudio.Desktop;

/// <summary>
/// Keeps the installed app current without ever interrupting it.
///
/// New versions are found and downloaded in the background, but never applied while the app is
/// running: applying restarts it, and a restart takes every agent mid-turn with it. The download
/// sits there until the user quits, and is installed on the way out — so the next launch is already
/// the new version and nobody lost a session to it.
/// </summary>
public sealed class UpdateWatcher : IDisposable
{
    private const string Repository = "https://github.com/matthewratcliffe/devstudio";

    // The download is the slow part and it dies with the process, so the check goes early: a session
    // short enough to miss this is a session too short to have finished the download anyway.
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BetweenChecks = TimeSpan.FromHours(6);

    // A check that failed says nothing about whether a version is waiting — six hours is the pause
    // between answers, not between a dropped connection and trying again.
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(15);

    private readonly CancellationTokenSource _stopping = new();
    private readonly UpdateManager? _manager;

    private UpdateInfo? _downloaded;
    private bool _lastCheckFailed;

    public UpdateWatcher()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(Repository, null, prerelease: false));
        }
        catch (Exception ex)
        {
            // No feed, no updates. Everything below turns into a no-op.
            UpdateLog.Instance.Write($"No update manager: {ex.Message}");
            _manager = null;
        }
    }

    /// <summary>Raised once a version has been downloaded and is waiting for the app to close.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>Version sitting on disk ready to be applied, if any.</summary>
    public string? ReadyVersion => _downloaded?.TargetFullRelease.Version.ToString();

    /// <summary>False for a build run from the repository, which has no install to replace.</summary>
    public bool CanUpdate => _manager is { IsInstalled: true };

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? "development build";

    /// <summary>Starts the background loop. Failures are silent — an update check is not worth a dialog.</summary>
    public void Start()
    {
        if (!CanUpdate)
        {
            UpdateLog.Instance.Write("Not an installed build; updates are off for this session.");
            return;
        }

        UpdateLog.Instance.Write($"Watching for updates. Running {CurrentVersion}.");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstCheck, _stopping.Token);

                while (!_stopping.IsCancellationRequested)
                {
                    await CheckAsync(_stopping.Token);
                    await Task.Delay(_lastCheckFailed ? AfterFailure : BetweenChecks, _stopping.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        });
    }

    /// <summary>
    /// One check, also used by the menu item. Returns the version now waiting to be applied, or null
    /// when there is nothing new.
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is not { IsInstalled: true })
            return null;

        try
        {
            var update = await _manager.CheckForUpdatesAsync();

            _lastCheckFailed = false;

            if (update is null)
            {
                UpdateLog.Instance.Write($"Checked: {CurrentVersion} is the newest release.");
                return null;
            }

            // Already downloaded; nothing to announce twice.
            if (_downloaded?.TargetFullRelease.Version == update.TargetFullRelease.Version)
                return ReadyVersion;

            var target = update.TargetFullRelease.Version.ToString();
            UpdateLog.Instance.Write($"Downloading {target}.");

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct);

            _downloaded = update;
            UpdateLog.Instance.Write($"{target} is downloaded and installs when devStudio next closes.");
            UpdateReady?.Invoke(ReadyVersion!);
            return ReadyVersion;
        }
        catch (OperationCanceledException)
        {
            // Quitting mid-download. The next launch starts it again.
            UpdateLog.Instance.Write("Download interrupted by shutdown; it starts again on the next launch.");
            return null;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or the release is half-uploaded. Try again on the next tick.
            UpdateLog.Instance.Write($"Update check did not complete: {ex.Message}");
            _lastCheckFailed = true;
            return null;
        }
    }

    /// <summary>
    /// Hands the downloaded version to the updater, which waits for this process to exit and then
    /// installs it. Called on the way out, so the update lands between sessions rather than during one.
    /// </summary>
    public void ApplyWhenClosed()
    {
        if (_manager is null || _downloaded is null)
            return;

        try
        {
            UpdateLog.Instance.Write($"Applying {ReadyVersion} after this process exits.");
            _manager.WaitExitThenApplyUpdates(_downloaded, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            // A failed update must not stop the app from closing; the next check finds it again.
            UpdateLog.Instance.Write($"Could not hand over to the updater: {ex.Message}");
        }
    }

    /// <summary>Applies now and restarts. Only ever from an explicit "restart now".</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _downloaded is null)
            return;

        UpdateLog.Instance.Write($"Applying {ReadyVersion} and restarting.");
        _manager.ApplyUpdatesAndRestart(_downloaded);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
