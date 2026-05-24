using Microsoft.Extensions.Logging;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Debouncing fan-in for cross-producer player-change signals. Each
/// <see cref="Signal"/> call resets a per-contentId timer; when that timer
/// fires without any further signal, <see cref="Aggregated"/> emits once for
/// that character. Lets the general notification producer publish a single
/// "X changed" line per logical change burst even when both the history
/// producer and the collection-growth producer detect overlapping events.
/// <para>The debounce window is sized to comfortably cover the refresh
/// worker's inter-task gap (<c>WorkerGap = 2s</c> in
/// <c>PlayerRefreshQueueService</c>), so consecutive Completed events for
/// the same player (Mounts → Minions → Achievements) coalesce into one
/// aggregated emission instead of three.</para>
/// </summary>
internal sealed class PlayerChangeAggregator : IPlayerChangeSignal, IDisposable
{
    /// <summary>Trailing-debounce window. Trades freshness for fewer
    /// duplicates: signals within this window of each other coalesce. 3s
    /// gives room for the queue worker's 2s WorkerGap so the natural
    /// Mounts/Minions/Achievements burst fires once.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);

    /// <summary>Fires once per coalesced burst — payload is the contentId
    /// of the character that changed. Handlers run on the threadpool task
    /// that owns the debounce timer; subscribers must marshal to the UI
    /// thread themselves if needed (chat publish is thread-safe).</summary>
    public event Action<ulong>? Aggregated;

    private readonly object mLock = new();
    private readonly Dictionary<ulong, CancellationTokenSource> mPending = new();
    private readonly ILogger<PlayerChangeAggregator> mLog;
    private bool mDisposed;

    public PlayerChangeAggregator(ILogger<PlayerChangeAggregator> log)
    {
        mLog = log;
    }

    public void Signal(ulong contentId)
    {
        if (mDisposed) return;
        if (contentId == 0) return;

        CancellationTokenSource newCts;
        CancellationTokenSource? oldCts;
        lock (mLock)
        {
            mPending.TryGetValue(contentId, out oldCts);
            newCts = new CancellationTokenSource();
            mPending[contentId] = newCts;
        }

        // Cancel the previous pending timer outside the lock — cancellation
        // can trigger continuations and we don't want them running while we
        // hold mLock.
        if (oldCts is not null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }

        _ = DebounceAsync(contentId, newCts);
    }

    private async Task DebounceAsync(ulong contentId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceWindow, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer signal — let it handle the emission
        }

        // Only emit if WE are still the active timer for this contentId.
        // A newer Signal would have replaced the entry in mPending; we'd
        // have been cancelled, but defensive double-check covers the race
        // where cancel arrived after Task.Delay completed.
        bool isCurrent;
        lock (mLock)
        {
            isCurrent = mPending.TryGetValue(contentId, out var current) && current == cts;
            if (isCurrent) mPending.Remove(contentId);
        }

        if (!isCurrent) return;

        try { Aggregated?.Invoke(contentId); }
        catch (Exception ex)
        {
            // Subscriber threw; don't let it take down the aggregator —
            // future signals must keep working.
            mLog.LogWarning(ex, "PlayerChangeAggregator: subscriber threw on Aggregated for cid={Cid}", contentId);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        CancellationTokenSource[] toCancel;
        lock (mLock)
        {
            toCancel = mPending.Values.ToArray();
            mPending.Clear();
        }
        foreach (var c in toCancel)
        {
            try { c.Cancel(); } catch (ObjectDisposedException) { }
            try { c.Dispose(); } catch { }
        }
    }
}
