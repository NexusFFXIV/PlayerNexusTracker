using Dalamud.Interface;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.History;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Domain wrapper around <see cref="NexusHint"/>: given the loaded history
/// for the selected player, paint a small yellow "history" icon next to a
/// field that has a recorded change of <paramref name="kind"/>. Tooltip
/// shows the most-recent <c>{old → new · timestamp}</c> only; full
/// timeline lives in the History tab.
/// </summary>
internal static class HistoryHint
{
    public static void Draw(IReadOnlyList<PlayerHistoryEntry>? history,
                            PlayerHistoryKind kind, ILocalizer loc)
    {
        if (LatestOf(history, kind) is not { } latest) return;

        var oldVal = string.IsNullOrEmpty(latest.OldValue) ? "—" : latest.OldValue!;
        var newVal = string.IsNullOrEmpty(latest.NewValue) ? "—" : latest.NewValue!;
        var when = latest.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var tooltip = string.Format(
            loc.Get("ui.main.history.hint.tooltip"), oldVal, newVal, when);

        NexusHint.Draw(FontAwesomeIcon.History, tooltip);
    }

    private static PlayerHistoryEntry? LatestOf(IReadOnlyList<PlayerHistoryEntry>? history,
                                                PlayerHistoryKind kind)
    {
        if (history is null) return null;
        // CurrentHistory is sorted ChangedAt DESC, so the first match is the latest.
        for (var i = 0; i < history.Count; i++)
            if (history[i].Kind == kind) return history[i];
        return null;
    }
}
