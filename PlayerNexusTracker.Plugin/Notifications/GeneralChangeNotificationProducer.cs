using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Players;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Publishes a single "{name} has changed" chat line whenever
/// <see cref="PlayerChangeAggregator.Aggregated"/> fires — the debounced
/// roll-up that other producers feed via <see cref="IPlayerChangeSignal.Signal"/>.
/// Default-enabled and intentionally light on detail; users who want
/// granular per-event lines turn on the per-kind history notifications and
/// the per-collection growth notifications and (typically) disable this one.
/// </summary>
internal sealed class GeneralChangeNotificationProducer : INotificationProducer, IDisposable
{
    /// <summary>Stable kind id — kept under the <c>notifications.</c>
    /// namespace (not <c>history.</c>) so it's clear this kind covers any
    /// source of change, not just the history pipeline.</summary>
    public const string KindId = "notifications.general_change";

    private readonly PlayerChangeAggregator mAggregator;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly ILocalizer mLoc;
    private readonly IChatNotificationPublisher mPublisher;
    private bool mDisposed;

    public GeneralChangeNotificationProducer(
        PlayerChangeAggregator aggregator,
        IInternalDataPlayerWatcher watcher,
        IChatNotificationRegistry registry,
        ILocalizer localizer)
    {
        mAggregator = aggregator;
        mWatcher = watcher;
        mLoc = localizer;
        mPublisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: KindId,
            LabelKey: "ui.notifications.general_change.label",
            DescriptionKey: "ui.notifications.general_change.description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Yellow,
            GroupKey: "ui.notifications.group.general"));

        mAggregator.Aggregated += OnAggregated;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mAggregator.Aggregated -= OnAggregated;
    }

    private void OnAggregated(ulong contentId)
    {
        var name = NameFor(contentId) ?? "—";
        var line = string.Format(
            mLoc.Get("ui.notifications.general_change.format"), name);
        mPublisher.Publish(new SeString(new TextPayload(line)));
    }

    private string? NameFor(ulong contentId)
    {
        foreach (var p in mWatcher.Recent)
            if (p.ContentId == contentId) return p.Name;
        return null;
    }
}
