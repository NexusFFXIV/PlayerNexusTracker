using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Surfaces "the refresh worker gave up on this character + category after
/// MaxAttempts failures" as a chat notification. The row stays in the queue
/// so a user-initiated Refresh can revive it (UpsertAsync resets the
/// bookkeeping); the chat line tells the user there's something to revive.
/// </summary>
internal sealed class RefreshFailureNotificationProducer : INotificationProducer, IDisposable
{
    public const string KindId = "refresh.attempts_exhausted";

    private readonly IPlayerRefreshQueueService mQueue;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IPlayerChangeSignal mSignal;
    private readonly ILocalizer mLoc;
    private readonly IChatNotificationPublisher mPublisher;
    private bool mDisposed;

    public RefreshFailureNotificationProducer(
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
        // prevents enabling both at once.
        mPublisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: KindId,
            LabelKey: "ui.notifications.refresh_failure.label",
            DescriptionKey: "ui.notifications.refresh_failure.description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Red,
            GroupKey: "ui.notifications.group.background",
            DefaultEnabled: false,
            SuppressedBy: new[] { GeneralChangeNotificationProducer.KindId }));

        mQueue.ExhaustedAttempts += OnExhausted;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mQueue.ExhaustedAttempts -= OnExhausted;
    }

    private void OnExhausted(ulong contentId, RefreshCategory category)
    {
        var name = NameFor(contentId) ?? "—";
        var categoryLabel = mLoc.Get($"ui.notifications.refresh_failure.category.{category.ToString().ToLowerInvariant()}");
        var line = string.Format(
            mLoc.Get("ui.notifications.refresh_failure.format"), name, categoryLabel);
        mPublisher.Publish(new SeString(new TextPayload(line)));

        // Feed the catchall — the general "X has changed" line fires too so
        // users running with only the general notification on still hear
        // about a player whose refresh worker just gave up.
        mSignal.Signal(contentId);
    }

    private string? NameFor(ulong contentId)
    {
        foreach (var p in mWatcher.Recent)
            if (p.ContentId == contentId) return p.Name;
        return null;
    }
}
