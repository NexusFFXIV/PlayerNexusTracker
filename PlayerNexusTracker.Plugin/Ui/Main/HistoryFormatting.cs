using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.History;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Shared formatting for <see cref="PlayerHistoryEntry"/> values — the kind
/// label and the human-readable "X → Y" change description. Lives in the UI
/// layer because both depend on <see cref="ILocalizer"/>; multiple call sites
/// (the History tab, the player-list dot tooltip, the chat notifier) share
/// these helpers so the wording stays consistent.
/// </summary>
internal static class HistoryFormatting
{
    public static string KindLabel(PlayerHistoryKind kind, ILocalizer loc) => kind switch
    {
        PlayerHistoryKind.NameChange           => loc.Get("ui.main.tab.history.kind.name_change"),
        PlayerHistoryKind.HomeWorldChange      => loc.Get("ui.main.tab.history.kind.world_change"),
        PlayerHistoryKind.CustomizeChange      => loc.Get("ui.main.tab.history.kind.customize_change"),
        PlayerHistoryKind.FreeCompanyChange    => loc.Get("ui.main.tab.history.kind.fc_change"),
        _ => kind.ToString(),
    };

    /// <summary>Renders a history row's change description. For
    /// <see cref="PlayerHistoryKind.FreeCompanyChange"/>, values are FC
    /// Lodestone ids; <paramref name="fcLabel"/> is consulted (when supplied)
    /// to turn each id into a human-readable "«TAG» Name" string. The
    /// resolver returning null falls back to the raw <c>FC#{id}</c> form so
    /// callers without a catalog (e.g. the chat notifier on a cold startup)
    /// still produce readable output.</summary>
    public static string FormatChange(
        PlayerHistoryEntry entry,
        ILocalizer loc,
        Func<string, string?>? fcLabel = null)
    {
        if (entry.Kind == PlayerHistoryKind.FreeCompanyChange)
        {
            string RenderFc(string id) => fcLabel?.Invoke(id) ?? $"FC#{id}";
            return (entry.OldValue, entry.NewValue) switch
            {
                (null or "", null or "") => "—",
                (null or "", { } n)      => string.Format(loc.Get("ui.main.tab.history.fc.joined"), RenderFc(n)),
                ({ } o, null or "")      => string.Format(loc.Get("ui.main.tab.history.fc.left"), RenderFc(o)),
                ({ } o, { } n)           => $"{RenderFc(o)} → {RenderFc(n)}",
            };
        }

        var oldStr = string.IsNullOrEmpty(entry.OldValue) ? "—" : $"'{entry.OldValue}'";
        var newStr = string.IsNullOrEmpty(entry.NewValue) ? "—" : $"'{entry.NewValue}'";
        return $"{oldStr} → {newStr}";
    }

    /// <summary>Produces the "«TAG» Name" presentation used inside FC history
    /// rendering. Public so the chat notifier and any other caller building
    /// its own one-shot resolver can format catalog hits the same way.</summary>
    public static string FormatFreeCompanyLabel(NexusKit.Modules.ExternalData.Models.FreeCompany fc)
    {
        if (string.IsNullOrEmpty(fc.Tag)) return fc.Name;
        return $"«{fc.Tag}» {fc.Name}";
    }
}
