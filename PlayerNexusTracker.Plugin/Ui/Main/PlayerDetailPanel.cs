using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using NexusKit.Core.Localization;
using NexusKit.Core.Utilities;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Core;
using NexusKit.Modules.PluginBridge.Adapters.Lifestream;
using NexusKit.Ui.Imaging;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main.Tabs;

namespace PlayerNexusTracker.Ui.Main;

internal sealed class PlayerDetailPanel
{
    private const float AvatarSize = 64f;

    private readonly MainWindowState mState;
    private readonly IGameDataLookups mLookups;
    private readonly IImageCache mImages;
    private readonly IBrowserLauncher mBrowser;
    private readonly IRefreshCategoryPolicy mRefreshPolicy;
    private readonly ILocalizer mLoc;
    private readonly ILifestreamAdapter mLifestream;
    private readonly ILocalPlayerContext mLocalPlayer;
    private readonly EncountersFilterPreferences mEncountersFilters;
    private string? mLastResetKey;

    /// <summary>Per-(content_id, tab) marker so each "tab just opened" side effect
    /// (scroll reset, mark-as-read) fires exactly once per actual open transition
    /// instead of every frame the tab is active.</summary>
    private string? mLastHistoryReadKey;

    public PlayerDetailPanel(
        MainWindowState state,
        IGameDataLookups lookups,
        IImageCache images,
        IBrowserLauncher browser,
        IRefreshCategoryPolicy refreshPolicy,
        ILocalizer localizer,
        ILifestreamAdapter lifestream,
        ILocalPlayerContext localPlayer,
        EncountersFilterPreferences encountersFilters)
    {
        mState = state;
        mLookups = lookups;
        mImages = images;
        mBrowser = browser;
        mRefreshPolicy = refreshPolicy;
        mLoc = localizer;
        mLifestream = lifestream;
        mLocalPlayer = localPlayer;
        mEncountersFilters = encountersFilters;
    }

    public void Draw()
    {
        if (mState.SelectedObserved is not { } observed)
        {
            DrawEmptyState();
            return;
        }

        DrawHeader(observed, mState.CurrentPlayer);
        ImGui.Spacing();
        DrawTabs(mState.CurrentPlayer, observed);
    }

    private void DrawHeader(ObservedPlayer observed, Player? player)
    {
        var avatarUrl = player?.Profile?.AvatarUrl;
        var texture = !string.IsNullOrEmpty(avatarUrl) ? mImages.GetTexture(avatarUrl) : null;
        if (texture is not null)
        {
            ImGui.Image(texture.Handle, new Vector2(AvatarSize, AvatarSize));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mLoc.Get("ui.main.detail.avatar.tooltip"));
        }
        else
        {
            // Full Customize lives on the lazy-loaded detail; the slim
            // ObservedPlayer carries only race+gender. The placeholder only
            // cares about those two bytes anyway, so passing them directly
            // avoids waiting on the detail load just to colour the avatar.
            AvatarPlaceholder.Draw(observed.Race, observed.Gender, AvatarSize);
        }
        ImGui.SameLine();

        ImGui.BeginGroup();
        // Live observation is the source of truth for Name. The Lodestone
        // cache can lag behind a rename for up to a full TTL window; if the
        // two disagree, render observed and surface the divergence so the
        // user knows the cached profile is stale (a refresh queues silently).
        ImGui.TextUnformatted(observed.Name);
        if (player is not null
            && !string.IsNullOrEmpty(player.Name)
            && !string.Equals(player.Name, observed.Name, StringComparison.Ordinal))
        {
            NexusHint.Draw(FontAwesomeIcon.ExclamationTriangle,
                string.Format(mLoc.Get("ui.main.detail.stale_name"), player.Name));
        }
        HistoryHint.Draw(mState.CurrentHistory, PlayerHistoryKind.NameChange, mLoc);
        DrawGenderBadge(observed.Race, observed.Gender);
        DrawLodestoneStatusBadge(observed, player);

        DrawRaceAndJobLine(observed);
        HistoryHint.Draw(mState.CurrentHistory, PlayerHistoryKind.CustomizeChange, mLoc);

        // Same precedence rule for the world / data-center line: observed
        // HomeWorldId is the live truth from the object table, player.* comes
        // from the Lodestone cache and can be stale post-transfer.
        var liveWorld = mLookups.GetWorldName(observed.HomeWorldId) ?? observed.HomeWorld;
        var liveDc = player is not null
            ? mLookups.GetDataCenterName(player.DataCenterId)
            : null;
        // When a transfer hits, the cached profile's DataCenterId still
        // belongs to the old DC. Fall back to deriving the DC name from the
        // live home world id (same Lumina lookup the player service uses
        // server-side) so the line never advertises a stale DC.
        if (player is null
            || observed.HomeWorldId != player.HomeWorldId
            || string.IsNullOrEmpty(liveDc))
        {
            liveDc = mLookups.GetDataCenterNameByWorldId(observed.HomeWorldId);
        }
        var dc = liveDc is null ? liveWorld : $"{liveWorld} · {liveDc}";
        ImGui.TextColored(ImGuiColors.DalamudGrey, dc);
        if (player is not null
            && player.HomeWorldId != 0
            && observed.HomeWorldId != 0
            && observed.HomeWorldId != player.HomeWorldId)
        {
            var stalePart = mLookups.GetWorldName(player.HomeWorldId) ?? $"#{player.HomeWorldId}";
            NexusHint.Draw(FontAwesomeIcon.ExclamationTriangle,
                string.Format(mLoc.Get("ui.main.detail.stale_home_world"), stalePart));
        }
        HistoryHint.Draw(mState.CurrentHistory, PlayerHistoryKind.HomeWorldChange, mLoc);
        DrawRefreshStatusLine();
        ImGui.EndGroup();

        DrawToolbar(observed);

        ImGui.Separator();
    }

    private void DrawToolbar(ObservedPlayer observed)
    {
        var hasLodestoneId = observed.LodestoneId is not null;
        var slots = new List<NexusIconToolbar.Slot>(4);

        // Spinner replaces the refresh button while a fetch is in flight; it's
        // narrower (20px) than a standard slot — the toolbar's width math reads
        // that off the Slot and keeps everything right-aligned regardless.
        slots.Add(mState.IsRefreshing
            ? NexusIconToolbar.Slot.Custom(20f, () => NexusLoadingSpinner.Draw(20f))
            : NexusIconToolbar.Slot.Button(FontAwesomeIcon.SyncAlt,
                mLoc.Get("ui.main.detail.toolbar.refresh"),
                () => mState.Refresh(),
                enabled: hasLodestoneId));

        slots.Add(NexusIconToolbar.Slot.Button(FontAwesomeIcon.ExternalLinkAlt,
            mLoc.Get("ui.main.detail.toolbar.open_lodestone"),
            () => mBrowser.OpenUrl($"https://eu.finalfantasyxiv.com/lodestone/character/{observed.LodestoneId}/"),
            enabled: hasLodestoneId));

#if DEBUG
        slots.Add(NexusIconToolbar.Slot.Button(FontAwesomeIcon.Copy,
            mLoc.Get("ui.main.detail.toolbar.copy_id"),
            () => ImGui.SetClipboardText(observed.LodestoneId!.Value.ToString()),
            enabled: hasLodestoneId));
#endif

        // Always enabled — works when the list is empty or filtered down so
        // a stale selection can always be cleared.
        slots.Add(NexusIconToolbar.Slot.Button(FontAwesomeIcon.Times,
            mLoc.Get("ui.main.detail.toolbar.close"),
            () => mState.Deselect()));

        NexusIconToolbar.DrawRightAligned(slots);
    }

    private void DrawRaceAndJobLine(ObservedPlayer observed)
    {
        // Race byte 0 is the slim-projection sentinel for "we never captured a
        // customize snapshot for this row" — skip the lookup so we don't end
        // up labelling such rows as the Lumina default race.
        var raceName = observed.Race != 0
            ? mLookups.GetRaceName(observed.Race, observed.Gender == 1)
            : null;
        var jobName = mLookups.GetClassJobName(observed.ClassJobId);
        var jobPart = !string.IsNullOrEmpty(jobName) && observed.Level > 0
            ? string.Format(mLoc.Get("ui.main.observation.active_job_value"),
                            jobName, observed.Level)
            : null;
        // Job tinted by role color (matches the Encounters / ClassJobs tabs);
        // race stays grey because it has no role-equivalent classification.
        var jobColor = mLookups.GetClassJobRole(observed.ClassJobId).ToRoleColor()
                       ?? ImGuiColors.DalamudGrey;

        if (!string.IsNullOrEmpty(raceName))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, raceName);
            if (jobPart is not null)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, "·");
                ImGui.SameLine();
                ImGui.TextColored(jobColor, jobPart);
            }
        }
        else if (jobPart is not null)
        {
            ImGui.TextColored(jobColor, jobPart);
        }
    }

    private void DrawGenderBadge(byte race, byte gender)
    {
        // Skip the badge when we've never captured a customize snapshot for
        // this row — gender 0 / 1 both have valid meanings, so we use the
        // race=0 sentinel that the slim ObservedPlayer carries instead.
        if (race == 0) return;

        var feminine = gender == 1;
        var icon = feminine ? FontAwesomeIcon.Venus : FontAwesomeIcon.Mars;
        var color = feminine
            ? new Vector4(1.00f, 0.55f, 0.75f, 1f)
            : new Vector4(0.45f, 0.65f, 1.00f, 1f);
        var tooltip = mLoc.Get(feminine ? "ui.main.gender.female" : "ui.main.gender.male");

        NexusHint.Draw(icon, tooltip, color);
    }

    /// <summary>Small hourglass next to the player name while enrichment hasn't
    /// landed. Replaces the old full-width banner inside the body view — the
    /// tabs now always render, and this badge is the only "still loading" cue.</summary>
    private void DrawLodestoneStatusBadge(ObservedPlayer observed, Player? player)
    {
        if (player is not null) return;
        var tooltipKey = observed.LodestoneId is null
            ? "ui.main.observation.banner_pending"
            : "ui.main.observation.banner_loading";
        NexusHint.Draw(FontAwesomeIcon.Hourglass, mLoc.Get(tooltipKey));
    }

    /// <summary>Grey one-liner under the data-center text: "Updated 3h ago · queued behind 5 (next: Mounts)".
    /// Only the parts that have meaningful data render. The "queued behind" piece is the
    /// number of OTHER characters' rows ahead of the selected one in the worker pick order
    /// (zero = next up); the category in parens names which sub-resource of the selected
    /// player is up next. When the queue has nothing for this character at all, the segment
    /// is dropped. Drives the user's intuition for how soon a fetch will land.</summary>
    private void DrawRefreshStatusLine()
    {
        var parts = new List<string>(2);

        if (mState.CurrentLastRefreshedAt is { } at)
            parts.Add(string.Format(mLoc.Get("ui.main.detail.refresh.updated_ago"), mLoc.FormatRelativeTimeAgo(at)));

        var queue = mState.CurrentQueueStatus;
        if (queue.PendingForContentId > 0)
        {
            var catName = mLoc.Get("ui.main.detail.refresh.catname."
                + (queue.NextCategory?.ToString().ToLowerInvariant() ?? "profile"));
            parts.Add(queue.RowsAhead == 0
                ? string.Format(mLoc.Get("ui.main.detail.refresh.queued_next"), catName)
                : string.Format(mLoc.Get("ui.main.detail.refresh.queued_behind"), queue.RowsAhead, catName));
        }

        if (parts.Count == 0) return;
        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Join(" · ", parts));
        if (mState.CurrentRefreshBreakdown is { } b && ImGui.IsItemHovered())
            DrawRefreshBreakdownTooltip(b);
    }

    /// <summary>Hover tooltip for the "Updated Xh ago" line. Lists each Lodestone
    /// sub-resource with its individual last-refresh timestamp so the user can see
    /// which category is fresh vs. stale instead of just the maximum. "—" for
    /// categories that have no row yet (never fetched). Rows for categories the
    /// user has disabled in settings are dropped — they'd be permanently "—" and
    /// just take up tooltip space.</summary>
    private void DrawRefreshBreakdownTooltip(NexusKit.Modules.ExternalData.Models.PlayerRefreshBreakdown b)
    {
        ImGui.BeginTooltip();
        DrawCatRow(RefreshCategory.Profile, "profile", b.ProfileAt);
        DrawCatRow(RefreshCategory.ClassJobs, "classjobs", b.ClassJobsAt);
        DrawCatRow(RefreshCategory.Gear, "gear", b.GearAt);
        DrawCatRow(RefreshCategory.FreeCompany, "freecompany", b.FreeCompanyAt);
        DrawCatRow(RefreshCategory.Mounts, "mounts", b.MountsAt);
        DrawCatRow(RefreshCategory.Minions, "minions", b.MinionsAt);
        DrawCatRow(RefreshCategory.Achievements, "achievements", b.AchievementsAt);
        ImGui.EndTooltip();

        void DrawCatRow(RefreshCategory cat, string keySuffix, DateTime? at)
        {
            if (!mRefreshPolicy.IsEnabled(cat)) return;
            DrawRow(mLoc.Get("ui.main.detail.refresh.cat." + keySuffix), at);
        }

        void DrawRow(string label, DateTime? at)
        {
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
            // Push value to a fixed column so the tooltip reads like a table even
            // though labels have different widths across languages. 140px covers
            // German ("Errungenschaften:") with margin to spare.
            var col = ImGui.GetCursorPosX() < 140f ? 140f : ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(col);
            if (at is { } d)
                ImGui.TextColored(ImGuiColors.DalamudGrey, mLoc.FormatRelativeTime(d));
            else
                ImGui.TextColored(ImGuiColors.DalamudGrey3, "—");
        }
    }

    private void DrawTabs(Player? player, ObservedPlayer observed)
    {
        if (!ImGui.BeginTabBar("##player_tabs", ImGuiTabBarFlags.Reorderable))
            return;

        DrawTab(mLoc.Get("ui.main.tab.summary"), () => SummaryTab.Draw(
            player, observed, mLookups, mLoc,
            history: mState.CurrentHistory,
            mountTotal: mState.MountTotal,
            minionTotal: mState.MinionTotal,
            achievementTotal: mState.AchievementTotal,
            seenCount: mState.CurrentEncounterCount));
        DrawTab(mLoc.Get("ui.main.tab.classjobs"), () => ClassJobsTab.Draw(player, observed, mLookups, mLoc));
        DrawTab(mLoc.Get("ui.main.tab.equipment"), () => EquipmentTab.Draw(player, observed, mLookups, mLoc));
        DrawTab(mLoc.Get("ui.main.tab.freecompany"), () => FreeCompanyTab.Draw(
            player, observed, mState.CurrentFcCandidates, mLookups, mLoc, mLifestream, mLocalPlayer));
        DrawTab(mLoc.Get("ui.main.tab.achievements"), () => AchievementsTab.Draw(player, observed, mState.AchievementCatalog, mLoc));
        var historyFlags = mState.ConsumePendingTab(MainWindowState.TabHistory)
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;
        DrawTab(mLoc.Get("ui.main.tab.history"), () =>
        {
            HistoryTab.Draw(player, mState.CurrentHistory, mState.ResolveFcLabel, mLoc);
            // First frame this tab is open for this character → bulk-mark read.
            // The service is a no-op when nothing's unread; HistoryRead fires
            // back through MainWindowState to drop the dot.
            var key = $"history|{observed.ContentId}";
            if (mLastHistoryReadKey != key)
            {
                mLastHistoryReadKey = key;
                _ = mState.MarkHistoryReadAsync(observed.ContentId);
            }
        }, historyFlags);
        DrawTab(mLoc.Get("ui.main.tab.encounters"), () => EncountersTab.Draw(
            mState.CurrentEncounters,
            mLocalPlayer.GetLocation()?.WorldId,
            mLookups, mLoc, mEncountersFilters));
        DrawTab(mLoc.Get("ui.main.tab.notes"), () => NotesTab.Draw(observed, mState.CurrentDetail, mState, mLoc));

        ImGui.EndTabBar();
    }

    private void DrawTab(string label, Action draw, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        if (!ImGui.BeginTabItem(label, flags)) return;
        try
        {
            ImGui.BeginChild($"##tab_content_{label}", new Vector2(0, 0), false);

            var key = $"tab|{mState.SelectedObserved?.ContentId}|{label}";
            if (mLastResetKey != key)
            {
                ImGui.SetScrollY(0f);
                mLastResetKey = key;
            }

            draw();
            ImGui.EndChild();
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    private void DrawEmptyState()
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.Dummy(new Vector2(0, avail.Y * 0.4f));
        var text = mLoc.Get("ui.main.detail.empty");
        var textW = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(MathF.Max(0, (avail.X - textW) * 0.5f));
        ImGui.TextColored(ImGuiColors.DalamudGrey, text);
    }
}