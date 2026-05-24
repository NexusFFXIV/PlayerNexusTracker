using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.History;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class HistoryTab
{
    public static void Draw(Player? player, IReadOnlyList<PlayerHistoryEntry>? entries,
                            Func<string, string?> fcLabelResolver, ILocalizer loc)
    {
        if (entries is null)
        {
            ImGui.Spacing();
            NexusLoadingSpinner.Draw(20f);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.loading"));
            return;
        }

        if (entries.Count == 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.empty"));
            ImGui.PopTextWrapPos();
            return;
        }

        if (!ImGui.BeginTable("##history", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.time"),
            ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.kind"),
            ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.change"),
            ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.FormatRelativeTimeAgo(entry.ChangedAt));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(HistoryFormatting.KindLabel(entry.Kind, loc));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(HistoryFormatting.FormatChange(entry, loc, fcLabelResolver));
        }

        ImGui.EndTable();
    }

}
