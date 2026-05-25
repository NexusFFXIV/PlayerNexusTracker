# PlayerNexusTracker.Plugin

The Dalamud plugin entry point. Wires NexusKit pieces and modules together,
adds plugin-specific code on top.

**Plugin-specific code lives here only.** Anything reusable belongs in
NexusKit (`NexusKit.*` or `NexusKit.Modules.*`).

## Layout

```
PlayerNexusTracker.Plugin/
├── Plugin.cs                            IAsyncDalamudPlugin entry
├── PlayerNexusTracker.yaml              Dalamud manifest
├── Composition/
│   ├── DalamudSessionStateProvider.cs   ISessionStateProvider over IClientState
│   ├── PlayerNexusTrackerModule.cs      IPluginModule bridge (calls AddServices)
│   └── PluginServiceCollectionExtensions.cs    AddServices() — full plugin wiring
├── Logging/
│   └── DalamudPluginLogSink.cs          IPluginLogSink implementation via IPluginLog
├── Notifications/
│   ├── EnrichmentResolvedNotificationProducer.cs   "we resolved a Lodestone id"
│   ├── HistoryNotificationProducer.cs              "history row added" → chat
│   └── RefreshFailureNotificationProducer.cs       "refresh exhausted" → chat
├── Resources/
│   ├── Language.resx                    EN strings (plugin-local + producer keys)
│   ├── Language.de.resx                 DE translations
│   └── Language.Designer.cs             auto-generated ResourceManager wrapper
├── Settings/
│   ├── Filters/                         player-filter pipeline (registry, evaluator,
│   │                                    SQL builder, DB query service, preview service)
│   ├── PlayerListSortPreference.cs      sort-direction enum + per-column preference POCO
│   ├── SettingsRefreshTtlProvider.cs    IRefreshTtlProvider backed by TrackerSettings
│   └── TrackerSettings.cs               plugin-level (non-module) settings POCO
└── Ui/
    ├── DebugWindow.cs                   /xllog-style developer overlay
    ├── PnTrackerMainWindow.cs           main window shell + tab routing
    ├── Main/                            tabs, panels, formatting, placeholders
    │   ├── PlayerListPanel.cs           recent + all-players list with filter UI
    │   ├── PlayerDetailPanel.cs         right-pane player view
    │   ├── MainWindowState.cs           shared selection + refresh kicks
    │   ├── HistoryFormatting.cs         FC label + kind-row formatters for chat + UI
    │   ├── HistoryHint.cs               unread-dot hover tooltip
    │   ├── ObservationSections.cs       summary section renderers
    │   ├── AvatarPlaceholder.cs         loading / fallback avatar drawing
    │   ├── LodestoneStatusBadge.cs      enrichment-pending banner / header badge
    │   └── Tabs/                        Summary / History / Encounters / Notes / …
    └── Settings/
        └── PlayerFilterSettingsSection.cs   filter editor as IAutoSettingsSection
```

## `[PluginService]` statics

Dalamud injects these on the `Plugin` class before `LoadAsync` runs:

| Static | Type | Used for |
|---|---|---|
| `PluginInterface` | `IDalamudPluginInterface` | Plugin lifecycle, IPC, manifest data |
| `Log` | `IPluginLog` | Backing for `DalamudPluginLogSink` |
| `CommandManager` | `ICommandManager` | Needed by `NexusKit.Ui.Commands.CommandRegistry` |
| `ClientState` | `IClientState` | Login/logout via `DalamudSessionStateProvider`; territory tracking |
| `DataManager` | `IDataManager` | Lumina sheet access — required by `AddNexusKitGameData()` |
| `Framework` | `IFramework` | The observation watcher subscribes to `Update` here |
| `ObjectTable` | `IObjectTable` | What the watcher scans every 60 frames |
| `Condition` | `ICondition` | Used to suppress scans during DutyRecorderPlayback |
| `TextureProvider` | `ITextureProvider` | Avatar + icon image loading via `IImageCache` |
| `ChatGui` | `IChatGui` | Backing for the chat-notification framework |

`Plugin.LoadAsync` re-registers each as a DI singleton so the framework's
services can constructor-inject them.

## Composition (`ConfigureServices`)

Run by `PluginHostBuilder.BuildAsync` before any `IPluginModule.Register`:

```csharp
s.AddSingleton(PluginInterface);
s.AddSingleton(CommandManager);
s.AddSingleton(ClientState);
s.AddSingleton(DataManager);
s.AddSingleton(Framework);
s.AddSingleton(ObjectTable);
s.AddSingleton(Condition);
s.AddSingleton(TextureProvider);
s.AddSingleton(ChatGui);
s.AddNexusKitPersistence();        // DB factory + maintenance loop + stats
s.AddNexusKitSettings();           // settings store
s.AddNexusKitIpc();                // IPC registry
s.AddNexusKitUi();                 // windows, language, utilities, sections
s.AddNexusKitGameData();           // Lumina sheets + lookups + resolver
s.AddMainWindow<PnTrackerMainWindow>();
s.AddAutoSettingsWindow();         // framework-rendered settings UI
s.AddWindow<DebugWindow>();        // extra window via WindowManager
```

After `BuildAsync` returns, the plugin force-resolves the services whose
constructors carry initialization side-effects (subscriptions, worker
threads, IPC registrations). See `Plugin.LoadAsync` for the full list.

## Module registration

Modules opt in via `AddServices()` in
`Composition/PluginServiceCollectionExtensions.cs`:

```csharp
// Adapter so IPluginLifetime can flip Idle ↔ Active based on Dalamud login.
services.AddSingleton<ISessionStateProvider, DalamudSessionStateProvider>();

// Plugin-local localization source (resolves plugin keys + override of any
// framework key with the same name).
services.AddResourceLocalizer<Language>();

services.AddSettings<TrackerSettings>(b => …);

// Plugin-side TTL provider for the refresh queue — registered BEFORE
// AddNexusKitPlayerEnrichment so its TryAddSingleton fallback defers to us.
services.AddSingleton<IRefreshTtlProvider, SettingsRefreshTtlProvider>();

// One call brings InternalData + ExternalData + bridges + maintenance
// + refresh-queue diagnostics tab in transitively.
services.AddNexusKitPlayerEnrichment();

// Chat-notification framework + producers (eager-resolved at LoadAsync).
services.AddNexusKitChatNotifications();
services.AddSingleton<EnrichmentResolvedNotificationProducer>();
services.AddSingleton<INotificationProducer>(sp =>
    sp.GetRequiredService<EnrichmentResolvedNotificationProducer>());
// … same shape for HistoryNotificationProducer + RefreshFailureNotificationProducer.

// Player-filter system: registry + editor section + DB query + preview service.
services.AddSingleton<PlayerFilterRegistry>();
services.AddSingleton<IAutoSettingsSection, PlayerFilterSettingsSection>();
services.AddSingleton<IPlayerFilterDbQueryService, PlayerFilterDbQueryService>();
services.AddSingleton<IPlayerFilterPreviewService, PlayerFilterPreviewService>();

// DB-maintenance settings section: shows on-disk + per-table stats and a
// "Run now" button. Provided by NexusKit.Ui; ordered after filters (50) and
// module sections.
services.AddDbMaintenanceSettingsSection(order: 200);
```

Add a new (unrelated) module: write the `services.AddNexusKitXyz()` line
next to the existing ones. Add a new notification producer: one
`AddSingleton<T>()` + one `AddSingleton<INotificationProducer>(sp => sp.GetRequiredService<T>())`.

## Translation workflow

1. Open `Resources/Language.resx` in Visual Studio's resource editor.
2. Add key/value pairs. Designer.cs regenerates on save.
3. For another language, copy → `Language.<culture>.resx`, fill values.
4. The `ResourceLocalizer<Language>` registration is already in place; new
   keys resolve automatically.

The framework also ships its own `.resx` files (in `NexusKit.Ui/Resources/`,
plus one per module). Your `Language.resx` wins on key collisions thanks to
the `LayeredLocalizer`'s reverse-registration-order rule. See
[docs/translation-workflow.md](docs/translation-workflow.md) for the long
form.

## Build output

```
PlayerNexusTracker.Plugin/bin/x64/Debug/PlayerNexusTracker.dll
```

Plus the manifest yaml and dependency DLLs.

## Database location

`%APPDATA%/XIVLauncher/pluginConfigs/PlayerNexusTracker/PlayerNexusTracker.db`

## Where to read next

- [docs/composition.md](docs/composition.md) — line-by-line `LoadAsync` /
  `AddServices` walkthrough.
- [docs/translation-workflow.md](docs/translation-workflow.md) — how to
  add or change plugin-local strings.
- [../docs/architecture.md](../docs/architecture.md) — how everything connects
- [../README.md](../README.md) — repo overview

---

**Maintenance**: when you add a `[PluginService]` static, register a new
service, add a module, add a notification producer, add a folder, or
restructure the filter / settings sections, update this README.
