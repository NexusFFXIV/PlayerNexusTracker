using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusKit.Core.Context;
using NexusKit.GameData;
using NexusKit.Hosting;
using NexusKit.Ipc;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Persistence;
using NexusKit.Ui;
using NexusKit.ChatNotifications;
using PlayerNexusTracker.Composition;
using PlayerNexusTracker.Logging;
using PlayerNexusTracker.Ui;

namespace PlayerNexusTracker;

/// <summary>
/// Dalamud entry point. Holds the <c>[PluginService]</c>-injected Dalamud
/// handles (statics by Dalamud convention, set before <see cref="LoadAsync"/>
/// runs) and owns the <see cref="PluginHost"/> that composes the framework +
/// modules into a single DI graph for the plugin session. <see cref="LoadAsync"/>
/// is the only Dalamud-touching code path; everything else goes through DI.
/// </summary>
public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;

    private PluginHost? host;

    public IServiceProvider Services => host?.Services
        ?? throw new InvalidOperationException("PluginHost is not built yet — LoadAsync has not completed.");

    /// <param name="cancellationToken">Dalamud's LOAD token. It is a 60-second
    /// timeout on the load operation, NOT the plugin's lifetime: LocalPlugin.
    /// LoadAsync fabricates a CTS with CancelAfter(60s) when the caller passes
    /// no token, and no call site passes one. So it must only ever scope
    /// build-time work — it is deliberately NOT handed to the host as a
    /// shutdown signal (see PluginHostBuilder.WithShutdownSignal). The
    /// authoritative unload signal is DisposeAsync below.</param>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var context = new PluginContext(
            PluginName: nameof(PlayerNexusTracker),
            ConfigDirectory: PluginInterface.GetPluginConfigDirectory(),
            PluginVersion: typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 1, 0, 0));

        // NOTE: cancellationToken scopes the BUILD only (migrations). It is
        // deliberately not passed to WithShutdownSignal — see the param doc.
        var built = await new PluginHostBuilder()
            .WithContext(context)
            .WithLogSink(new DalamudPluginLogSink(Log))
            .WithModule(new PlayerNexusTrackerModule())
            .ConfigureServices(s =>
            {
                s.AddSingleton(PluginInterface);
                s.AddSingleton(CommandManager);
                s.AddSingleton(ClientState);
                s.AddSingleton(DataManager);
                s.AddSingleton(Framework);
                s.AddSingleton(ObjectTable);
                s.AddSingleton(Condition);
                s.AddSingleton(TextureProvider);
                s.AddSingleton(ChatGui);
                s.AddSingleton(GameGui);
                s.AddNexusKitPersistence();
                s.AddNexusKitSettings();
                s.AddNexusKitIpc();
                s.AddNexusKitUi();
                s.AddNexusKitGameData();
                s.AddMainWindow<PnTrackerMainWindow>();
                s.AddAutoSettingsWindow();
                s.AddWindow<DebugWindow>();
            })
            .BuildAsync(cancellationToken);

        // Publish the host before the eager resolves below: if one of them
        // throws, Dalamud still calls DisposeAsync and we want it to tear the
        // container down rather than leak it with half its singletons live.
        host = built;
        var services = built.Services;

        services.GetRequiredService<PluginUiHost>();
        // Eagerly resolve the game-object watcher so its IFramework.Update subscription
        // wires up before anyone opens the UI.
        services.GetRequiredService<IInternalDataPlayerWatcher>();
        // Same reasoning for the history service — its ctor subscribes to the watcher's
        // ObservationProcessed event. Without an eager resolve, no diffs would be captured.
        services.GetRequiredService<IInternalDataHistoryService>();
        // Encounter tracker subscribes to ObservationProcessed + TerritoryChanged +
        // Logout in its ctor — resolve early so the first zone-change after login
        // produces an encounter row, not a missed event.
        services.GetRequiredService<IInternalDataEncounterTracker>();
        // The refresh queue subscribes to Watcher.Observed in its ctor and spins
        // up the worker thread there — eagerly resolve so both happen on plugin
        // load instead of waiting for the first UI access.
        services.GetRequiredService<IPlayerRefreshQueueService>();
        // Live-tag → Profile-refresh trigger. Ctor subscribes to
        // ObservationProcessed; needs to be alive before observations start
        // ticking, otherwise the first FC tag flip after login goes unnoticed
        // until the TTL sweep catches up.
        services.GetRequiredService<NexusKit.Modules.PlayerEnrichment.Bridges.LiveTagChangeRefreshTrigger>();
        // Notification producers register kinds + subscribe to their event
        // sources in their constructors — resolution IS the registration.
        // Iterate so adding a new producer is a single registration line
        // in PluginServiceCollectionExtensions, no edit needed here.
        foreach (var _ in services.GetServices<INotificationProducer>())
        {
            // resolution is the registration side-effect; nothing else to do
        }

        var logger = host.Services.GetRequiredService<ILogger<Plugin>>();
        logger.LogInformation("PlayerNexusTracker loaded. Version={Version}", context.PluginVersion);
    }

    /// <summary>The authoritative "plugin is going away" signal. Dalamud calls
    /// this even when <see cref="LoadAsync"/> threw (so <c>host</c> can be
    /// null), and can call it more than once on some unload paths — hence the
    /// single-shot exchange, which also makes a concurrent call a no-op.</summary>
    public async ValueTask DisposeAsync()
    {
        var h = Interlocked.Exchange(ref host, null);
        if (h is not null)
            await h.DisposeAsync().ConfigureAwait(false);
    }
}