using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.FreeCompanies;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Catch-all chat line for Free Company entity lifecycle events — sibling
/// of <see cref="GeneralChangeNotificationProducer"/> on the player side.
/// Subscribes to both <see cref="IExternalDataFreeCompanyService.FreeCompanyAdded"/>
/// (first-time persist) and <see cref="IExternalDataFreeCompanyService.FreeCompanyChanged"/>
/// (any persisted field differed on upsert) so a single line covers every
/// in-group FC notification kind. Lives in the "General" group and is
/// default-on; granular per-kind producers in the "Free Company history"
/// group declare <c>SuppressedBy = [KindId]</c> so the settings UI greys
/// them out while this one is enabled (and vice versa).
/// <para>The kind id stays <c>enrichment.freecompany_changed</c> for
/// override stability — renaming would orphan any existing user settings.</para>
/// </summary>
internal sealed class FreeCompanyChangedNotificationProducer : INotificationProducer, IDisposable
{
    public const string KindId = "enrichment.freecompany_changed";

    private readonly IExternalDataFreeCompanyService mFreeCompanies;
    private readonly ILocalizer mLoc;
    private readonly IChatNotificationPublisher mPublisher;
    private bool mDisposed;

    public FreeCompanyChangedNotificationProducer(
        IExternalDataFreeCompanyService freeCompanies,
        IChatNotificationRegistry registry,
        ILocalizer localizer)
    {
        mFreeCompanies = freeCompanies;
        mLoc = localizer;
        mPublisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: KindId,
            LabelKey: "ui.notifications.enrichment.freecompany_changed.label",
            DescriptionKey: "ui.notifications.enrichment.freecompany_changed.description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Yellow,
            GroupKey: "ui.notifications.group.general",
            DefaultEnabled: true));

        mFreeCompanies.FreeCompanyAdded += OnFreeCompanyEvent;
        mFreeCompanies.FreeCompanyChanged += OnFreeCompanyEvent;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mFreeCompanies.FreeCompanyAdded -= OnFreeCompanyEvent;
        mFreeCompanies.FreeCompanyChanged -= OnFreeCompanyEvent;
    }

    private void OnFreeCompanyEvent(string lodestoneFcId)
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
            mLoc.Get("ui.notifications.enrichment.freecompany_changed.format"), label);
        mPublisher.Publish(new SeString(new TextPayload(line)));
    }
}
