using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Persistence;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Set of content ids with at least one outstanding row in the refresh queue,
/// backing the player list's "In refresh queue" system filter.
/// <para>Deliberately unfiltered: every row counts, including ones cooling down
/// after a failure, ones that exhausted their attempts, and ones whose category
/// was disabled after they were queued. The filter answers "is there still open
/// refresh work on file for this player", not "will the worker pick this up on
/// its next tick" — the latter is what the detail header's queue badge reports
/// via <c>GetQueueStatusForAsync</c>.</para>
/// <para>The queue's composite <c>(content_id, category)</c> key means one player
/// can hold up to eight rows; <c>DISTINCT</c> plus the <see cref="HashSet{T}"/>
/// collapse those to a single membership entry.</para>
/// </summary>
public sealed class RefreshQueueMembershipCache
{
    /// <summary>How long a loaded snapshot is served before the next
    /// <see cref="EnsureFresh"/> is allowed to re-query. The queue turns over on
    /// the worker's 2s gap, so a matching TTL keeps the list roughly in step
    /// without polling per frame.
    /// <para>Polling rather than subscribing to the queue's Enqueued/Completed
    /// events is intentional: those fire per row (a single bulk refresh emits
    /// hundreds), and rows also disappear without any event at all when the
    /// maintenance contributors prune them.</para></summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    private readonly INexusDbContextFactory mFactory;
    private readonly ILogger<RefreshQueueMembershipCache> mLog;

    // Swapped wholesale by ReloadAsync, never mutated in place — the draw thread
    // always observes either the previous or the next snapshot, never a torn one.
    private volatile IReadOnlySet<ulong> mIds = new HashSet<ulong>();
    private DateTime mNextAllowedAt = DateTime.MinValue;
    // Guards against fanning out a second query while the first is still running.
    private Task? mLoad;

    public RefreshQueueMembershipCache(
        INexusDbContextFactory factory,
        ILogger<RefreshQueueMembershipCache> log)
    {
        mFactory = factory;
        mLog = log;
    }

    /// <summary>The most recently loaded snapshot. Empty until the first load
    /// lands (and after a failed one) — the list shows nothing for a frame or
    /// two after the filter is picked, same transient the DB-backed sort path
    /// already has.</summary>
    public IReadOnlySet<ulong> ContentIds => mIds;

    /// <summary>Kick a reload when the snapshot has aged past <see cref="Ttl"/>.
    /// Cheap no-op otherwise, so the list panel can call it every frame while
    /// the filter is selected.</summary>
    public void EnsureFresh()
    {
        if (mLoad is not null) return;
        if (DateTime.UtcNow < mNextAllowedAt) return;
        mLoad = Task.Run(ReloadAsync);
    }

    private async Task ReloadAsync()
    {
        try
        {
            await using var ctx = await mFactory.CreateDbContextAsync().ConfigureAwait(false);
            var ids = await ctx.Set<InternalRefreshQueueEntity>()
                .Select(e => e.ContentId)
                .Distinct()
                .ToListAsync().ConfigureAwait(false);
            mIds = new HashSet<ulong>(ids);
        }
        catch (Exception ex)
        {
            // Same contract as the filter DB query service: a broken read leaves
            // the last good snapshot in place and logs, it never takes the list
            // (or the frame) down with it.
            mLog.LogWarning(ex, "Refresh-queue membership load failed.");
        }
        finally
        {
            // Settle from completion, not from kick-off, so a slow query can't
            // queue up back-to-back reloads.
            mNextAllowedAt = DateTime.UtcNow + Ttl;
            mLoad = null;
        }
    }
}
