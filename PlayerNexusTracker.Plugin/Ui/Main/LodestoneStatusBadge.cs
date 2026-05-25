using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Shared "Lodestone enrichment still running" cue. Same loc keys, same icon,
/// same player-loaded guard for both presentations — the only difference is the
/// underlying <see cref="NexusStatusNotice"/> mode: <c>Text</c> for the full
/// tab-body banner, <c>Tooltip</c> for the inline header hourglass.
/// <para>Returns <c>true</c> iff the cue was actually rendered, letting tabs
/// that have no live-data fallback short-circuit (<c>if (Draw(...)) return;</c>).
/// Header callers simply ignore the return value.</para>
/// </summary>
internal static class LodestoneStatusBadge
{
    public static bool Draw(
        [NotNullWhen(false)] Player? player,
        ObservedPlayer observed,
        ILocalizer loc,
        NexusStatusNoticeMode mode = NexusStatusNoticeMode.Text)
    {
        if (player is not null) return false;
        var key = observed.LodestoneId is null
            ? "ui.main.observation.banner_pending"
            : "ui.main.observation.banner_loading";
        NexusStatusNotice.Draw(
            mode,
            FontAwesomeIcon.Hourglass,
            loc.Get(key),
            sameLine: mode == NexusStatusNoticeMode.Tooltip);
        if (mode == NexusStatusNoticeMode.Text)
            ImGui.Dummy(new Vector2(0, 4f));
        return true;
    }
}
