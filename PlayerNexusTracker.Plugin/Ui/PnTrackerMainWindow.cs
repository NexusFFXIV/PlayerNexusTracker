using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using NexusKit.Core.Localization;
using NexusKit.Core.Utilities;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Modules.PluginBridge.Adapters.Lifestream;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.Abstractions;
using NexusKit.Ui.Imaging;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Settings.Filters;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Ui;

/// <summary>
/// The plugin's main window. Hosts the player list (left) and detail panel
/// (right) split layout, plus the title-bar debug-window quick-launch.
/// Owns the no-selection / with-selection width persistence: the user can
/// resize the window separately in each mode, the values land in the
/// settings store under <c>ui.pntracker.main.width.no_selection</c> and
/// <c>ui.pntracker.main.width.with_selection</c>, and the layout keeps the
/// list pane's visible width identical across the two modes.
/// </summary>
public sealed class PnTrackerMainWindow : MainWindow
{
    private const float DefaultNarrowWidth = 280f;
    private const float DefaultWideWidth = 900f;
    private const float DefaultHeight = 560f;
    private const float MinSaneWidth = 200f;

    private const string KeyWidthNoSelection = "ui.pntracker.main.width.no_selection";
    private const string KeyWidthWithSelection = "ui.pntracker.main.width.with_selection";

    private readonly ISettingsStore mStore;
    private readonly MainWindowState mState;
    private readonly PlayerListPanel mListPanel;
    private readonly PlayerDetailPanel mDetailPanel;

    private float mNarrowWidth;
    private float mWideWidth;
    private bool? mLastHadSelection;

    public PnTrackerMainWindow(
        ISettingsStore store,
        IWindowManager windows,
        IInternalDataPlayerWatcher watcher,
        IExternalDataPlayerService players,
        IExternalDataFreeCompanyService freeCompanies,
        IInternalDataHistoryService history,
        IInternalDataEncounterTracker encounters,
        IExternalDataAchievementCatalog achievements,
        IExternalDataMountCatalog mounts,
        IExternalDataMinionCatalog minions,
        IPlayerRefreshQueueService refreshQueue,
        IRefreshCategoryPolicy refreshPolicy,
        IGameDataLookups lookups,
        IImageCache images,
        IBrowserLauncher browser,
        ILocalizer localizer,
        PlayerFilterRegistry filterRegistry,
        IPlayerFilterDbQueryService filterDbQuery,
        ILifestreamAdapter lifestream,
        NexusKit.Core.ILocalPlayerContext localPlayer,
        PlayerNexusTracker.Ui.Main.Tabs.EncountersFilterPreferences encountersFilters,
        PlayerNexusTracker.Settings.Filters.EncounterCategoryResolver categoryResolver,
        Microsoft.Extensions.Logging.ILogger<MainWindowState> stateLog)
        : base(
            "PlayerNexusTracker###PNT_Main",
            store,
            windows,
            restoreOpenState: true,
            showSettingsButton: true,
            localizer: localizer)
    {
        mStore = store;
        mNarrowWidth = LoadWidth(KeyWidthNoSelection, DefaultNarrowWidth);
        mWideWidth = LoadWidth(KeyWidthWithSelection, DefaultWideWidth);
        // Detail pane needs at least MinSaneWidth of breathing room when both are
        // visible — otherwise the list could eat the whole window. Reset to
        // defaults if the persisted pair would leave too little space.
        if (mNarrowWidth + MinSaneWidth >= mWideWidth)
        {
            mNarrowWidth = DefaultNarrowWidth;
            mWideWidth = DefaultWideWidth;
        }

        Size = new Vector2(mNarrowWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;

        mState = new MainWindowState(players, freeCompanies, history, watcher, encounters, achievements, mounts, minions, refreshQueue, stateLog);
        mListPanel = new PlayerListPanel(watcher, mState, lookups, store, localizer, filterRegistry, filterDbQuery, categoryResolver);
        mDetailPanel = new PlayerDetailPanel(mState, lookups, images, browser, refreshPolicy, localizer, lifestream, localPlayer, encountersFilters);

#if DEBUG

        // Title-bar quick-launch for the debug window — sits alongside the settings cog.
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            IconOffset = new Vector2(2, 1),
            Click = btn => { if (btn == ImGuiMouseButton.Left) windows.Open<DebugWindow>(); },
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Open debug window");
                ImGui.EndTooltip();
            },
        });

#endif
    }

    public override void Draw()
    {
        var hasSelection = mState.SelectedObserved is not null;
        var size = ImGui.GetWindowSize();

        if (mLastHadSelection != hasSelection)
        {
            if (mLastHadSelection is { } prev && size.X >= MinSaneWidth)
            {
                if (prev) mWideWidth = size.X;
                else mNarrowWidth = size.X;
                _ = mStore.SetAsync(
                    prev ? KeyWidthWithSelection : KeyWidthNoSelection,
                    prev ? mWideWidth : mNarrowWidth);
            }

            var newWidth = hasSelection ? mWideWidth : mNarrowWidth;
            ImGui.SetWindowSize(new Vector2(newWidth, size.Y));
            mLastHadSelection = hasSelection;
        }
        else if (size.X >= MinSaneWidth)
        {
            if (hasSelection) mWideWidth = size.X;
            else mNarrowWidth = size.X;
        }

        if (hasSelection)
        {
            // The list pane keeps its visible width identical to no-selection
            // mode. mNarrowWidth is the persisted *window* width; the actual
            // drawing area inside the window is reduced by WindowPadding on
            // each side. The split's left child draws straight into that
            // padded region with no further inset of its own, so we have to
            // subtract 2×WindowPadding here to match what the user sees when
            // no selection is active. Also clamp so the detail pane always
            // keeps at least MinSaneWidth of breathing room.
            var avail = ImGui.GetContentRegionAvail().X;
            var padding = ImGui.GetStyle().WindowPadding.X;
            var target = mNarrowWidth - 2f * padding;
            var listWidth = MathF.Max(MinSaneWidth,
                                      MathF.Min(target, avail - MinSaneWidth));
            NexusSplitLayout.Draw(
                id: "##pnt_main_split",
                leftWidth: listWidth,
                drawLeft: mListPanel.Draw,
                drawRight: mDetailPanel.Draw,
                rightScrolls: false);
        }
        else
        {
            mListPanel.Draw();
        }
    }

    public override void Dispose()
    {
        if (mLastHadSelection is { } had)
        {
            try
            {
                mStore.SetAsync(
                    had ? KeyWidthWithSelection : KeyWidthNoSelection,
                    had ? mWideWidth : mNarrowWidth).GetAwaiter().GetResult();
            }
            catch { /* best-effort on shutdown */ }
        }
        mState.Dispose();
        base.Dispose();
    }

    private float LoadWidth(string key, float fallback)
    {
        try
        {
            var v = mStore.GetAsync<float>(key).GetAwaiter().GetResult();
            return v >= MinSaneWidth ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}