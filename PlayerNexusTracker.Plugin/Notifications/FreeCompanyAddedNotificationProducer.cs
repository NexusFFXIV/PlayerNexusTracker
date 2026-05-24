using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.FreeCompanies;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Publishes a chat line the first time an FC row is persisted into
/// <c>nexus_external_free_company</c>. Driven by
/// <see cref="IExternalDataFreeCompanyService.FreeCompanyAdded"/>, which fires
/// once per upsert that produced a brand-new row — the complementary signal
/// to <see cref="FreeCompanyChangedNotificationProducer"/> (which stays silent
/// on first inserts). Together they cover the full FC-lifecycle in the
/// "FC history" notification group.
/// </summary>
internal sealed class FreeCompanyAddedNotificationProducer : INotificationProducer, IDisposable
{
    public const string KindId = "enrichment.freecompany_added";
    private const string GroupKey = "ui.notifications.group.fc_history";

    private readonly IExternalDataFreeCompanyService mFreeCompanies;
    private readonly ILocalizer mLoc;
    private readonly IChatNotificationPublisher mPublisher;
    private bool mDisposed;

    public FreeCompanyAddedNotificationProducer(
        IExternalDataFreeCompanyService freeCompanies,
        IChatNotificationRegistry registry,
        ILocalizer localizer)
    {
        mFreeCompanies = freeCompanies;
        mLoc = localizer;
        // SuppressedBy the FC catch-all (which lives in "General"): the
        // settings UI greys this row out while the catch-all is on, and —
        // via the reverse direction in ResolveSuppression — greys the
        // catch-all when this granular kind is on. Same UX as the
        // player-history rows vs. GeneralChangeNotificationProducer.
        mPublisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: KindId,
            LabelKey: "ui.notifications.enrichment.freecompany_added.label",
            DescriptionKey: "ui.notifications.enrichment.freecompany_added.description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Yellow,
            GroupKey: GroupKey,
            DefaultEnabled: false,
            SuppressedBy: new[] { FreeCompanyChangedNotificationProducer.KindId }));

        mFreeCompanies.FreeCompanyAdded += OnFreeCompanyAdded;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mFreeCompanies.FreeCompanyAdded -= OnFreeCompanyAdded;
    }

    private void OnFreeCompanyAdded(string lodestoneFcId)
    {
        _ = PublishAsync(lodestoneFcId);
    }

    private async Task PublishAsync(string lodestoneFcId)
    {
        string label;
        try
        {
            var map = await mFreeCompanies.GetManyCachedAsync(new[] { lodestoneFcId })
                .ConfigureAwait(false);
            label = map.TryGetValue(lodestoneFcId, out var fc)
                ? Ui.Main.HistoryFormatting.FormatFreeCompanyLabel(fc)
                : "FC#" + lodestoneFcId;
        }
        catch
        {
            label = "FC#" + lodestoneFcId;
        }

        var line = string.Format(
            mLoc.Get("ui.notifications.enrichment.freecompany_added.format"), label);
        mPublisher.Publish(new SeString(new TextPayload(line)));
    }
}
