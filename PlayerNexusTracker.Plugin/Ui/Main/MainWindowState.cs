using Microsoft.Extensions.Logging;
using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Shared selection / loading state between the left player list and the right
/// detail panel. Selection is the <see cref="ObservedPlayer"/> the user clicked —
/// the rich Lodestone-backed <see cref="Player"/> and the change-history list are
/// fetched asynchronously when a selection lands.
/// <para>Subscribes to the watcher's <c>Observed</c> event and the history service's
/// <c>HistoryAdded</c> event so the detail panel stays live without re-clicking.</para>
/// </summary>
public sealed class MainWindowState : IDisposable
{
    private readonly IExternalDataPlayerService mPlayers;
    private readonly IExternalDataFreeCompanyService mFreeCompanies;
    private readonly IInternalDataHistoryService mHistory;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IInternalDataEncounterTracker mEncounters;
    private readonly IExternalDataAchievementCatalog mAchievementCatalog;
    private readonly IExternalDataMountCatalog mMountCatalog;
    private readonly IExternalDataMinionCatalog mMinionCatalog;
    private readonly IPlayerRefreshQueueService mRefreshQueue;
    private readonly ILogger<MainWindowState> mLog;
    /// <summary>Per-character set of unread history kinds. Populated once at startup,
    /// kept current via <see cref="IInternalDataHistoryService.HistoryAdded"/> (adds the
    /// affected character and kinds) and <see cref="IInternalDataHistoryService.HistoryRead"/>
    /// (drops the character). Drives the player-list dot + its hover-tooltip.</summary>
    private readonly Dictionary<ulong, HashSet<PlayerHistoryKind>> mUnreadKindsByContentId = new();
    private string? mPendingTabKey;
    private CancellationTokenSource? mPlayerFetch;
    private CancellationTokenSource? mHistoryFetch;
    private bool mDisposed;

    public MainWindowState(
        IExternalDataPlayerService players,
        IExternalDataFreeCompanyService freeCompanies,
        IInternalDataHistoryService history,
        IInternalDataPlayerWatcher watcher,
        IInternalDataEncounterTracker encounters,
        IExternalDataAchievementCatalog achievementCatalog,
        IExternalDataMountCatalog mountCatalog,
        IExternalDataMinionCatalog minionCatalog,
        IPlayerRefreshQueueService refreshQueue,
        ILogger<MainWindowState> log)
    {
        mPlayers = players;
        mFreeCompanies = freeCompanies;
        mHistory = history;
        mWatcher = watcher;
        mEncounters = encounters;
        mAchievementCatalog = achievementCatalog;
        mMountCatalog = mountCatalog;
        mMinionCatalog = minionCatalog;
        mRefreshQueue = refreshQueue;
        mLog = log;

        mWatcher.Observed += OnWatcherObserved;
        mHistory.HistoryAdded += OnHistoryAdded;
        mHistory.HistoryRead += OnHistoryRead;
        mHistory.AllHistoryRead += OnAllHistoryRead;
        mRefreshQueue.Enqueued += OnQueueChanged;
        mRefreshQueue.Completed += OnQueueChanged;
        mEncounters.EncountersChanged += OnEncountersChanged;

        // Kick off all three FFXIVCollect catalogs in parallel — totals power the
        // Summary tab's "0 / <max>" fallback when a character has no collection
        // stats yet, and the achievement dictionary lights up the Achievements
        // tab's name resolution. All three are one-time per session.
        _ = LoadCatalogsAsync();

        // One-shot bootstrap of "which characters have any history at all" — used
        // by the list panel to dot rows worth opening. Kept current by
        // OnHistoryAdded which adds the affected content id on each new row.
        _ = LoadHistoryContentIdsAsync();
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mWatcher.Observed -= OnWatcherObserved;
        mHistory.HistoryAdded -= OnHistoryAdded;
        mHistory.HistoryRead -= OnHistoryRead;
        mHistory.AllHistoryRead -= OnAllHistoryRead;
        mRefreshQueue.Enqueued -= OnQueueChanged;
        mRefreshQueue.Completed -= OnQueueChanged;
        mEncounters.EncountersChanged -= OnEncountersChanged;
        mPlayerFetch?.Cancel();
        mHistoryFetch?.Cancel();
        mDetailFetch?.Cancel();
        mFcCandidatesFetch?.Cancel();
    }

    /// <summary>The observation entry the user picked — non-null as soon as anyone clicks
    /// a row. Always available, regardless of whether Lodestone enrichment completed.</summary>
    public ObservedPlayer? SelectedObserved { get; private set; }

    /// <summary>The fully-enriched player from Lodestone/FFXIVCollect. Null while the
    /// initial fetch is in flight, when no LodestoneId is known, or when enrichment failed.</summary>
    public Player? CurrentPlayer { get; private set; }

    /// <summary>Tag+world FC candidates surfaced when <see cref="CurrentPlayer"/> has no
    /// resolved <see cref="Player.FreeCompany"/> but the observation carries a live
    /// <c>CompanyTag</c>. (tag, world_id) is intentionally non-unique — the list can
    /// contain multiple legitimate FCs, and the FC tab always renders it with an
    /// ambiguity warning. Null when no fetch has happened yet for the current selection;
    /// empty when a fetch happened and nothing matched.</summary>
    public IReadOnlyList<FreeCompany>? CurrentFcCandidates { get; private set; }
    private CancellationTokenSource? mFcCandidatesFetch;

    /// <summary>Lazy-loaded heavy fields (full Customize bytes, Notes text) for the
    /// selected character — null while loading or when no observation row exists.
    /// The slim <see cref="SelectedObserved"/> stays the source of truth for
    /// hot-path fields; this carries only what the detail panel + notes tab need.</summary>
    public ObservedPlayerDetail? CurrentDetail { get; private set; }
    private CancellationTokenSource? mDetailFetch;

    /// <summary>True while a remote refresh is in flight for the selected player.
    /// Derived from <see cref="CurrentQueueStatus"/>: the queue worker is the only
    /// thing that hits the network now (ReloadPlayerAsync is cache-only), so any
    /// pending queue row for this player means "still fetching". Drives the
    /// toolbar's spinner-vs-button swap.</summary>
    public bool IsRefreshing => CurrentQueueStatus.PendingForContentId > 0;

    /// <summary>The persisted change-history for the selected character, newest first.
    /// Null while the load is in flight (HistoryTab renders a spinner); empty list when
    /// the player has no recorded changes yet.</summary>
    public IReadOnlyList<PlayerHistoryEntry>? CurrentHistory { get; private set; }

    /// <summary>Lookup map for FC Lodestone ids referenced by the current
    /// history rows — populated alongside <see cref="CurrentHistory"/> by a
    /// single cache-only catalog read. Maps id → "«TAG» Name" display string
    /// (or the FC name alone when no tag is on file). Used by
    /// <see cref="ResolveFcLabel"/>, which the History tab passes into
    /// <see cref="HistoryFormatting.FormatChange"/> so rows render readable
    /// labels instead of raw <c>FC#xxx</c> identifiers. Empty when no history
    /// has been loaded yet or no kind=4 rows are present.</summary>
    private IReadOnlyDictionary<string, string>? mFcLabelMap;

    /// <summary>Resolver suitable for passing as the fcLabel parameter of
    /// <see cref="HistoryFormatting.FormatChange"/>. Returns null when the
    /// catalog has no row for the id (renderer then falls back to the raw
    /// <c>FC#xxx</c> form), or when the map hasn't been populated yet.</summary>
    public string? ResolveFcLabel(string fcLodestoneId)
        => mFcLabelMap is not null && mFcLabelMap.TryGetValue(fcLodestoneId, out var label) ? label : null;

    /// <summary>Freshest <c>UpdatedAt</c> across the selected player's persisted Lodestone
    /// sub-resources, polled at selection time and on each successful refresh. Drives the
    /// "Updated Xh ago" badge in the detail header. Null when nothing is enriched yet.</summary>
    public DateTime? CurrentLastRefreshedAt { get; private set; }

    /// <summary>Per-sub-resource <c>UpdatedAt</c> breakdown for the selected player,
    /// loaded alongside <see cref="CurrentLastRefreshedAt"/>. Drives the tooltip on
    /// the "Updated Xh ago" line so the user can see which of the seven categories
    /// is actually fresh vs. stale instead of seeing only the max. Null while loading
    /// or when no LodestoneId is on file.</summary>
    public PlayerRefreshBreakdown? CurrentRefreshBreakdown { get; private set; }

    /// <summary>Refresh-queue snapshot for the selected player. Refreshed on selection
    /// and whenever the queue's Enqueued / Completed events fire. Drives the
    /// "queued behind N items" header badge.</summary>
    public QueueStatusForContent CurrentQueueStatus { get; private set; }

    /// <summary>Encounter rows for the selected player, newest first. Null
    /// while the load is in flight; empty list when no encounters exist yet.
    /// Re-loaded on selection and whenever the tracker's
    /// <see cref="IInternalDataEncounterTracker.EncountersChanged"/> event
    /// fires for the active character.</summary>
    public IReadOnlyList<EncounterEntry>? CurrentEncounters { get; private set; }

    /// <summary>Total encounter-row count for the selected player — replaces
    /// the retired <c>SeenCount</c> column on observed_player. Drives the
    /// Summary tab's "Anzahl Sichtungen" stat. Null while loading.</summary>
    public int? CurrentEncounterCount { get; private set; }

    /// <summary>Distinct <see cref="PlayerHistoryKind"/>s with at least one UNREAD row
    /// for a character — empty once every row has been seen in the History tab. Drives
    /// the yellow dot AND its hover-tooltip ("Verlauf: Umbenennung, …"). Returns a
    /// fresh snapshot — safe to enumerate on the draw thread.</summary>
    public IReadOnlyList<PlayerHistoryKind> GetUnreadKinds(ulong contentId)
    {
        lock (mUnreadKindsByContentId)
        {
            return mUnreadKindsByContentId.TryGetValue(contentId, out var kinds)
                ? kinds.ToArray()
                : Array.Empty<PlayerHistoryKind>();
        }
    }

    /// <summary>Number of characters with at least one unread history row.
    /// Drives the "mark all read" toolbar button (hidden when zero) and the
    /// tooltip's count formatter. Snapshot read — safe on the draw thread.</summary>
    public int UnreadPlayerCount
    {
        get { lock (mUnreadKindsByContentId) return mUnreadKindsByContentId.Count; }
    }

    /// <summary>Asynchronously flags every history row for a character as read.
    /// Called by the History tab the first frame it's open. The
    /// <see cref="IInternalDataHistoryService.HistoryRead"/> event then clears the
    /// dot via <see cref="OnHistoryRead"/>.</summary>
    public Task MarkHistoryReadAsync(ulong contentId)
        => mHistory.MarkAllReadForContentIdAsync(contentId);

    /// <summary>Asynchronously flags every unread history row across all characters
    /// as read in one DB roundtrip. Drives the toolbar's "mark all read" button.
    /// The <see cref="IInternalDataHistoryService.AllHistoryRead"/> event clears the
    /// in-memory unread index via <see cref="OnAllHistoryRead"/>.</summary>
    public Task MarkAllHistoryReadAsync()
        => mHistory.MarkAllReadAsync();

    /// <summary>Persists user-authored notes for an observed character and
    /// re-loads <see cref="CurrentDetail"/> so the NotesTab's "persisted
    /// snapshot" reflects the new value. The watcher also fires
    /// <see cref="IInternalDataPlayerWatcher.Observed"/> with an updated
    /// HasNotes flag; <see cref="OnWatcherObserved"/> picks that up so the
    /// list-panel "Has notes" filter sees the change too.</summary>
    public async Task<bool> SaveNotesAsync(ulong contentId, string? notes)
    {
        var ok = await mWatcher.UpdateNotesAsync(contentId, notes).ConfigureAwait(false);
        if (ok && SelectedObserved?.ContentId == contentId)
            await ReloadDetailAsync(contentId).ConfigureAwait(false);
        return ok;
    }

    /// <summary>Cross-component intent: the player-list dot click sets the History
    /// tab key; the detail panel reads it on the next draw and passes
    /// <c>ImGuiTabItemFlags.SetSelected</c> to that tab's <c>BeginTabItem</c>.
    /// The pending value is consumed by <see cref="ConsumePendingTab"/>.</summary>
    public void RequestTabActivation(string tabKey) => mPendingTabKey = tabKey;

    /// <summary>Returns true and clears the pending intent if it matches
    /// <paramref name="tabKey"/>. Returns false otherwise (no consumption).</summary>
    public bool ConsumePendingTab(string tabKey)
    {
        if (mPendingTabKey != tabKey) return false;
        mPendingTabKey = null;
        return true;
    }

    /// <summary>Tab keys used by <see cref="RequestTabActivation"/>. Strings so the
    /// detail panel can compare without taking a dependency on an enum that lives
    /// outside its file.</summary>
    public const string TabHistory = "history";


    /// <summary>Full FFXIVCollect achievement catalog keyed by id, loaded once
    /// per session. Null while the initial fetch is in flight.</summary>
    public IReadOnlyDictionary<int, AchievementEntry>? AchievementCatalog { get; private set; }

    /// <summary>Catalog totals (count of all known entries) loaded once per
    /// session — used as the "0 / <c>max</c>" fallback in the Summary tab when
    /// a character has no per-collection stats yet. Null while still loading.</summary>
    public int? AchievementTotal { get; private set; }
    public int? MountTotal { get; private set; }
    public int? MinionTotal { get; private set; }

    public void Select(ObservedPlayer observed)
    {
        SelectedObserved = observed;
        CurrentPlayer = null;
        CurrentDetail = null;
        CurrentHistory = null;
        mFcLabelMap = null;
        CurrentLastRefreshedAt = null;
        CurrentRefreshBreakdown = null;
        CurrentQueueStatus = default;
        CurrentEncounters = null;
        CurrentEncounterCount = null;
        CurrentFcCandidates = null;
        _ = ReloadDetailAsync(observed.ContentId);
        if (observed.LodestoneId is { } lid)
            _ = ReloadPlayerAsync(lid);
        else
            // Lodestone id not resolved yet → no strong-match path is even
            // possible; surface the tag-based candidates immediately so the FC
            // tab has something to render.
            _ = ReloadFcCandidatesAsync(observed);

        // Detail panel just opened — queue whatever's stale at top priority.
        // EnqueueStale handles the not-yet-resolved case too: if no Lodestone
        // id is on file it only queues the LodestoneId category and the
        // worker cascades the rest when the id arrives. If every sub-resource
        // is within TTL, ComputeStaleSubResourcesAsync returns empty and
        // nothing is fetched — the cached UpdatedAt values stay put and the
        // per-category breakdown tooltip surfaces that exact state.
        _ = mRefreshQueue.EnqueueStaleAsync(observed.ContentId, RefreshPriority.Immediate);
        _ = ReloadHistoryAsync(observed.ContentId);
        _ = ReloadQueueStatusAsync(observed.ContentId);
        _ = ReloadEncountersAsync(observed.ContentId);
    }

    public void Deselect()
    {
        mPlayerFetch?.Cancel();
        mHistoryFetch?.Cancel();
        mDetailFetch?.Cancel();
        mFcCandidatesFetch?.Cancel();
        SelectedObserved = null;
        CurrentPlayer = null;
        CurrentDetail = null;
        CurrentHistory = null;
        mFcLabelMap = null;
        CurrentLastRefreshedAt = null;
        CurrentRefreshBreakdown = null;
        CurrentQueueStatus = default;
        CurrentEncounters = null;
        CurrentEncounterCount = null;
        CurrentFcCandidates = null;
        // IsRefreshing follows CurrentQueueStatus automatically (now derived).
    }

    public void Refresh()
    {
        if (SelectedObserved is not { } observed) return;
        if (observed.LodestoneId is { } id) _ = ReloadPlayerAsync(id);

        // Explicit user action — bypass freshness, force every category back
        // into the queue. When the LodestoneId is still unknown the queue
        // service inserts the resolution task plus the sub-resources, and the
        // worker cascades them as soon as the id resolves.
        _ = mRefreshQueue.EnqueueAllAsync(observed.ContentId, RefreshPriority.Immediate);
        _ = ReloadHistoryAsync(observed.ContentId);
    }

    /// <summary>Re-queue every supplied player at Low priority, stale-only.
    /// Used by the list-toolbar bulk refresh. Low lane keeps the batch behind
    /// live in-range updates (High) and other user clicks (Immediate); the
    /// per-category TTL check inside the queue service leaves fresh sub-resources
    /// untouched.</summary>
    public void RefreshVisibleStale(IReadOnlyList<ObservedPlayer> players)
    {
        foreach (var p in players)
            _ = mRefreshQueue.EnqueueStaleAsync(p.ContentId, RefreshPriority.Low);
    }

    private void OnWatcherObserved(ObservedPlayer p)
    {
        // Fast-path: skip the 99% case where the observed update is for somebody else.
        if (SelectedObserved is not { } current) return;
        if (current.ContentId != p.ContentId) return;

        var lodestoneJustResolved = current.LodestoneId is null && p.LodestoneId is not null;
        // Reference assignment is atomic — UI reads on the draw thread see either the
        // old or new record, never a torn snapshot.
        SelectedObserved = p;

        // If enrichment just resolved a Lodestone id for the selected player, fetch
        // the full Lodestone-backed Player automatically so the user doesn't have to
        // click Refresh themselves.
        if (lodestoneJustResolved)
            _ = ReloadPlayerAsync(p.LodestoneId!.Value);
    }

    private void OnHistoryAdded(ulong contentId, IReadOnlyList<PlayerHistoryEntry> entries)
    {
        // Keep the unread-kind index up to date even when the affected character
        // isn't the one currently selected — the list-panel dot (and its
        // hover-tooltip) has to light up for newly-relevant entries without
        // a restart. Newly-written rows are always unread by definition.
        lock (mUnreadKindsByContentId)
        {
            if (!mUnreadKindsByContentId.TryGetValue(contentId, out var set))
            {
                set = new HashSet<PlayerHistoryKind>();
                mUnreadKindsByContentId[contentId] = set;
            }
            for (var i = 0; i < entries.Count; i++) set.Add(entries[i].Kind);
        }

        if (SelectedObserved?.ContentId != contentId) return;
        _ = ReloadHistoryAsync(contentId);
    }

    private void OnHistoryRead(ulong contentId)
    {
        // The whole character drops out of the unread map — the bulk mark-read
        // covers every kind for that content id by construction.
        lock (mUnreadKindsByContentId) mUnreadKindsByContentId.Remove(contentId);

        // No CurrentHistory reload here: the rows themselves are unchanged
        // (only the is_read flag flipped), and the History tab doesn't render
        // anything that depends on the flag.
    }

    private void OnAllHistoryRead()
    {
        lock (mUnreadKindsByContentId) mUnreadKindsByContentId.Clear();
    }

    private async Task LoadHistoryContentIdsAsync()
    {
        try
        {
            var byId = await mHistory.GetUnreadHistoryKindsByContentIdAsync().ConfigureAwait(false);
            lock (mUnreadKindsByContentId)
            {
                mUnreadKindsByContentId.Clear();
                foreach (var kv in byId)
                    mUnreadKindsByContentId[kv.Key] = new HashSet<PlayerHistoryKind>(kv.Value);
            }
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: unread history kind-index load failed.");
        }
    }

    private async Task ReloadDetailAsync(ulong contentId)
    {
        mDetailFetch?.Cancel();
        var cts = new CancellationTokenSource();
        mDetailFetch = cts;

        try
        {
            var detail = await mWatcher.GetDetailAsync(contentId, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            // Selection might have flipped while we awaited — only land the
            // result when it still describes the open detail panel.
            if (SelectedObserved?.ContentId == contentId)
                CurrentDetail = detail;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: detail load failed for {ContentId}", contentId);
        }
    }

    private async Task ReloadPlayerAsync(ulong id)
    {
        mPlayerFetch?.Cancel();
        var cts = new CancellationTokenSource();
        mPlayerFetch = cts;

        try
        {
            // Cache-only — never let the UI's reload path trigger a network fetch.
            // GetAsync would re-fetch every category and bump all seven UpdatedAt
            // values to "now" on every click, completely ignoring the 7-day TTL
            // (TTL is enforced inside PlayerRefreshQueueService.ComputeStaleSub-
            // ResourcesAsync, which the queue worker honors). Remote refreshes
            // flow exclusively through the queue: EnqueueStaleAsync below for
            // automatic stale-only fetches, and Refresh() for the user-driven
            // force-fetch. Completion of either fires OnQueueChanged, which
            // re-reads cache to surface the new data.
            var player = await mPlayers.GetCachedAsync(id, PlayerInclude.All, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            if (SelectedObserved?.LodestoneId == id)
            {
                CurrentPlayer = player;
                // Same DB roundtrip pattern as the player itself — pull the
                // per-sub-resource UpdatedAt right after the load so the
                // "Updated Xh ago" badge and its breakdown tooltip both
                // reflect the row we just rendered. We derive the max from
                // the breakdown so the two readings stay in sync (the
                // service's GetLastRefreshedAtAsync delegates to the same
                // breakdown internally).
                var breakdown = await mPlayers.GetRefreshBreakdownAsync(id, cts.Token)
                    .ConfigureAwait(false);
                if (SelectedObserved?.LodestoneId == id)
                {
                    CurrentRefreshBreakdown = breakdown;
                    CurrentLastRefreshedAt = MaxOf(breakdown);
                }

                // Tag-based FC candidates only matter when the strong-match
                // (profile → FreeCompany) didn't resolve. Skip the DB roundtrip
                // when we already have a hard FC link, and clear any stale list
                // left over from a previous selection.
                if (SelectedObserved is { } obs)
                {
                    if (player?.FreeCompany is not null) CurrentFcCandidates = Array.Empty<FreeCompany>();
                    else _ = ReloadFcCandidatesAsync(obs);
                }
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: player load failed for {Id}", id);
        }
    }

    private static DateTime? MaxOf(PlayerRefreshBreakdown b)
    {
        DateTime? max = null;
        foreach (var v in new[] { b.ProfileAt, b.ClassJobsAt, b.GearAt, b.FreeCompanyAt,
                                   b.MountsAt, b.MinionsAt, b.AchievementsAt })
            if (v is { } d && (max is null || d > max)) max = d;
        return max;
    }

    private async Task LoadCatalogsAsync()
    {
        var achievementsTask = LoadAchievementCatalogAsync();
        var mountsTask = LoadMountTotalAsync();
        var minionsTask = LoadMinionTotalAsync();
        await Task.WhenAll(achievementsTask, mountsTask, minionsTask).ConfigureAwait(false);
    }

    private async Task LoadAchievementCatalogAsync()
    {
        try
        {
            var list = await mAchievementCatalog.ListAsync().ConfigureAwait(false);
            var dict = new Dictionary<int, AchievementEntry>(list.Count);
            foreach (var a in list) dict[a.Id] = a;
            AchievementCatalog = dict;
            AchievementTotal = list.Count;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: achievement catalog load failed.");
        }
    }

    private async Task LoadMountTotalAsync()
    {
        try
        {
            var list = await mMountCatalog.ListAsync().ConfigureAwait(false);
            MountTotal = list.Count;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: mount catalog load failed.");
        }
    }

    private async Task LoadMinionTotalAsync()
    {
        try
        {
            var list = await mMinionCatalog.ListAsync().ConfigureAwait(false);
            MinionTotal = list.Count;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: minion catalog load failed.");
        }
    }

    private void OnQueueChanged(ulong contentId, RefreshCategory category)
    {
        // Queue events fire on the worker thread for any character; we only
        // care when the change touches the currently-selected one or when
        // the worker just completed someone else (which can change the
        // selected player's "rows ahead" count too — recompute either way
        // if a player is selected). Reload is cheap (single indexed scan).
        if (SelectedObserved is not { } observed) return;
        _ = ReloadQueueStatusAsync(observed.ContentId);

        // The worker just persisted a sub-resource for the selected player —
        // reload the cached Player + breakdown so the UI sees the freshly-
        // written rows. The event fires for Enqueued too, where this is a
        // harmless extra DB roundtrip (no UpdatedAt change yet), so we don't
        // bother distinguishing Enqueued vs Completed at this layer.
        if (observed.ContentId == contentId && observed.LodestoneId is { } lid)
            _ = ReloadPlayerAsync(lid);
    }

    private async Task ReloadQueueStatusAsync(ulong contentId)
    {
        try
        {
            var status = await mRefreshQueue.GetQueueStatusForAsync(contentId).ConfigureAwait(false);
            if (SelectedObserved?.ContentId == contentId)
                CurrentQueueStatus = status;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: queue status load failed for {ContentId}", contentId);
        }
    }

    private async Task ReloadEncountersAsync(ulong contentId)
    {
        try
        {
            var entries = await mEncounters.GetForContentIdAsync(contentId).ConfigureAwait(false);
            var count = await mEncounters.GetEncounterCountAsync(contentId).ConfigureAwait(false);
            // Selection might have changed while we awaited — only land the
            // results when they still describe the open detail panel.
            if (SelectedObserved?.ContentId != contentId) return;
            CurrentEncounters = entries;
            CurrentEncounterCount = count;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: encounter reload failed for {ContentId}", contentId);
        }
    }

    private void OnEncountersChanged(ulong contentId, long encounterId)
    {
        if (SelectedObserved?.ContentId != contentId) return;
        _ = ReloadEncountersAsync(contentId);
    }

    private async Task ReloadFcCandidatesAsync(ObservedPlayer observed)
    {
        mFcCandidatesFetch?.Cancel();
        var cts = new CancellationTokenSource();
        mFcCandidatesFetch = cts;

        // Without a live tag + home world there's nothing to look up — clear
        // any leftover candidates so the FC tab falls through to its empty
        // state instead of repeating a previous player's list.
        if (string.IsNullOrEmpty(observed.CompanyTag) || observed.HomeWorldId == 0)
        {
            if (SelectedObserved?.ContentId == observed.ContentId)
                CurrentFcCandidates = Array.Empty<FreeCompany>();
            return;
        }

        try
        {
            var list = await mFreeCompanies.FindCandidatesByTagAndWorldAsync(
                observed.CompanyTag!, observed.HomeWorldId, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            if (SelectedObserved?.ContentId == observed.ContentId)
                CurrentFcCandidates = list;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: FC candidates load failed for {ContentId}", observed.ContentId);
            if (SelectedObserved?.ContentId == observed.ContentId)
                CurrentFcCandidates = Array.Empty<FreeCompany>();
        }
    }

    private async Task ReloadHistoryAsync(ulong contentId)
    {
        mHistoryFetch?.Cancel();
        var cts = new CancellationTokenSource();
        mHistoryFetch = cts;

        try
        {
            var entries = await mHistory.GetForContentIdAsync(contentId, ct: cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            if (SelectedObserved?.ContentId != contentId) return;

            CurrentHistory = entries;

            // Walk the rows for FC ids referenced from FreeCompanyChange
            // entries and prime mFcLabelMap with one batch cache read. Skip
            // the lookup when there's nothing to resolve so the History tab
            // doesn't pay a roundtrip per re-selection of FC-free characters.
            var fcIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (e.Kind != PlayerHistoryKind.FreeCompanyChange) continue;
                if (!string.IsNullOrEmpty(e.OldValue)) fcIds.Add(e.OldValue);
                if (!string.IsNullOrEmpty(e.NewValue)) fcIds.Add(e.NewValue);
            }

            if (fcIds.Count == 0)
            {
                mFcLabelMap = null;
                return;
            }

            var fcs = await mFreeCompanies.GetManyCachedAsync(fcIds, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            if (SelectedObserved?.ContentId != contentId) return;

            var map = new Dictionary<string, string>(fcs.Count, StringComparer.Ordinal);
            foreach (var (id, fc) in fcs)
                map[id] = HistoryFormatting.FormatFreeCompanyLabel(fc);
            mFcLabelMap = map;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "MainWindowState: history load failed for {ContentId}", contentId);
            if (SelectedObserved?.ContentId == contentId)
            {
                CurrentHistory = Array.Empty<PlayerHistoryEntry>();
                mFcLabelMap = null;
            }
        }
    }
}
