using Microsoft.Extensions.DependencyInjection;
using NexusKit.ChatNotifications;
using NexusKit.Core;
using NexusKit.Core.Localization;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Modules.PluginBridge;
using NexusKit.Persistence;
using NexusKit.Ui;
using NexusKit.Ui.AutoSettings;
using PlayerNexusTracker.Notifications;
using PlayerNexusTracker.Resources;
using PlayerNexusTracker.Settings;
using PlayerNexusTracker.Settings.Filters;
using PlayerNexusTracker.Ui.Main.Tabs;
using PlayerNexusTracker.Ui.Settings;

namespace PlayerNexusTracker.Composition;

public static class PluginServiceCollectionExtensions
{
    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        // Adapter that exposes Dalamud's IClientState as the framework's
        // ISessionStateProvider. NexusKit.Hosting auto-resolves the bridge
        // on host build and wires Login/Logout into PluginLifetime — no
        // per-plugin glue code required beyond this single registration.
        services.AddSingleton<ISessionStateProvider, DalamudSessionStateProvider>();

        // Localization: the Language.resx-backed source is registered up front so any
        // schema property that later switches to .LabelKey/.DescriptionKey starts
        // resolving immediately. The .resx is empty for now; add keys via VS.
        services.AddResourceLocalizer<Language>();

        services.AddSettings<TrackerSettings>(b => b
            .StoredAs("config")
            .Group("Tracker", order: 10)
            .Property(x => x.MaxRecentPlayers, p => p
                .Label("Max recent players")
                .Description("How many entries the 'Recent players' list shows (most-recent first).")
                .NumericInput()
                .Order(1))
            .Property(x => x.RefreshTtlDays, p => p
                .Label("Refresh after (days)")
                .Description("How long cached Lodestone/FFXIVCollect data stays fresh before the background queue re-fetches it. Per sub-resource (profile, class jobs, gear, FC, mounts, minions, achievements).")
                .NumericInput()
                .Order(2)));

        // TTL provider for the refresh queue — registered BEFORE AddNexusKitPlayerEnrichment
        // so its TryAddSingleton fallback skips the default 7-day provider.
        services.AddSingleton<IRefreshTtlProvider, SettingsRefreshTtlProvider>();

        // Single composition entry — pulls InternalData + ExternalData and the
        // bridging services (LodestoneIdResolver, PlayerRefreshQueueService).
        services.AddNexusKitPlayerEnrichment();

        // Foreign-plugin integration: probes Lifestream (and future adapters)
        // via Dalamud's InstalledPlugins, exposes normalized adapter services
        // (ILifestreamAdapter, …) that the UI code injects. The settings
        // section below renders the per-adapter status.
        services.AddNexusKitPluginBridge();
        services.AddSingleton<IAutoSettingsSection, PluginBridgesSettingsSection>();

        // Generic chat-notification framework: registry + Notifications
        // settings tab. Producers (below) register kinds against it at
        // resolve time.
        services.AddNexusKitChatNotifications();

        // Cross-producer change-signal bus. Producers (history,
        // collection-growth, future ones) push to IPlayerChangeSignal when
        // they detect a change; the aggregator debounces by contentId and
        // re-emits a single Aggregated event that the general-change
        // producer turns into a single chat line per coalesced burst.
        services.AddSingleton<PlayerChangeAggregator>();
        services.AddSingleton<IPlayerChangeSignal>(sp => sp.GetRequiredService<PlayerChangeAggregator>());

        // Notification producers. Each subscribes to its event source in
        // its constructor and registers its kind with the framework — the
        // plugin eagerly resolves every INotificationProducer at LoadAsync
        // so subscriptions are live before any in-game events fire.
        //
        // Registration order matters for the settings UI: the first kind in
        // each group fixes the group's spot in the layout. The
        // GeneralChangeNotificationProducer is registered first so its
        // "General" group leads the section; granular per-kind producers
        // follow in their respective groups.
        services.AddSingleton<GeneralChangeNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<GeneralChangeNotificationProducer>());
        // FC catch-all lives in the General group right next to the player
        // catch-all so the two roll-up rows sit together at the top of the
        // settings panel. Subscribes to both FreeCompanyAdded and
        // FreeCompanyChanged and is default-on; the granular FC-Added kind
        // in the fc_history group declares SuppressedBy on this id so the
        // UI greys whichever isn't active.
        services.AddSingleton<FreeCompanyChangedNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<FreeCompanyChangedNotificationProducer>());
        services.AddSingleton<EnrichmentResolvedNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<EnrichmentResolvedNotificationProducer>());
        services.AddSingleton<HistoryNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<HistoryNotificationProducer>());
        services.AddSingleton<RefreshFailureNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<RefreshFailureNotificationProducer>());
        services.AddSingleton<CollectionGrowthNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<CollectionGrowthNotificationProducer>());
        // FC-history group: granular per-kind rows. Today only Added lives
        // here; future FC-lifecycle producers slot in alongside it.
        services.AddSingleton<FreeCompanyAddedNotificationProducer>();
        services.AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<FreeCompanyAddedNotificationProducer>());

        // User-defined player-list filters. Both the editor (settings tab)
        // and the consumer (PlayerListPanel) read from the same registry
        // singleton, so edits propagate without a plugin reload.
        services.AddSingleton<PlayerFilterRegistry>();
        services.AddSingleton<IAutoSettingsSection, PlayerFilterSettingsSection>();
        // DB query service for the SQL-view-driven half of the filter pipeline
        // (D3). Compiled filters with DB-resolvable criteria run a single
        // SELECT against nexus_filter_player at activation; the panel feeds
        // the resulting HashSet<ContentId> into ApplyFilters as a pre-narrow.
        services.AddSingleton<IPlayerFilterDbQueryService, PlayerFilterDbQueryService>();

        // DB-maintenance settings section is a framework-provided
        // IAutoSettingsSection — lives in NexusKit.Ui so any plugin that
        // wires AddNexusKitPersistence + AddNexusKitUi can plug it in with
        // one call. Order 200 lands it after the filter section (50) and
        // module sections.
        services.AddDbMaintenanceSettingsSection(order: 200);
        // Editor preview: compiles a draft filter and counts matches against
        // mWatcher.Recent + DB pre-narrow. Stubbed volatile EvalContext fields
        // so the count is stable across observation ticks while the user
        // edits — see the caveat tooltip in PlayerFilterSettingsSection.
        services.AddSingleton<IPlayerFilterPreviewService, PlayerFilterPreviewService>();

        // Per-tab persisted UI preferences. Today only EncountersTab needs
        // this, but the singleton-per-tab pattern is the seam if other
        // static tabs grow their own filter state later.
        services.AddSingleton<EncountersFilterPreferences>();

        // Lumina-backed category map (TerritoryId ↔ EncounterZoneFilter).
        // Singleton because the sheets are immutable at runtime and the
        // resolver lazy-caches the per-category lists on first call. Used
        // by the player-filter compile path and the settings-tab zone
        // picker.
        services.AddSingleton<EncounterCategoryResolver>();

        return services;
    }
}