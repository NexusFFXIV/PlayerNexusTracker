# Plugin Composition (PlayerNexusTracker.Plugin)

Line-by-line walkthrough of how the plugin assembles itself at startup, and
where to plug in new modules or services.

## Entry point

`PlayerNexusTracker.Plugin/Plugin.cs` implements `IAsyncDalamudPlugin`:

```csharp
public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IPluginLog               Log             { get; private set; } = null!;
    [PluginService] public static ICommandManager          CommandManager  { get; private set; } = null!;
    [PluginService] public static IClientState             ClientState     { get; private set; } = null!;
    [PluginService] public static IDataManager             DataManager     { get; private set; } = null!;
    [PluginService] public static IFramework               Framework       { get; private set; } = null!;
    [PluginService] public static IObjectTable             ObjectTable     { get; private set; } = null!;
    [PluginService] public static ICondition               Condition       { get; private set; } = null!;
    [PluginService] public static ITextureProvider         TextureProvider { get; private set; } = null!;
    [PluginService] public static IChatGui                 ChatGui         { get; private set; } = null!;

    private PluginHost host = null!;
    public IServiceProvider Services => host.Services;

    public async Task LoadAsync(CancellationToken cancellationToken) { … }
    public ValueTask DisposeAsync() => host.DisposeAsync();
}
```

`[PluginService]` properties are filled by Dalamud's reflection injection
**before** `LoadAsync` runs. They're statics by Dalamud convention (the
plugin instance isn't yet a useful target). Each one gets re-registered as
a DI singleton so framework services can constructor-inject them.

## `LoadAsync` walkthrough

```csharp
public async Task LoadAsync(CancellationToken cancellationToken)
{
    // 1. Plugin context
    var context = new PluginContext(
        PluginName: nameof(PlayerNexusTracker),
        ConfigDirectory: PluginInterface.GetPluginConfigDirectory(),
        PluginVersion: typeof(Plugin).Assembly.GetName().Version
            ?? new Version(0, 1, 0, 0));

    // 2. Build the host
    host = await new PluginHostBuilder()
        .WithContext(context)
        .WithLogSink(new DalamudPluginLogSink(Log))
        .WithModule(new PlayerNexusTrackerModule())
        .ConfigureServices(s =>
        {
            // Dalamud handles → DI singletons
            s.AddSingleton(PluginInterface);
            s.AddSingleton(CommandManager);
            s.AddSingleton(ClientState);
            s.AddSingleton(DataManager);
            s.AddSingleton(Framework);
            s.AddSingleton(ObjectTable);
            s.AddSingleton(Condition);
            s.AddSingleton(TextureProvider);
            s.AddSingleton(ChatGui);

            // Framework facilities
            s.AddNexusKitPersistence();   // DbContextFactory + maintenance loop + stats
            s.AddNexusKitSettings();      // settings entity + ISettingsStore
            s.AddNexusKitIpc();           // DalamudIpcRegistry as IIpcRegistry
            s.AddNexusKitUi();            // WindowSystem, PluginUiHost, sections,
                                          // utilities, Framework.resx localizer
            s.AddNexusKitGameData();      // Lumina sheets + lookups + resolver

            // Plugin's own windows
            s.AddMainWindow<PnTrackerMainWindow>();
            s.AddAutoSettingsWindow();    // framework-rendered settings UI
            s.AddWindow<DebugWindow>();   // extra NexusWindow
        })
        .BuildAsync(cancellationToken);

    // 3. Force-construct services with subscription side-effects.
    host.Services.GetRequiredService<PluginUiHost>();
    host.Services.GetRequiredService<IInternalDataPlayerWatcher>();
    host.Services.GetRequiredService<IInternalDataHistoryService>();
    host.Services.GetRequiredService<IInternalDataEncounterTracker>();
    host.Services.GetRequiredService<IPlayerRefreshQueueService>();
    host.Services.GetRequiredService<LiveTagChangeRefreshTrigger>();

    // 4. Eagerly resolve every notification producer — each ctor registers
    //    its kind with the chat-notification framework and subscribes to
    //    its event source. Iteration shape so adding a producer is one
    //    registration line in PluginServiceCollectionExtensions, no edit
    //    here.
    foreach (var _ in host.Services.GetServices<INotificationProducer>()) { }

    // 5. Log the loaded message via the framework's logging pipeline.
    var logger = host.Services.GetRequiredService<ILogger<Plugin>>();
    logger.LogInformation("PlayerNexusTracker loaded. Version={Version}", context.PluginVersion);
}
```

Steps 1–2 are the only Dalamud-specific code paths in the plugin. Steps 3–4
exist because DI singletons are lazy by default; the services they resolve
have constructors that subscribe to events / spin up worker threads, and
without the eager kick the first user interaction (instead of plugin load)
would be the point when subscriptions land.

## What `PlayerNexusTrackerModule` does

Plugin-specific composition lives in
`Composition/PluginServiceCollectionExtensions.cs` behind an `AddServices()`
extension. The module simply calls it:

```csharp
public sealed class PlayerNexusTrackerModule : IPluginModule
{
    public void Register(IServiceCollection services, IPluginContext context)
        => services.AddServices();
}
```

Why split? `PluginHostBuilder` accepts `IPluginModule` for "this is the
plugin's domain code"; `AddServices` is the actual implementation. The
indirection costs nothing and isolates the plugin's wiring inside
`PluginServiceCollectionExtensions` (a logical place to look for "what does
this plugin actually do?").

## What `AddServices()` does

```csharp
public static IServiceCollection AddServices(this IServiceCollection services)
{
    // 1. Session-state adapter so the framework's IPluginLifetime flips
    //    Idle ↔ Active based on Dalamud login/logout events.
    services.AddSingleton<ISessionStateProvider, DalamudSessionStateProvider>();

    // 2. Plugin-local localization source. Registered up front so any
    //    schema property that later switches to .LabelKey/.DescriptionKey
    //    starts resolving immediately. Plugin keys also win over module /
    //    framework keys via the layered localizer's reverse-registration rule.
    services.AddResourceLocalizer<Language>();

    // 3. Plugin-level (non-module) settings schema.
    services.AddSettings<TrackerSettings>(b => b
        .StoredAs("config")
        .Group("Tracker", order: 10)
        .Property(x => x.MaxRecentPlayers, p => p.NumericInput().Order(1)…)
        .Property(x => x.RefreshTtlDays,    p => p.NumericInput().Order(2)…));

    // 4. Plugin-side TTL provider — registered BEFORE the module pulls in
    //    its TryAddSingleton fallback so the module's default 7-day provider
    //    is skipped in favour of this settings-backed one.
    services.AddSingleton<IRefreshTtlProvider, SettingsRefreshTtlProvider>();

    // 5. Single composition entry — pulls InternalData + ExternalData and the
    //    bridging services (HistoryChangeRecorder, LiveTagChangeRefreshTrigger,
    //    PlayerRefreshQueueService) + the maintenance contributor + the
    //    refresh-queue diagnostics IAutoSettingsSection.
    services.AddNexusKitPlayerEnrichment();

    // 6. Chat-notification framework: registry + Notifications settings tab.
    services.AddNexusKitChatNotifications();

    // 7. Notification producers. Each subscribes to its event source in its
    //    constructor and registers its kind with the framework — resolution
    //    IS the registration side-effect (the iteration in Plugin.LoadAsync
    //    is what triggers it).
    services.AddSingleton<EnrichmentResolvedNotificationProducer>();
    services.AddSingleton<INotificationProducer>(sp =>
        sp.GetRequiredService<EnrichmentResolvedNotificationProducer>());
    services.AddSingleton<HistoryNotificationProducer>();
    services.AddSingleton<INotificationProducer>(sp =>
        sp.GetRequiredService<HistoryNotificationProducer>());
    services.AddSingleton<RefreshFailureNotificationProducer>();
    services.AddSingleton<INotificationProducer>(sp =>
        sp.GetRequiredService<RefreshFailureNotificationProducer>());

    // 8. User-defined player-list filters. Both the editor (settings tab)
    //    and the consumer (PlayerListPanel) read from the same registry
    //    singleton, so edits propagate without a plugin reload.
    services.AddSingleton<PlayerFilterRegistry>();
    services.AddSingleton<IAutoSettingsSection, PlayerFilterSettingsSection>();
    services.AddSingleton<IPlayerFilterDbQueryService, PlayerFilterDbQueryService>();
    services.AddSingleton<IPlayerFilterPreviewService, PlayerFilterPreviewService>();

    // 9. Framework-provided DB-maintenance section — wires the
    //    DbMaintenanceSettingsSection into auto-settings with order 200
    //    so it lands after the filter section (50) and the module sections.
    services.AddDbMaintenanceSettingsSection(order: 200);

    return services;
}
```

The plugin ships ONE module reference: `AddNexusKitPlayerEnrichment()`.
That call internally invokes `AddNexusKitInternalData()` and
`AddNexusKitExternalData()`, which in turn pull `AddNexusKitFfxivCollect()`
and `AddNexusKitLodestone()`. The whole data pipeline activates from one
line. An unrelated module (e.g. a future weather module) would still be one
line of its own next to this.

## Where new code goes

| Concern | Where |
|---|---|
| New Dalamud-injected service (`[PluginService]` static) | `Plugin.cs` + register in `ConfigureServices` |
| New module dependency | `AddServices()` |
| Plugin-level settings (non-`IModuleSettings`) | A POCO in `Settings/` + `services.AddSettings<T>` |
| Plugin-level translations | `Resources/Language.resx` |
| New chat-notification kind | A new `INotificationProducer` under `Notifications/` |
| Plugin-specific Window | `Ui/` |
| Domain logic (player tracking, filter logic, etc.) | Plugin-only — never in NexusKit |

## Why are background services eagerly resolved?

DI singletons are lazy by default — they're constructed only when first
requested. Several of the plugin's singletons have constructor side
effects:

- `PluginUiHost` subscribes to `UiBuilder.Draw`, `OpenMainUi`,
  `OpenConfigUi`, and `LanguageChanged`. If we never resolve it, nobody
  subscribes, and the user's clicks on the plugin's "Open" / gear buttons
  do nothing.
- `IInternalDataPlayerWatcher` subscribes to `IFramework.Update`.
- `IInternalDataHistoryService` subscribes to the watcher's
  `ObservationProcessed`.
- `IInternalDataEncounterTracker` subscribes to `TerritoryChanged`,
  `IPluginLifetime.StateChanged`, and `ObservationProcessed`.
- `IPlayerRefreshQueueService` spins up a worker thread + subscribes
  to `Observed`.
- `LiveTagChangeRefreshTrigger` subscribes to `ObservationProcessed`.
- `INotificationProducer`s register their kinds + subscribe to event
  sources.

The `host.Services.GetRequiredService<...>()` calls in `LoadAsync` are the
explicit kick to construct each. We don't keep references — disposing them
isn't our job, the ServiceProvider owns it.

The IPC providers in our modules are similarly "construct = register". The
`PluginHostBuilder` resolves `IEnumerable<IIpcProvider>` automatically
inside `BuildAsync`, so they don't need an explicit kick here.

## Shutdown

```csharp
public ValueTask DisposeAsync() => host.DisposeAsync();
```

The framework owns disposal of everything inside the ServiceProvider. The
host drives `IPluginLifetime` through `Stopping → Stopped` first so
services that subscribed to `StateChanged` (e.g. the encounter tracker)
get a synchronous last-chance write window before the cancellation token
fires and the container starts disposing.

## Dalamud manifest (`PlayerNexusTracker.yaml`)

Tells Dalamud how to display the plugin in `/xlplugins`. Required fields
match `Dalamud.NET.Sdk`'s `DalamudPackager` expectations. When you bump the
plugin version in `.csproj` or rename it, update the yaml in lockstep.

---

**Maintenance**: when you add a `[PluginService]` static, register a new
DI service, add/remove a module call, add a notification producer, or
restructure `AddServices()`, update this doc.
