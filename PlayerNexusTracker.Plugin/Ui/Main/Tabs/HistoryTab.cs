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
    private static int sFilter;

    public static void Draw(Player? player, IReadOnlyList<PlayerHistoryEntry>? entries,
                            Func<string, string?> fcLabelResolver, ILocalizer loc,
                            PlayerSnapshotDiff? snapshotDiff = null)
    {
        if (entries is null)
        {
            ImGui.Spacing();
            NexusLoadingSpinner.Draw(20f);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.loading"));
            return;
        }

        var kinds = new[]
        {
            (PlayerHistoryKind?)null,
            PlayerHistoryKind.NameChange,
            PlayerHistoryKind.HomeWorldChange,
            PlayerHistoryKind.CustomizeChange,
            PlayerHistoryKind.FreeCompanyChange,
            PlayerHistoryKind.SearchCommentChange,
        };
        sFilter = Math.Clamp(sFilter, 0, kinds.Length - 1);
        var labels = kinds.Select(k => k is null
            ? loc.Get("ui.main.tab.history.filter.all")
            : HistoryFormatting.KindLabel(k.Value, loc)).ToArray();
        ImGui.SetNextItemWidth(220f);
        ImGui.Combo("##history_kind_filter", ref sFilter, labels, labels.Length);
        if (snapshotDiff is { HasChanges: true } currentDiff)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(loc.Get("ui.main.tab.history.since_scan"),
                currentDiff.Changes.Count));
            ImGui.TextDisabled(string.Format(loc.Get("ui.main.tab.history.snapshot_range"),
                currentDiff.PreviousCapturedAt.ToLocalTime().ToString("g"),
                currentDiff.CurrentCapturedAt.ToLocalTime().ToString("g")));
            foreach (var change in currentDiff.Changes)
            {
                var oldValue = string.IsNullOrEmpty(change.OldValue) ? "—" : change.OldValue;
                var newValue = string.IsNullOrEmpty(change.NewValue) ? "—" : change.NewValue;
                ImGui.BulletText(string.Format(loc.Get("ui.main.tab.history.snapshot_change"),
                    change.Field, oldValue, newValue));
            }
        }
        var visible = kinds[sFilter] is { } selected
            ? entries.Where(e => e.Kind == selected).ToArray()
            : entries;

        if (visible.Count == 0)
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

        foreach (var entry in visible)
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
