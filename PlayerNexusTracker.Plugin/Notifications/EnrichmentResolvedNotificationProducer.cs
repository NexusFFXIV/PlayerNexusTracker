using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Surfaces the first-time Lodestone link of an observed character as a chat
/// notification. Subscribes to <see cref="IPlayerRefreshQueueService.Completed"/>
/// and filters by <see cref="RefreshCategory.LodestoneId"/> so only the
/// "search-and-resolve" step triggers — sub-resource fetches (gear, mounts, …)
/// reuse the same Completed event but are routed away here.
/// </summary>
internal sealed class EnrichmentResolvedNotificationProducer : INotificationProducer, IDisposable
{
    public const string KindId = "enrichment.lodestone_resolved";

    private readonly IPlayerRefreshQueueService mQueue;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IPlayerChangeSignal mSignal;
    private readonly ILocalizer mLoc;
    private readonly IChatNotificationPublisher mPublisher;
    private bool mDisposed;

    public EnrichmentResolvedNotificationProducer(
        IPlayerRefreshQueueService queue,
        IInternalDataPlayerWatcher watcher,
        IPlayerChangeSignal signal,
        IChatNotificationRegistry registry,
        ILocalizer localizer)
    {
        mQueue = queue;
        mWatcher = watcher;
        mSignal = signal;
        mLoc = localizer;
        // Default-OFF + suppressed by the general catchall: the settings UI
        // prevents enabling both at once. The catchall already fires for
        // this producer's events via the change-signal bus.
        mPublisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: KindId,
            LabelKey: "ui.notifications.enrichment_resolved.label",
            DescriptionKey: "ui.notifications.enrichment_resolved.description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Red,
            GroupKey: "ui.notifications.group.background",
            DefaultEnabled: false,
            SuppressedBy: new[] { GeneralChangeNotificationProducer.KindId }));

        mQueue.Completed += OnCompleted;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mQueue.Completed -= OnCompleted;
    }

    private void OnCompleted(ulong contentId, RefreshCategory category)
    {
        if (category != RefreshCategory.LodestoneId) return;
        var name = NameFor(contentId) ?? "—";
        var line = string.Format(
            mLoc.Get("ui.notifications.enrichment_resolved.format"), name);
        mPublisher.Publish(new SeString(new TextPayload(line)));

        // Feed the catchall — the general-change producer fires once per
        // coalesced burst even when this is the only thing that landed.
        mSignal.Signal(contentId);
    }

    private string? NameFor(ulong contentId)
    {
        foreach (var p in mWatcher.Recent)
            if (p.ContentId == contentId) return p.Name;
        return null;
    }
}
