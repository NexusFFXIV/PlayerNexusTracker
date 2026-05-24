using System.Globalization;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Persistence;
using NexusKit.Persistence.Settings;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Publishes a chat line when a tracked player's mount / minion / achievement
/// collection count grows. FFXIVCollect-backed counts only ever increase, so
/// a simple before/after compare against a persisted baseline gives us a
/// no-spam "new content unlocked" signal without diffing entire sets.
///
/// <para>Subscribes to <see cref="IPlayerRefreshQueueService.Completed"/>
/// filtered to <see cref="RefreshCategory.Mounts"/> /
/// <see cref="RefreshCategory.Minions"/> /
/// <see cref="RefreshCategory.Achievements"/>. On each event the producer
/// reads the current row in <c>nexus_external_player_collection_stats</c>,
/// compares it to the per-(contentId, kind) baseline cached in the settings
/// store, and publishes only when <c>current &gt; baseline</c>.</para>
///
/// <para>First observation of a (contentId, kind) silently establishes the
/// baseline — no chat noise on plugin startup or when seeing a player for
/// the first time. The baseline blob lives under
/// <c>notifications.collection_baseline</c> in <see cref="ISettingsStore"/>;
/// wipe it via DbInspect if it gets out of sync.</para>
/// </summary>
internal sealed class CollectionGrowthNotificationProducer : INotificationProducer, IDisposable
{
    public const string MountsIncreasedKindId       = "enrichment.mounts_increased";
    public const string MinionsIncreasedKindId      = "enrichment.minions_increased";
    public const string AchievementsIncreasedKindId = "enrichment.achievements_increased";

    private const string GroupKey = "ui.notifications.group.collections";
    internal const string BaselineStoreKey = "notifications.collection_baseline";

    private readonly IPlayerRefreshQueueService mQueue;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly INexusDbContextFactory mDb;
    private readonly ISettingsStore mStore;
    private readonly IPlayerChangeSignal mSignal;
    private readonly ILocalizer mLoc;
    private readonly ILogger<CollectionGrowthNotificationProducer> mLog;

    // Per-category publisher + format key. Built in the ctor; missing categories
    // make OnCompleted no-op for that event.
    private readonly Dictionary<RefreshCategory, IChatNotificationPublisher> mPublishers = new();
    private readonly Dictionary<RefreshCategory, string> mFormatKeys = new();

    // In-memory baseline cache. Loaded once at construction; mutated on every
    // event that crosses the publish threshold; flushed to the settings store
    // fire-and-forget after each mutation. Lock for any access — the queue
    // worker fires Completed on the thread-pool and multiple categories can
    // overlap.
    private readonly object mBaselineLock = new();
    private CollectionBaselineStore mBaseline = new();

    private bool mDisposed;

    public CollectionGrowthNotificationProducer(
        IPlayerRefreshQueueService queue,
        IInternalDataPlayerWatcher watcher,
        INexusDbContextFactory db,
        ISettingsStore store,
        IPlayerChangeSignal signal,
        IChatNotificationRegistry registry,
        ILocalizer localizer,
        ILogger<CollectionGrowthNotificationProducer> log)
    {
        mQueue = queue;
        mWatcher = watcher;
        mDb = db;
        mStore = store;
        mSignal = signal;
        mLoc = localizer;
        mLog = log;

        Register(registry, RefreshCategory.Mounts,
            MountsIncreasedKindId,
            "ui.notifications.enrichment.mounts_increased");
        Register(registry, RefreshCategory.Minions,
            MinionsIncreasedKindId,
            "ui.notifications.enrichment.minions_increased");
        Register(registry, RefreshCategory.Achievements,
            AchievementsIncreasedKindId,
            "ui.notifications.enrichment.achievements_increased");

        _ = LoadBaselineAsync();

        mQueue.Completed += OnCompleted;
    }

    private void Register(IChatNotificationRegistry registry,
                          RefreshCategory category,
                          string kindId,
                          string resxRoot)
    {
        // Default-OFF + suppressed by the general catchall: the settings UI
        // greys these out while the catchall is on, preventing the user
        // from accidentally enabling both and getting duplicate lines per
        // growth event.
        mPublishers[category] = registry.RegisterKind(new NotificationKindDefinition(
            Id: kindId,
            LabelKey: resxRoot + ".label",
            DescriptionKey: resxRoot + ".description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Green,
            GroupKey: GroupKey,
            DefaultEnabled: false,
            SuppressedBy: new[] { GeneralChangeNotificationProducer.KindId }));
        mFormatKeys[category] = resxRoot + ".format";
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mQueue.Completed -= OnCompleted;
    }

    private void OnCompleted(ulong contentId, RefreshCategory category)
    {
        if (!mPublishers.ContainsKey(category)) return;
        // Fire-and-forget on the thread-pool — Completed handlers shouldn't
        // block the queue worker on DB queries.
        _ = HandleAsync(contentId, category);
    }

    private async Task HandleAsync(ulong contentId, RefreshCategory category)
    {
        try
        {
            if (!mWatcher.TryGetObserved(contentId, out var observed) || observed is null)
                return;
            if (observed.LodestoneId is not { } lodestoneId) return;

            var kind = MapToCollectionKind(category);
            int currentCount;
            try
            {
                await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);
                currentCount = await ctx.Set<PlayerCollectionStatsEntity>()
                    .Where(s => s.LodestoneId == lodestoneId && s.Kind == kind)
                    .Select(s => (int?)s.Count)
                    .FirstOrDefaultAsync().ConfigureAwait(false) ?? 0;
            }
            catch (Exception ex)
            {
                mLog.LogWarning(ex,
                    "Collection-growth count lookup failed for cid={Cid} cat={Cat}",
                    contentId, category);
                return;
            }

            // First sighting for this (cid, kind) establishes baseline silently.
            // Subsequent equal-or-lower counts no-op (FFXIVCollect counts can
            // only grow, but a transient empty cache could yield 0 — treat that
            // as "no signal" rather than a regression that resets baseline).
            int? prior;
            var growth = 0;
            lock (mBaselineLock)
            {
                var key = BaselineKey(contentId, category);
                if (!mBaseline.Counts.TryGetValue(key, out var existing))
                {
                    mBaseline.Counts[key] = currentCount;
                    prior = null;
                }
                else if (currentCount > existing)
                {
                    growth = currentCount - existing;
                    mBaseline.Counts[key] = currentCount;
                    prior = existing;
                }
                else
                {
                    return; // no change worth publishing; skip the persist roundtrip
                }
            }

            // Persist after any cache mutation. Fire-and-forget — Set is a
            // single SQLite write keyed by the well-known store key.
            _ = PersistBaselineAsync();

            if (prior is null) return; // baseline-only, don't publish

            Publish(contentId, category, growth);

            // Feed the cross-producer aggregator so the GeneralChange
            // producer fires once per debounce window, regardless of how
            // many categories grow at the same time for this player.
            mSignal.Signal(contentId);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex,
                "Collection-growth handler crashed for cid={Cid} cat={Cat}",
                contentId, category);
        }
    }

    private void Publish(ulong contentId, RefreshCategory category, int growth)
    {
        if (!mPublishers.TryGetValue(category, out var publisher)) return;
        if (!mFormatKeys.TryGetValue(category, out var formatKey)) return;

        var name = NameFor(contentId) ?? "—";
        var line = string.Format(CultureInfo.CurrentCulture,
            mLoc.Get(formatKey), name, growth);
        publisher.Publish(new SeString(new TextPayload(line)));
    }

    private string? NameFor(ulong contentId)
    {
        foreach (var p in mWatcher.Recent)
            if (p.ContentId == contentId) return p.Name;
        return null;
    }

    private static CollectionKind MapToCollectionKind(RefreshCategory category) => category switch
    {
        RefreshCategory.Mounts       => CollectionKind.Mounts,
        RefreshCategory.Minions      => CollectionKind.Minions,
        RefreshCategory.Achievements => CollectionKind.Achievements,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>Stable dictionary key for the baseline blob — content id then
    /// the category's byte value, colon-separated. Format is internal: the
    /// blob is wholly owned by this producer and only round-trips through
    /// JSON, so changing the shape is safe (existing keys would just become
    /// orphan entries; the user can wipe via DbInspect).</summary>
    private static string BaselineKey(ulong contentId, RefreshCategory category)
        => contentId.ToString(CultureInfo.InvariantCulture) + ":" + ((byte)category).ToString(CultureInfo.InvariantCulture);

    private async Task LoadBaselineAsync()
    {
        try
        {
            var loaded = await mStore.GetAsync<CollectionBaselineStore>(BaselineStoreKey).ConfigureAwait(false);
            if (loaded is null) return;
            lock (mBaselineLock) mBaseline = loaded;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Collection-growth baseline load failed; starting with empty cache.");
        }
    }

    private async Task PersistBaselineAsync()
    {
        try
        {
            // Snapshot under the lock so the writer doesn't observe a torn
            // dictionary; SetAsync runs against the copy. Cheap — even thousands
            // of (cid, kind) entries are well under 1MB JSON.
            CollectionBaselineStore snapshot;
            lock (mBaselineLock)
            {
                snapshot = new CollectionBaselineStore
                {
                    Counts = new Dictionary<string, int>(mBaseline.Counts, StringComparer.Ordinal),
                };
            }
            await mStore.SetAsync(BaselineStoreKey, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Collection-growth baseline persist failed.");
        }
    }
}

/// <summary>Persisted baseline blob for the collection-growth producer.
/// Keys are <c>"{contentId}:{categoryByte}"</c>; values are the last count we
/// either observed as baseline or just published as a growth event.
/// <para>POCO is public so JSON serialization sees its property; the
/// instance lives entirely inside the producer.</para></summary>
public sealed class CollectionBaselineStore
{
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);
}
