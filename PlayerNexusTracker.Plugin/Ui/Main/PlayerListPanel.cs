using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Settings;
using PlayerNexusTracker.Settings.Filters;

namespace PlayerNexusTracker.Ui.Main;

internal enum PlayerListSortField : byte
{
    Name = 0,
    LastSeen = 1,
    FirstSeen = 2,
    Level = 3,
    GearScore = 4,
    MaxJobLevel = 5,
    MountCount = 6,
    MinionCount = 7,
    AchievementCount = 8,
}

internal sealed class PlayerListPanel
{
    private const float RowHeight = 24f;
    private const int DefaultMaxRecent = 100;
    /// <summary>Page size for DB-resolved sort queries. The panel asks the
    /// view for this many rows at a time and pages forward via OFFSET as
    /// the user scrolls toward the end of what's loaded — no hard cap on
    /// total rows, just a steady drip-feed sized to match a couple of
    /// viewports' worth of content.</summary>
    private const int DbPageSize = 500;
    /// <summary>How close to the end of the loaded list (in rows) the
    /// clipper has to render before we kick the next page. Big enough that
    /// the next page lands before the user reaches the bottom, small
    /// enough that we don't fan out pages we don't need.</summary>
    private const int DbPageLookahead = 100;
    /// <summary>How long to suppress repeat DB queries after a successful
    /// one. Filter-id / source-revision changes ignore this timer (user
    /// action expects immediate refresh); only watcher.Revision-driven
    /// re-queries get debounced.</summary>
    private static readonly TimeSpan DbQuerySettle = TimeSpan.FromMilliseconds(500);
    private const string KeyLastFilter = "ui.pntracker.main.list.filter";
    /// <summary>Active user-filter selection. Persisted as a bare Guid (or
    /// null for "(No filter)") — matches the simple-scalar storage shape
    /// the system-filter index already uses under <see cref="KeyLastFilter"/>,
    /// rather than wrapping the value in a one-property POCO.</summary>
    private const string KeyUserFilter = "ui.pntracker.main.list.user_filter";
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly MainWindowState mState;
    private readonly IGameDataLookups mLookups;
    private readonly ISettingsStore mSettingsStore;
    private readonly ILocalizer mLoc;
    private readonly PlayerFilterRegistry mFilters;
    private readonly IPlayerFilterDbQueryService mDbQuery;

    private int mFilterIndex; // 0 = Current, 1 = Recent (top N by LastSeen), 2 = All, 3 = Unread history
    private string mSearch = string.Empty;
    private int mMaxRecent = DefaultMaxRecent;
    private PlayerListSortField mSortField = PlayerListSortField.Name;
    /// <summary>Direction applied to <see cref="mSortField"/>. Toggled when
    /// the user clicks the same sort entry a second time in the popup;
    /// otherwise resets to the field's default direction on change of
    /// field (Name defaults to ascending, everything else descending).</summary>
    private bool mSortDescending;
    // Settle timestamp for the DB-query debounce. Updated after each
    // successful query; consulted by EnsureDbQuery before kicking a
    // revision-bump-driven re-query.
    private DateTime mNextAllowedDbQueryAt;

    // Active user filter (second dropdown). Null = "(No filter)" — the first
    // entry in the combo, which means "use the system scope only". Persisted
    // independently of mFilterIndex; the two dropdowns compose at evaluation
    // time, with the textbox stacking on top.
    private Guid? mActiveUserFilterId;
    // Cached compile of the active user filter. Rebuilt whenever the id
    // changes or the source filter's criterion-list hash changes (detected
    // via PlayerFilterEvaluator.ComputeSourceRevision). Keeps per-frame
    // parsing of integer/enum values out of the hot path.
    private CompiledFilter? mCompiled;
    // Tracks the in-flight DB pre-narrow query so we don't fan out duplicate
    // SELECTs on every frame while the first one is still running. Cleared
    // when the compile is invalidated.
    private Task? mDbQueryTask;
    // Used by the filter compiler to expand "encountered in any Raid"-style
    // category-only criteria into concrete territory id lists.
    private readonly EncounterCategoryResolver mCategoryResolver;

    public PlayerListPanel(
        IInternalDataPlayerWatcher watcher,
        MainWindowState state,
        IGameDataLookups lookups,
        ISettingsStore settingsStore,
        ILocalizer localizer,
        PlayerFilterRegistry filters,
        IPlayerFilterDbQueryService dbQuery,
        EncounterCategoryResolver categoryResolver)
    {
        mWatcher = watcher;
        mState = state;
        mLookups = lookups;
        mSettingsStore = settingsStore;
        mLoc = localizer;
        mFilters = filters;
        mDbQuery = dbQuery;
        mCategoryResolver = categoryResolver;

        // Load the recent-cap once at startup. Re-read whenever the settings
        // window writes — there's no change-event on ISettingsStore yet, but a
        // plugin reload picks up new values either way.
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var s = await mSettingsStore.GetAsync<TrackerSettings>("config").ConfigureAwait(false);
            if (s is not null && s.MaxRecentPlayers > 0) mMaxRecent = s.MaxRecentPlayers;
        }
        catch { /* keep the default — settings load is non-critical */ }

        try
        {
            var saved = await mSettingsStore.GetAsync<int>(KeyLastFilter).ConfigureAwait(false);
            // 0..3 are the only valid filter slots; anything else gets clamped to 0.
            if (saved is >= 0 and <= 3) mFilterIndex = saved;
        }
        catch { /* keep the default — fall back to "Current" on read failure */ }

        try
        {
            var saved = await mSettingsStore.GetAsync<Guid?>(KeyUserFilter).ConfigureAwait(false);
            // Guid.Empty is the JSON-default for a missing/zero value; treat
            // it the same as null so a fresh install lands on "(No filter)".
            mActiveUserFilterId = saved is { } g && g != Guid.Empty ? g : null;
        }
        catch { /* keep the default — no user filter active */ }

        await LoadSortPreferenceAsync().ConfigureAwait(false);
    }

    /// <summary>Restore the persisted sort field + direction from the bundled
    /// <see cref="PlayerListSortPreference"/> POCO. Defaults to Name ascending
    /// when no preference is persisted yet.</summary>
    private async Task LoadSortPreferenceAsync()
    {
        try
        {
            var pref = await mSettingsStore.GetAsync<PlayerListSortPreference>(
                PlayerListSortPreference.StoreKey).ConfigureAwait(false);
            if (pref is null) return;
            if (Enum.IsDefined(typeof(PlayerListSortField), (byte)pref.FieldIndex))
                mSortField = (PlayerListSortField)pref.FieldIndex;
            mSortDescending = pref.Descending;
        }
        catch { /* keep default Name ascending on read failure */ }
    }

    /// <summary>Write the current <c>(mSortField, mSortDescending)</c> pair
    /// to the bundled POCO key. Called every time the popup mutates the
    /// sort + once during legacy migration.</summary>
    private void PersistSortPreference()
    {
        _ = mSettingsStore.SetAsync(PlayerListSortPreference.StoreKey, new PlayerListSortPreference
        {
            FieldIndex = (int)mSortField,
            Descending = mSortDescending,
        });
    }

    /// <summary>Default sort direction when the user first picks a field
    /// (or on legacy installs without a persisted direction). Name reads
    /// naturally ascending (A→Z); every other field has "bigger is more
    /// interesting" semantics, so descending is the friendlier default.</summary>
    private static bool DefaultDescendingForField(PlayerListSortField field)
        => field != PlayerListSortField.Name;

    public void Draw()
    {
        // Filter labels re-resolved every frame so a culture switch picks up live.
        var systemFilterLabels = new[]
        {
            mLoc.Get("ui.main.list.filter.current"),
            mLoc.Get("ui.main.list.filter.recent"),
            mLoc.Get("ui.main.list.filter.all"),
            mLoc.Get("ui.main.list.filter.unread"),
        };

        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##filter", ref mFilterIndex, systemFilterLabels, systemFilterLabels.Length))
            _ = mSettingsStore.SetAsync(KeyLastFilter, mFilterIndex);

        // Second dropdown: optional user filter. First entry is the fixed
        // "(No filter)" sentinel; the rest mirror the registry's filters in
        // their stored order. Duplicate display names get a "(2)" / "(3)"
        // suffix so the user can tell collisions apart in the combo.
        var (userLabels, userIds) = BuildUserFilterDropdown();
        var userIdx = ResolveUserFilterIndex(userIds);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##user_filter", ref userIdx, userLabels, userLabels.Length))
        {
            mActiveUserFilterId = userIds[userIdx];
            PersistUserFilterSelection();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", mLoc.Get("ui.main.list.search.hint"), ref mSearch, 64);

        // Kick the sort-only DB query when needed (DB sort + no user filter
        // touching the DB). EnsureCompiled handles the user-filter path
        // already; this covers the case where the user just picks "Gear
        // Score ↓" on an otherwise-unfiltered list. Cheap no-op when
        // unneeded or the cache is fresh.
        EnsureUnfilteredOrderedQuery(filterChanged: false);

        var filtered = ApplyFilters();

        // Count line on the left, action cluster on the right. The cluster is
        // [bulk-refresh?] [mark-all-read?] [sort], reserved as a single block
        // up front so the sort button still lands flush right regardless of
        // which optional buttons are showing.
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            string.Format(mLoc.Get("ui.main.list.count"), filtered.Count));

        var unreadCount = mState.UnreadPlayerCount;
        var showMarkAllRead = unreadCount > 0;
        var showBulkRefresh = filtered.Count > 0;

        const float btnWidth = 32f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonCount = 1
                        + (showMarkAllRead ? 1 : 0)
                        + (showBulkRefresh ? 1 : 0);
        var clusterWidth = btnWidth * buttonCount + spacing * (buttonCount - 1);

        ImGui.SameLine();
        var available = ImGui.GetContentRegionAvail().X;
        var pushX = available - clusterWidth;
        if (pushX > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pushX);

        if (showBulkRefresh)
        {
            var refreshTooltip = string.Format(
                mLoc.Get("ui.main.list.bulk_refresh.tooltip"), filtered.Count);
            if (NexusIconButton.Draw(FontAwesomeIcon.CloudDownloadAlt, refreshTooltip))
                mState.RefreshVisibleStale(filtered);
            ImGui.SameLine();
        }

        if (showMarkAllRead)
        {
            var markTooltip = string.Format(
                mLoc.Get("ui.main.list.mark_all_read.tooltip"), unreadCount);
            if (NexusIconButton.Draw(FontAwesomeIcon.CheckDouble, markTooltip))
                _ = mState.MarkAllHistoryReadAsync();
            ImGui.SameLine();
        }

        DrawSortButtonAndPopup();
        ImGui.Separator();

        if (filtered.Count == 0)
        {
            DrawEmptyState();
            return;
        }

        var maxRenderedIdx = -1;
        using (NexusCard.Begin("##recent_list", new Vector2(0, 0), border: false, padding: 4f))
        {
            NexusListClipper.ForEach(filtered, RowHeight, (i, player) =>
            {
                if (i > maxRenderedIdx) maxRenderedIdx = i;
                DrawRow(player);
            });
        }

        // Scroll-paging: when the clipper rendered close to the end of the
        // loaded DB-ordered list, kick the next page so the user keeps
        // scrolling smoothly. No-op for in-memory sorts (whole filtered
        // list is already materialized) and once a page came back short
        // (end-of-stream).
        if (maxRenderedIdx >= 0 && maxRenderedIdx >= filtered.Count - DbPageLookahead)
            EnsureNextDbPage(loadedCount: filtered.Count);
    }

    private string[] BuildSortLabels() => new[]
    {
        mLoc.Get("ui.main.list.sort.name"),
        mLoc.Get("ui.main.list.sort.lastseen"),
        mLoc.Get("ui.main.list.sort.firstseen"),
        mLoc.Get("ui.main.list.sort.level"),
        mLoc.Get("ui.main.list.sort.gearscore"),
        mLoc.Get("ui.main.list.sort.maxjoblevel"),
        mLoc.Get("ui.main.list.sort.mountcount"),
        mLoc.Get("ui.main.list.sort.minioncount"),
        mLoc.Get("ui.main.list.sort.achievementcount"),
    };

    private const string SortPopupId = "##pnt_sort_popup";

    private void DrawSortButtonAndPopup()
    {
        var labels = BuildSortLabels();
        var current = labels[(int)mSortField];

        // Right-align: anchor the icon button to the right edge of the
        // content region. The button's default size is 32×24; computing the
        // offset from the current cursor position (mid-line after SameLine)
        // gives a clean right edge regardless of how wide the count text was.
        var btnWidth = 32f;
        var available = ImGui.GetContentRegionAvail().X;
        if (available > btnWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - btnWidth);

        // Arrow points at where the list grows: A→Z (ascending) is "list grows
        // downward, end is at the bottom" → ↓. Z→A (descending) is "biggest
        // first, end is at the bottom but you're looking upward to find the
        // start" → ↑. This is the file-explorer convention, not the Excel /
        // SQL one (where ↑ = ascending) — chosen because the arrow then reads
        // as "the list is going this way", which matches user intuition more
        // reliably across the rest of the plugin.
        var icon = mSortDescending ? FontAwesomeIcon.SortAmountUp : FontAwesomeIcon.SortAmountDown;
        var directionGlyph = mSortDescending ? "↑" : "↓";
        var tooltip = string.Format(mLoc.Get("ui.main.list.sort.tooltip"), $"{current} {directionGlyph}");
        if (NexusIconButton.Draw(icon, tooltip))
            ImGui.OpenPopup(SortPopupId);

        if (ImGui.BeginPopup(SortPopupId))
        {
            // Direction-toggle row: two compact Selectables side-by-side at
            // the top of the popup. The active direction renders in the
            // "selected" highlight, matching the field list below; clicking
            // the inactive arrow flips direction without touching the field.
            // DontClosePopups keeps the popup open so the user can chain a
            // field switch afterwards if they want.
            // Glyphs follow the same "arrow points where the list grows"
            // convention as the toolbar icon: ↓ for A→Z (ascending), ↑ for
            // Z→A (descending). The underlying mSortDescending semantics
            // are unchanged — only the visual mapping is inverted from the
            // Excel default.
            var dirSize = new Vector2(40f, 0f);
            if (ImGui.Selectable("↓##sort_dir_asc", !mSortDescending,
                    ImGuiSelectableFlags.DontClosePopups, dirSize))
            {
                mSortDescending = false;
                PersistSortPreference();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mLoc.Get("ui.main.list.sort.dir.asc"));

            ImGui.SameLine();
            if (ImGui.Selectable("↑##sort_dir_desc", mSortDescending,
                    ImGuiSelectableFlags.DontClosePopups, dirSize))
            {
                mSortDescending = true;
                PersistSortPreference();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mLoc.Get("ui.main.list.sort.dir.desc"));

            ImGui.Separator();

            for (var i = 0; i < labels.Length; i++)
            {
                var isCurrent = (int)mSortField == i;
                // Active entry gets a direction glyph so the user can see at
                // a glance whether re-clicking will toggle direction (and
                // which way). Inactive entries stay plain text.
                var label = isCurrent ? $"{labels[i]}  {directionGlyph}" : labels[i];
                if (ImGui.Selectable(label, isCurrent))
                {
                    if (isCurrent)
                    {
                        // Same field re-selected → toggle direction.
                        mSortDescending = !mSortDescending;
                    }
                    else
                    {
                        // Different field → set + use that field's natural
                        // default direction.
                        mSortField = (PlayerListSortField)i;
                        mSortDescending = DefaultDescendingForField(mSortField);
                    }
                    PersistSortPreference();
                }
            }
            ImGui.EndPopup();
        }
    }

    /// <summary>True if the active sort field resolves through the SQL view
    /// rather than a slim <c>ObservedPlayer</c> property — drives whether
    /// the panel runs <see cref="IPlayerFilterDbQueryService.RunOrderedAsync"/>
    /// instead of the regular unordered <c>RunAsync</c>.</summary>
    private bool IsDbSort => mSortField is
        PlayerListSortField.GearScore or PlayerListSortField.MaxJobLevel
        or PlayerListSortField.MountCount or PlayerListSortField.MinionCount
        or PlayerListSortField.AchievementCount;

    private string DbSortColumn => mSortField switch
    {
        PlayerListSortField.GearScore => "gear_score",
        PlayerListSortField.MaxJobLevel => "max_job_level",
        PlayerListSortField.MountCount => "mount_count",
        PlayerListSortField.MinionCount => "minion_count",
        PlayerListSortField.AchievementCount => "achievement_count",
        _ => "content_id",  // never hit when IsDbSort is true; defensive fallback
    };

    private (string[] Labels, Guid?[] Ids) BuildUserFilterDropdown()
    {
        var filters = mFilters.Filters;
        // Always-present "(No filter)" sentinel + one row per user filter.
        var labels = new string[filters.Count + 1];
        var ids = new Guid?[filters.Count + 1];
        labels[0] = mLoc.Get("ui.main.list.user_filter.none");
        ids[0] = null;

        // Cheap per-name counter — typical N is 0..10, so a plain list is
        // faster than a dictionary and produces stable ordering.
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < filters.Count; i++)
            nameCounts[NormalizeName(filters[i].Name)] = 0;

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var baseName = NormalizeName(f.Name);
            var n = ++nameCounts[baseName];
            // Anything past the first occurrence gets a "(2)" / "(3)" suffix.
            // Done at render time so renames don't accidentally lock in a
            // stale disambiguator — the Guid is the persistent identity.
            labels[i + 1] = n == 1 ? baseName : $"{baseName} ({n})";
            ids[i + 1] = f.Id;
        }
        return (labels, ids);
    }

    private string NormalizeName(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? mLoc.Get("ui.pntracker.filter.unnamed") : raw.Trim();

    private int ResolveUserFilterIndex(Guid?[] ids)
    {
        if (mActiveUserFilterId is not { } active) return 0;
        for (var i = 1; i < ids.Length; i++)
            if (ids[i] == active) return i;

        // Active filter was deleted (locally or via external edit). Silently
        // fall back to "(No filter)" + clear the persisted selection so the
        // state is consistent for the next session too.
        mActiveUserFilterId = null;
        PersistUserFilterSelection();
        return 0;
    }

    private void PersistUserFilterSelection()
    {
        // Persist as Guid? directly — JsonSerializer writes either a quoted
        // GUID string or the JSON literal "null", which the settings table
        // stores as a single scalar value just like the system filter int.
        _ = mSettingsStore.SetAsync<Guid?>(KeyUserFilter, mActiveUserFilterId);
    }

    private IReadOnlyList<ObservedPlayer> ApplyFilters()
    {
        CompiledFilter? compiled = null;
        EvalContext? evalCtx = null;
        if (mActiveUserFilterId is { } activeId)
        {
            var source = mFilters.FindById(activeId);
            if (source is not null)
            {
                EnsureCompiled(source);
                compiled = mCompiled;
                evalCtx = new EvalContext
                {
                    CurrentlyVisible = mWatcher.CurrentlyVisible,
                    HasUnreadHistory = id => mState.GetUnreadKinds(id).Count > 0,
                    Lookups = mLookups,
                    UtcNow = DateTime.UtcNow,
                };
            }
            // Selection-points-at-deleted-filter case: handled by the next
            // ResolveUserFilterIndex on draw — for this frame we just leave
            // compiled null and skip the user-filter stage, so the list
            // doesn't flash empty for one frame.
        }

        var dbQueryPending = compiled is not null
            && compiled.RequiresDbQuery
            && compiled.DbAllowedContentIds is null
            && compiled.DbOrderedContentIds is null;
        var dbSortPending = IsDbSort
            && (compiled is null || !compiled.RequiresDbQuery)
            && mUnfilteredOrderedContentIds is null;
        if (dbQueryPending || dbSortPending)
        {
            // Same one-frame transient as D3's notes-content path: while the
            // DB roundtrip is in flight we return nothing; next frame after
            // the load lands the real list shows up.
            return Array.Empty<ObservedPlayer>();
        }

        // Candidate-set construction. The order matters because we're going
        // to honour DB-driven ordering when it's present — bypassing the
        // system-filter OrderByDescending(LastSeen) for the "Recent" preset.
        IEnumerable<ObservedPlayer> candidates;
        if (compiled is { DbOrderedContentIds: { } ordered })
        {
            // DB sort + user filter: iterate the ordered ContentId list,
            // hydrate from the in-memory map. Order from the query is the
            // final render order.
            candidates = HydrateFromIds(ordered);
        }
        else if (compiled is { DbAllowedContentIds: { Count: var setCount } set }
                 && setCount < mWatcher.Recent.Count)
        {
            // Loop-flip: a selective DB filter (e.g. specific FC name) returns
            // a small ContentId set. Iterating that set + dictionary lookup
            // beats iterating Recent and probing set membership on every row.
            candidates = HydrateFromIds(set);
        }
        else if (IsDbSort && mUnfilteredOrderedContentIds is { } unfiltered)
        {
            // DB-sort without DB-touching filter — the unfiltered ordered list
            // is the candidate. Order is already correct; later in-memory
            // criteria filtering preserves the relative order via Enumerable.Where.
            candidates = HydrateFromIds(unfiltered);
        }
        else
        {
            candidates = mWatcher.Recent;
        }

        // Apply the user-filter's in-memory criteria FIRST, then the
        // system-filter preset on top. The Recent preset's Take(mMaxRecent)
        // would otherwise cap the input *before* user-filter narrowing,
        // turning "Recent + FC:ATRS" into "of the 100 most recently seen
        // players (any FC), show the ATRS ones" — easily empty when the
        // recent set is dominated by non-matching players. Reordering means
        // Recent counts the top N *of the filtered set*, which is what the
        // user dropdowns visually suggest.
        // (DB-resolvable user-filter criteria are already baked into
        // `candidates` via HydrateFromIds → for those, Match is a no-op.)
        IEnumerable<ObservedPlayer> userFiltered = candidates;
        if (compiled is not null && evalCtx is not null)
            userFiltered = userFiltered.Where(p => PlayerFilterEvaluator.Match(compiled, p, evalCtx));

        // Apply the system-filter preset. Skip the Recent preset's
        // OrderByDescending(LastSeen) when a DB sort is active — the
        // DB-supplied order is the truth then.
        IEnumerable<ObservedPlayer> query = mFilterIndex switch
        {
            0 => userFiltered.Where(p => mWatcher.CurrentlyVisible.Contains(p.ContentId)),
            1 when !IsDbSort => userFiltered.OrderByDescending(p => p.LastSeen).Take(mMaxRecent),
            1 => userFiltered.Take(mMaxRecent),
            3 => userFiltered.Where(p => mState.GetUnreadKinds(p.ContentId).Count > 0),
            _ => userFiltered,
        };

        if (!string.IsNullOrWhiteSpace(mSearch))
        {
            var needle = mSearch.Trim();
            query = query.Where(p => p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        // Final sort. DB-sort already gave us the order via the ordered
        // list; only in-memory sorts need an explicit ordering here. No
        // hard row cap — the clipper virtualizes render and DB sorts page
        // lazily via OFFSET/LIMIT as the user scrolls (see EnsureNextDbPage).
        if (!IsDbSort)
            query = ApplyInMemorySort(query);

        return query.ToList();
    }

    private IEnumerable<ObservedPlayer> HydrateFromIds(IEnumerable<ulong> ids)
    {
        foreach (var id in ids)
        {
            if (mWatcher.TryGetObserved(id, out var p) && p is not null)
                yield return p;
        }
    }

    private IEnumerable<ObservedPlayer> ApplyInMemorySort(IEnumerable<ObservedPlayer> source) => mSortField switch
    {
        PlayerListSortField.LastSeen => mSortDescending
            ? source.OrderByDescending(p => p.LastSeen)
            : source.OrderBy(p => p.LastSeen),
        PlayerListSortField.FirstSeen => mSortDescending
            ? source.OrderByDescending(p => p.FirstSeen)
            : source.OrderBy(p => p.FirstSeen),
        PlayerListSortField.Level => mSortDescending
            ? source.OrderByDescending(p => p.Level)
            : source.OrderBy(p => p.Level),
        _ => mSortDescending
            ? source.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            : source.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
    };

    /// <summary>Cached result of <see cref="IPlayerFilterDbQueryService.RunOrderedAsync"/>
    /// for the no-user-filter case (DB sort, no WHERE clause). The user-filter
    /// path stashes the equivalent ordered list on the CompiledFilter
    /// instead. Mutable + appended-to by the page-load follow-ups.</summary>
    private List<ulong>? mUnfilteredOrderedContentIds;
    private long mUnfilteredOrderedDataVersion = -1;
    private string? mUnfilteredOrderedColumn;
    private bool mUnfilteredOrderedDescending;
    private Task? mUnfilteredOrderedTask;
    /// <summary>True once a follow-up page returned fewer rows than its
    /// requested LIMIT — no more OFFSETs will produce results. Reset to
    /// false whenever the cache is invalidated (sort/revision change).</summary>
    private bool mUnfilteredOrderedEndOfStream;
    /// <summary>Tracks the in-flight page-load (NOT the initial query — that's
    /// <see cref="mUnfilteredOrderedTask"/>). Set so multiple Scroll-near-end
    /// frames don't fan out duplicate OFFSET queries.</summary>
    private Task? mUnfilteredNextPageTask;

    private void EnsureCompiled(PlayerFilter source)
    {
        var revision = PlayerFilterEvaluator.ComputeSourceRevision(source);
        var filterChanged = mCompiled is null
            || mCompiled.SourceId != source.Id
            || mCompiled.SourceRevision != revision;
        if (filterChanged)
        {
            mCompiled = PlayerFilterEvaluator.Compile(source, mCategoryResolver);
            mDbQueryTask = null;
        }
        EnsureDbQuery(mCompiled!, filterChanged);
        EnsureUnfilteredOrderedQuery(filterChanged);
    }

    private void EnsureDbQuery(CompiledFilter compiled, bool filterChanged)
    {
        var needsUnordered = compiled.RequiresDbQuery && !IsDbSort;
        var needsOrdered = (compiled.RequiresDbQuery && IsDbSort)
                           || (compiled.RequiresDbQuery && IsDbSort);
        // The two needs* booleans above are intentionally identical for now:
        // a user filter with DB criteria always runs through this path, and
        // a DB sort fold the ORDER BY into the same query. The split exists
        // so the variable names document intent.
        if (!needsUnordered && !needsOrdered) return;

        var watcherRevision = mWatcher.Revision;
        var activeSortColumn = IsDbSort ? DbSortColumn : null;

        // Cache-hit predicates: same data version + same sort key.
        if (!filterChanged)
        {
            if (IsDbSort)
            {
                if (compiled.DbOrderedContentIds is not null
                    && compiled.CachedSortColumn == activeSortColumn
                    && compiled.CachedSortDescending == mSortDescending
                    && compiled.DbDataVersion == watcherRevision)
                    return;
            }
            else
            {
                if (compiled.DbAllowedContentIds is not null
                    && compiled.CachedSortColumn is null
                    && compiled.DbDataVersion == watcherRevision)
                    return;
            }

            // Debounce revision-bump-only re-queries. Filter-change always
            // bypasses (handled by the filterChanged guard above): the user
            // expects an immediate refresh, observation ticks don't.
            if (DateTime.UtcNow < mNextAllowedDbQueryAt) return;
        }

        if (mDbQueryTask is not null) return;

        var captured = compiled;
        var sql = compiled.SqlWhere!;
        var parameters = compiled.SqlParameters ?? Array.Empty<object>();
        mDbQueryTask = Task.Run(async () =>
        {
            if (IsDbSort)
            {
                var descending = mSortDescending;
                var ordered = await mDbQuery.RunOrderedAsync(
                    sql, parameters, activeSortColumn!, descending,
                    limit: DbPageSize, offset: 0).ConfigureAwait(false);
                if (ReferenceEquals(mCompiled, captured))
                {
                    // Wrap in a fresh List so the page-load follow-ups can
                    // append rather than build a new list every page.
                    captured.DbOrderedContentIds = new List<ulong>(ordered);
                    captured.DbOrderedEndOfStream = ordered.Count < DbPageSize;
                    captured.DbAllowedContentIds = null;
                    captured.CachedSortColumn = activeSortColumn;
                    captured.CachedSortDescending = descending;
                    captured.DbDataVersion = watcherRevision;
                }
            }
            else
            {
                var set = await mDbQuery.RunAsync(sql, parameters).ConfigureAwait(false);
                if (ReferenceEquals(mCompiled, captured))
                {
                    captured.DbAllowedContentIds = set;
                    captured.DbOrderedContentIds = null;
                    captured.DbOrderedEndOfStream = false;
                    captured.CachedSortColumn = null;
                    captured.DbDataVersion = watcherRevision;
                }
            }
            mNextAllowedDbQueryAt = DateTime.UtcNow + DbQuerySettle;
            mDbQueryTask = null;
        });
    }

    /// <summary>Fetches the next OFFSET/LIMIT page for a DB-ordered list
    /// when the user has scrolled near the end of what's loaded. Operates
    /// on either the user-filtered CompiledFilter or the unfiltered sort
    /// cache, whichever is active. No-op when end-of-stream has already
    /// been signalled or a page-load is already in flight.</summary>
    private void EnsureNextDbPage(int loadedCount)
    {
        // User-filter path: CompiledFilter owns the list + EOS flag. Use
        // the cached direction the first page was loaded with so a
        // mid-stream direction change doesn't interleave ASC/DESC pages
        // into the same list — the next EnsureDbQuery pass will detect
        // the direction mismatch and rebuild from offset 0.
        if (mCompiled is { RequiresDbQuery: true, DbOrderedContentIds: { } list } captured
            && !captured.DbOrderedEndOfStream
            && captured.CachedSortColumn is { } sortColumn
            && mDbQueryTask is null)
        {
            var sql = captured.SqlWhere!;
            var parameters = captured.SqlParameters ?? Array.Empty<object>();
            var offset = list.Count;
            var descending = captured.CachedSortDescending;
            mDbQueryTask = Task.Run(async () =>
            {
                var page = await mDbQuery.RunOrderedAsync(
                    sql, parameters, sortColumn, descending,
                    limit: DbPageSize, offset: offset).ConfigureAwait(false);
                if (ReferenceEquals(mCompiled, captured))
                {
                    captured.DbOrderedContentIds!.AddRange(page);
                    if (page.Count < DbPageSize)
                        captured.DbOrderedEndOfStream = true;
                }
                mDbQueryTask = null;
            });
            return;
        }

        // Unfiltered DB-sort path: panel owns the list + EOS flag.
        if (IsDbSort
            && mUnfilteredOrderedContentIds is { } unfiltered
            && !mUnfilteredOrderedEndOfStream
            && mUnfilteredOrderedColumn is { } column
            && mUnfilteredNextPageTask is null)
        {
            var offset = unfiltered.Count;
            var descending = mUnfilteredOrderedDescending;
            mUnfilteredNextPageTask = Task.Run(async () =>
            {
                var page = await mDbQuery.RunOrderedAsync(
                    sqlWhere: null, parameters: null,
                    sortColumn: column, descending: descending,
                    limit: DbPageSize, offset: offset).ConfigureAwait(false);
                // Capture-by-reference: if the user changed sort mid-flight,
                // mUnfilteredOrderedContentIds may have been reset. Only
                // append when our captured list reference matches.
                if (ReferenceEquals(mUnfilteredOrderedContentIds, unfiltered))
                {
                    unfiltered.AddRange(page);
                    if (page.Count < DbPageSize)
                        mUnfilteredOrderedEndOfStream = true;
                }
                mUnfilteredNextPageTask = null;
            });
        }
    }

    /// <summary>Sort-only path: a DB-resolved sort is active but no user
    /// filter narrows the candidate set. We still need the view to provide
    /// the ordered ContentIds; cache the result alongside the watcher
    /// revision so observation ticks within the debounce window don't
    /// re-fire the query.</summary>
    private void EnsureUnfilteredOrderedQuery(bool filterChanged)
    {
        // Only relevant when there's NO active user-filter with DB criteria —
        // that case is handled by EnsureDbQuery's ordered branch above. We
        // gate on mActiveUserFilterId rather than the (possibly stale)
        // mCompiled so that clearing the filter immediately flips to the
        // unfiltered-sort cache.
        var hasActiveDbFilter = mActiveUserFilterId is not null
            && mCompiled is { RequiresDbQuery: true };
        var needsUnfilteredSort = IsDbSort && !hasActiveDbFilter;

        if (!needsUnfilteredSort)
        {
            mUnfilteredOrderedContentIds = null;
            mUnfilteredOrderedColumn = null;
            mUnfilteredOrderedDataVersion = -1;
            mUnfilteredOrderedEndOfStream = false;
            return;
        }

        var watcherRevision = mWatcher.Revision;
        var column = DbSortColumn;
        var descending = mSortDescending;

        // Cache hit? Column AND direction must match — flipping direction
        // means re-querying from offset 0 (a partially-loaded ASC page
        // can't be reused as the head of a DESC stream).
        if (!filterChanged
            && mUnfilteredOrderedContentIds is not null
            && mUnfilteredOrderedColumn == column
            && mUnfilteredOrderedDescending == descending
            && mUnfilteredOrderedDataVersion == watcherRevision)
            return;

        if (!filterChanged && DateTime.UtcNow < mNextAllowedDbQueryAt) return;
        if (mUnfilteredOrderedTask is not null) return;

        mUnfilteredOrderedTask = Task.Run(async () =>
        {
            var ordered = await mDbQuery.RunOrderedAsync(
                sqlWhere: null, parameters: null,
                sortColumn: column, descending: descending,
                limit: DbPageSize, offset: 0).ConfigureAwait(false);
            mUnfilteredOrderedContentIds = new List<ulong>(ordered);
            mUnfilteredOrderedColumn = column;
            mUnfilteredOrderedDescending = descending;
            mUnfilteredOrderedDataVersion = watcherRevision;
            mUnfilteredOrderedEndOfStream = ordered.Count < DbPageSize;
            mUnfilteredNextPageTask = null;
            mNextAllowedDbQueryAt = DateTime.UtcNow + DbQuerySettle;
            mUnfilteredOrderedTask = null;
        });
    }

    private void DrawRow(ObservedPlayer player)
    {
        var isSelected = mState.SelectedObserved?.ContentId == player.ContentId;
        var clicked = ImGui.Selectable($"##row_{player.ContentId}",
            isSelected, ImGuiSelectableFlags.None, new Vector2(0, RowHeight - 4f));

        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();

        draw.AddText(min + new Vector2(6, 4),
            ImGui.GetColorU32(ImGuiCol.Text),
            player.Name);

        var rectW = ImGui.GetItemRectSize().X;
        var rightX = rectW - 6;

        // History dot — yellow when the player has UNREAD recorded changes.
        // Drawn first so the "no lodestone id" hint dot sits left of it when
        // both would otherwise occupy the same spot. The dot is painted onto
        // the DrawList directly (not an ImGui item), so hover-tooltip dispatch
        // goes through IsMouseHoveringRect over the painted rect — and so does
        // the click that jumps straight into the History tab.
        var unreadKinds = mState.GetUnreadKinds(player.ContentId);
        var dotClickedThisFrame = false;
        if (unreadKinds.Count > 0)
        {
            const string histDot = "●";
            var size = ImGui.CalcTextSize(histDot);
            var topLeft = min + new Vector2(rightX - size.X, 4);
            draw.AddText(topLeft,
                ImGui.GetColorU32(ImGuiColors.DalamudYellow),
                histDot);
            if (ImGui.IsMouseHoveringRect(topLeft, topLeft + size))
            {
                var labels = string.Join(", ", unreadKinds.Select(k => HistoryFormatting.KindLabel(k, mLoc)));
                ImGui.SetTooltip(string.Format(
                    mLoc.Get("ui.main.list.history_dot.tooltip"), labels));
                // The row's Selectable swallows the click for selection toggle
                // — we still see the "clicked" flag this frame, so we reroute
                // it: select the player AND request the History tab.
                if (clicked) dotClickedThisFrame = true;
            }
            rightX -= size.X + 4;
        }

        if (player.LodestoneId is null)
        {
            // Same glyph as the yellow history dot so the two render at identical
            // size — the previous middle-dot (U+00B7) was noticeably smaller and
            // the visual weight mismatch made the grey hint look like rendering
            // noise rather than a status indicator.
            const string hint = "●";
            var size = ImGui.CalcTextSize(hint);
            var topLeft = min + new Vector2(rightX - size.X, 4);
            draw.AddText(topLeft,
                ImGui.GetColorU32(ImGuiColors.DalamudGrey3),
                hint);
            if (ImGui.IsMouseHoveringRect(topLeft, topLeft + size))
                ImGui.SetTooltip(mLoc.Get("ui.main.list.lodestone_dot.tooltip"));
        }

        // Click dispatch lives after the dot-hit-test so a click on the yellow
        // dot becomes "select + jump to History tab" even when the row was
        // already selected (otherwise the Selectable's toggle would deselect).
        if (clicked)
        {
            if (dotClickedThisFrame)
            {
                if (!isSelected) mState.Select(player);
                mState.RequestTabActivation(MainWindowState.TabHistory);
            }
            else if (isSelected) mState.Deselect();
            else mState.Select(player);
        }
    }

    private void DrawEmptyState()
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        var msg = mFilterIndex switch
        {
            // A user filter narrowing the result to zero takes precedence
            // over the system-scope empty messages — that's what the user
            // just tweaked, so the explanation should match.
            _ when mActiveUserFilterId is not null =>
                mLoc.Get("ui.main.list.empty.user_filter"),
            0 => mLoc.Get("ui.main.list.empty.current"),
            1 => mLoc.Get("ui.main.list.empty.recent"),
            3 => mLoc.Get("ui.main.list.empty.unread"),
            _ when !string.IsNullOrWhiteSpace(mSearch) =>
                string.Format(mLoc.Get("ui.main.list.empty.search"), mSearch.Trim()),
            _ => mLoc.Get("ui.main.list.empty.default"),
        };
        ImGui.TextColored(ImGuiColors.DalamudGrey, msg);
        ImGui.PopTextWrapPos();
    }
}
