using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using NexusKit.ChatNotifications;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Surfaces detected player-history changes as chat notifications via the
/// generic <see cref="IChatNotificationRegistry"/>. Registers one
/// <see cref="NotificationKindDefinition"/> per <see cref="PlayerHistoryKind"/>
/// so the user can mute name changes while keeping FC notifications (or vice
/// versa). Single producer + single event subscription — the dispatch table
/// inside <see cref="OnHistoryAdded"/> routes each entry to the right
/// publisher.
/// <para>FC-added vs FC-changed is intentionally not a separate kind: the
/// existing <see cref="PlayerHistoryKind.FreeCompanyChange"/> entry carries
/// <c>OldValue == null</c> when the player went from no FC into one, and
/// <see cref="HistoryFormatting.FormatChange"/> already renders that nicely.
/// Splitting into a separate kind would only add a settings row.</para>
/// </summary>
internal sealed class HistoryNotificationProducer : INotificationProducer, IDisposable
{
    /// <summary>Stable kind ids — change with care; existing user overrides
    /// in <c>ChatNotificationSettings.Overrides</c> become orphaned when
    /// these strings are renamed.</summary>
    public const string NameChangeKindId = "history.name_change";

    public const string WorldChangeKindId = "history.world_change";
    public const string CustomizeChangeKindId = "history.customize_change";
    public const string FreeCompanyChangeKindId = "history.freecompany_change";

    private const string GroupKey = "ui.notifications.group.history";

    private readonly IInternalDataHistoryService mHistory;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IExternalDataFreeCompanyService mFreeCompanies;
    private readonly IPlayerChangeSignal mSignal;
    private readonly ILocalizer mLoc;

    // Per-kind publishers, keyed by PlayerHistoryKind. Populated in the ctor;
    // OnHistoryAdded uses TryGetValue so future enum values (no publisher
    // registered yet) silently no-op instead of throwing.
    private readonly Dictionary<PlayerHistoryKind, IChatNotificationPublisher> mPublishers = new();

    private readonly Dictionary<PlayerHistoryKind, string> mFormatKeys = new();

    private bool mDisposed;

    public HistoryNotificationProducer(
        IInternalDataHistoryService history,
        IInternalDataPlayerWatcher watcher,
        IExternalDataFreeCompanyService freeCompanies,
        IPlayerChangeSignal signal,
        IChatNotificationRegistry registry,
        ILocalizer localizer)
    {
        mHistory = history;
        mWatcher = watcher;
        mFreeCompanies = freeCompanies;
        mSignal = signal;
        mLoc = localizer;

        Register(registry, PlayerHistoryKind.NameChange,
            NameChangeKindId,
            "ui.notifications.history.name_change");
        Register(registry, PlayerHistoryKind.HomeWorldChange,
            WorldChangeKindId,
            "ui.notifications.history.world_change");
        Register(registry, PlayerHistoryKind.CustomizeChange,
            CustomizeChangeKindId,
            "ui.notifications.history.customize_change");
        Register(registry, PlayerHistoryKind.FreeCompanyChange,
            FreeCompanyChangeKindId,
            "ui.notifications.history.freecompany_change");

        mHistory.HistoryAdded += OnHistoryAdded;
    }

    /// <summary>Registers one kind and remembers its publisher + format-string
    /// key under the matching <see cref="PlayerHistoryKind"/> enum value.
    /// <paramref name="resxRoot"/> is the prefix shared by the kind's
    /// <c>.label</c>, <c>.description</c>, and <c>.format</c> keys.</summary>
    private void Register(IChatNotificationRegistry registry,
                          PlayerHistoryKind kind,
                          string kindId,
                          string resxRoot)
    {
        // Per-kind history rows ship off by default — the generic catchall
        // kind already fires for these via the change-signal bus. Enabling
        // both produces duplicate lines per burst, so the row is also
        // SuppressedBy the catchall: the settings UI greys it out and the
        // publisher silently no-ops while the catchall is on.
        var publisher = registry.RegisterKind(new NotificationKindDefinition(
            Id: kindId,
            LabelKey: resxRoot + ".label",
            DescriptionKey: resxRoot + ".description",
            DefaultChannel: NotificationChannel.Echo,
            DefaultColor: NotificationColor.Yellow,
            GroupKey: GroupKey,
            DefaultEnabled: false,
            SuppressedBy: new[] { GeneralChangeNotificationProducer.KindId }));
        mPublishers[kind] = publisher;
        mFormatKeys[kind] = resxRoot + ".format";
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mHistory.HistoryAdded -= OnHistoryAdded;
    }

    private void OnHistoryAdded(ulong contentId, IReadOnlyList<PlayerHistoryEntry> entries)
    {
        _ = PublishAsync(contentId, entries);
    }

    private async Task PublishAsync(ulong contentId, IReadOnlyList<PlayerHistoryEntry> entries)
    {
        var name = NameFor(contentId) ?? "—";

        // Batch-resolve every FC id referenced from this notification burst
        // in one cache hit so each row can render "«TAG» Name" instead of the
        // bare FC#xxx fallback. Cache-only — never triggers a Lodestone fetch.
        var fcIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Kind != PlayerHistoryKind.FreeCompanyChange) continue;
            if (!string.IsNullOrEmpty(e.OldValue)) fcIds.Add(e.OldValue);
            if (!string.IsNullOrEmpty(e.NewValue)) fcIds.Add(e.NewValue);
        }

        Func<string, string?> fcLabel = static _ => null;
        if (fcIds.Count > 0)
        {
            try
            {
                var fcs = await mFreeCompanies.GetManyCachedAsync(fcIds).ConfigureAwait(false);
                fcLabel = id => fcs.TryGetValue(id, out var fc)
                    ? HistoryFormatting.FormatFreeCompanyLabel(fc)
                    : null;
            }
            catch { /* keep the FC#xxx fallback */ }
        }

        // Per-kind publishes — one line per entry. Disabled kinds get
        // silently dropped at the publisher level.
        foreach (var entry in entries)
        {
            if (!mPublishers.TryGetValue(entry.Kind, out var publisher)) continue;
            if (!mFormatKeys.TryGetValue(entry.Kind, out var formatKey)) continue;

            var change = HistoryFormatting.FormatChange(entry, mLoc, fcLabel);
            // NameChange is the one kind where the prefix `{0}` is the
            // *subject* of the rename, not the character's identity — using
            // the watcher's current name here would render
            // "<new> wurde umbenannt: '<old>' → '<new>'", which reads as if
            // the new identity was renamed. Other kinds use the player name:
            // OldValue there is the prior customize string / world / FC id,
            // never the character's name.
            var prefix = entry.Kind == PlayerHistoryKind.NameChange && !string.IsNullOrEmpty(entry.OldValue)
                ? entry.OldValue
                : name;
            var line = string.Format(mLoc.Get(formatKey), prefix, change);
            publisher.Publish(new SeString(new TextPayload(line)));
        }

        // Feed the cross-producer aggregator so the GeneralChange producer
        // emits a single "X changed" line per coalesced burst even when
        // multiple producers (history + collection-growth) detect changes
        // for the same player within the debounce window.
        if (entries.Count > 0)
            mSignal.Signal(contentId);
    }

    private string? NameFor(ulong contentId)
    {
        foreach (var p in mWatcher.Recent)
            if (p.ContentId == contentId) return p.Name;
        return null;
    }
}