using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Players;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Shared empty-state for tabs whose entire content depends on enriched Lodestone
/// data. Renders one wrapped line — "still resolving the character" vs. "fetch in
/// flight" — so each tab stays visible (with the same chrome) instead of swapping
/// to a different layout while enrichment lands.
/// </summary>
internal static class LodestonePlaceholder
{
    public static void Draw(ObservedPlayer observed, ILocalizer loc)
    {
        var key = observed.LodestoneId is null
            ? "ui.main.observation.banner_pending"
            : "ui.main.observation.banner_loading";
        ImGui.PushTextWrapPos();
        ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get(key));
        ImGui.PopTextWrapPos();
    }
}
