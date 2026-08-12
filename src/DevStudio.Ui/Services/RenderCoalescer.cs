namespace DevStudio.Ui.Services;

/// <summary>
/// Collapses a burst of updates into one redraw per interval. A running turn now streams its
/// answer a few characters at a time, which is more updates than a page can usefully draw — and
/// far more than a page that reloads from the store on each one can survive.
/// </summary>
public sealed class RenderCoalescer(int intervalMs = 100)
{
    private int _queued;

    /// <summary>
    /// Runs <paramref name="flush"/> after the interval, unless one is already pending — in which
    /// case this update rides along with it. Callers hold their state in fields the flush reads,
    /// so the pending flush shows whatever arrived while it waited.
    /// </summary>
    public void Queue(Func<Task> flush)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
            return;

        _ = FlushAsync(flush);
    }

    private async Task FlushAsync(Func<Task> flush)
    {
        try
        {
            await Task.Delay(intervalMs);
            Interlocked.Exchange(ref _queued, 0);
            await flush();
        }
        catch (ObjectDisposedException)
        {
            // The user navigated away mid-turn; there is nothing left to draw.
        }
        catch (OperationCanceledException)
        {
        }
    }
}
