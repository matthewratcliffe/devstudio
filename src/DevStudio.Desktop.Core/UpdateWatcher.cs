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

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BetweenChecks = TimeSpan.FromHours(6);

    private readonly CancellationTokenSource _stopping = new();
    private readonly UpdateManager? _manager;

    private UpdateInfo? _downloaded;

    public UpdateWatcher()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(Repository, null, prerelease: false));
        }
        catch (Exception)
        {
            // No feed, no updates. Everything below turns into a no-op.
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
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstCheck, _stopping.Token);

                while (!_stopping.IsCancellationRequested)
                {
                    await CheckAsync(_stopping.Token);
                    await Task.Delay(BetweenChecks, _stopping.Token);
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

            if (update is null)
                return null;

            // Already downloaded; nothing to announce twice.
            if (_downloaded?.TargetFullRelease.Version == update.TargetFullRelease.Version)
                return ReadyVersion;

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct);

            _downloaded = update;
            UpdateReady?.Invoke(ReadyVersion!);
            return ReadyVersion;
        }
        catch (Exception)
        {
            // Offline, rate-limited, or the release is half-uploaded. Try again on the next tick.
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
            _manager.WaitExitThenApplyUpdates(_downloaded, silent: true, restart: false);
        }
        catch (Exception)
        {
            // A failed update must not stop the app from closing; the next check finds it again.
        }
    }

    /// <summary>Applies now and restarts. Only ever from an explicit "restart now".</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _downloaded is null)
            return;

        _manager.ApplyUpdatesAndRestart(_downloaded);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
