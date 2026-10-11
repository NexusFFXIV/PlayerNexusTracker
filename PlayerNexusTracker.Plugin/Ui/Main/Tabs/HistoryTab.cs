using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.History;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class HistoryTab
{
    private sealed record Row(DateTime Time, string Kind, string Change);

    public static void Draw(Player? player, IReadOnlyList<PlayerHistoryEntry>? entries, Func<string, string?> resolver, ILocalizer loc, PlayerSnapshotDiff? fullDiff = null, PlayerSnapshotDiff? selectedDiff = null, IReadOnlyList<PlayerSnapshotRecord>? snapshots = null, IGameDataLookups? lookups = null, HistoryFilterPreferences? filters = null, int currentIndex = 0, int previousIndex = 1, Action<int, int>? selectSnapshots = null)
    {
        if (entries is null) { ImGui.Spacing(); NexusLoadingSpinner.Draw(20f); ImGui.SameLine(); ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.loading")); return; }
        var categories = Categories(loc); filters?.EnsureLoaded(categories.Select(c => c.Key));
        if (!ImGui.BeginTabBar("##history_subtabs")) return;
        if (ImGui.BeginTabItem(loc.Get("ui.main.tab.history"))) { DrawHistory(entries, fullDiff, resolver, loc, lookups, filters, categories); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem(loc.Get("ui.main.tab.history.compare"))) { DrawComparison(selectedDiff, snapshots, lookups, loc, filters, currentIndex, previousIndex, selectSnapshots); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private static void DrawHistory(IReadOnlyList<PlayerHistoryEntry> entries, PlayerSnapshotDiff? diff, Func<string, string?> resolver, ILocalizer loc, IGameDataLookups? lookups, HistoryFilterPreferences? filters, (string Key, string Label)[] categories)
    {
        DrawFilterMenu(categories, filters, loc);
        var rows = entries.Where(e => filters is null || filters.IsSelected(HistoryFilterKey(e.Kind))).Select(e => new Row(e.ChangedAt, HistoryFormatting.KindLabel(e.Kind, loc), HistoryFormatting.FormatChange(e, loc, resolver))).ToList();
        if (diff is { HasChanges: true }) rows.AddRange(diff.Changes.Where(c => filters is null || filters.IsSelected("snapshot." + c.Field)).Select(c => SnapshotRow(diff.CurrentCapturedAt, c, loc, lookups)));
        DrawRows(rows.OrderByDescending(r => r.Time), loc);
    }

    private static void DrawComparison(PlayerSnapshotDiff? diff, IReadOnlyList<PlayerSnapshotRecord>? snapshots, IGameDataLookups? lookups, ILocalizer loc, HistoryFilterPreferences? filters, int currentIndex, int previousIndex, Action<int, int>? selectSnapshots)
    {
        if (snapshots is not { Count: > 1 } list || selectSnapshots is null) { ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.empty")); return; }
        var labels = Enumerable.Range(0, list.Count)
            .Select(displayIndex =>
            {
                var snapshot = list[list.Count - 1 - displayIndex].Snapshot;
                return $"{displayIndex + 1}: {snapshot.CapturedAt.ToLocalTime():g}";
            })
            .ToArray();
        var from = Math.Clamp(list.Count - 1 - previousIndex, 0, labels.Length - 1);
        var to = Math.Clamp(list.Count - 1 - currentIndex, 0, labels.Length - 1);
        ImGui.TextUnformatted(loc.Get("ui.main.tab.history.snapshot.from")); ImGui.SameLine(); ImGui.SetNextItemWidth(180f); if (ImGui.Combo("##history_snapshot_from", ref from, labels, labels.Length)) selectSnapshots(list.Count - 1 - to, list.Count - 1 - from);
        ImGui.SameLine(); ImGui.TextUnformatted(loc.Get("ui.main.tab.history.snapshot.to")); ImGui.SameLine(); ImGui.SetNextItemWidth(180f); if (ImGui.Combo("##history_snapshot_to", ref to, labels, labels.Length)) selectSnapshots(list.Count - 1 - to, list.Count - 1 - from);
        DrawRows((diff?.Changes ?? Array.Empty<PlayerSnapshotChange>()).Where(c => filters is null || filters.IsSelected("snapshot." + c.Field)).Select(c => SnapshotRow(diff!.CurrentCapturedAt, c, loc, lookups)), loc);
    }

    private static Row SnapshotRow(DateTime time, PlayerSnapshotChange change, ILocalizer loc, IGameDataLookups? lookups)
    {
        var field = change.Field switch { "home_world_id" => "world", "active_class_job" => "classjob_level", "company_tag" => "company_tag", _ => change.Field };
        string Format(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            if (field == "classjob_level")
            {
                var parts = value.Split(':', 2);
                if (parts.Length == 2 && uint.TryParse(parts[0], out var job) && byte.TryParse(parts[1], out var level))
                    return $"{lookups?.GetClassJobName(job) ?? $"#{job}"} ({level})";
                return value;
            }
            if (!uint.TryParse(value, out var id)) return value;
            return field switch { "world" => lookups?.GetWorldName(id) ?? $"#{id}", "race" => lookups?.GetRaceName(id, false) ?? $"#{id}", _ => value };
        }
        return new(time, loc.Get("ui.main.tab.history.snapshot.field." + field), $"{Format(change.OldValue)} → {Format(change.NewValue)}");
    }

    private static (string Key, string Label)[] Categories(ILocalizer loc) => new[] { "name_change", "world_change", "customize_change", "fc_change", "search_comment_change" }.Select(k => ($"history.{k}", loc.Get("ui.main.tab.history.kind." + k))).Concat(new[] { "name", "home_world_id", "active_class_job", "race", "gender", "company_tag" }.Select(k => ($"snapshot.{k}", loc.Get("ui.main.tab.history.snapshot.field." + (k switch { "home_world_id" => "world", "active_class_job" => "classjob_level", _ => k }))))).ToArray();

    private static string HistoryFilterKey(PlayerHistoryKind kind) => kind switch
    {
        PlayerHistoryKind.NameChange => "history.name_change",
        PlayerHistoryKind.HomeWorldChange => "history.world_change",
        PlayerHistoryKind.CustomizeChange => "history.customize_change",
        PlayerHistoryKind.FreeCompanyChange => "history.fc_change",
        PlayerHistoryKind.SearchCommentChange => "history.search_comment_change",
        _ => "history." + kind,
    };

    private static void DrawFilterMenu((string Key, string Label)[] categories, HistoryFilterPreferences? filters, ILocalizer loc)
    {
        if (filters is null) return; var selected = categories.Count(c => filters.IsSelected(c.Key)); var preview = selected == categories.Length ? loc.Get("ui.main.tab.history.filter.all") : string.Format(loc.Get("ui.main.tab.history.filter.selected"), selected);
        if (!ImGui.BeginCombo("##history_kind_filter", preview)) return;
        foreach (var c in categories) { var value = filters.IsSelected(c.Key); if (ImGui.Checkbox(c.Label + $"##{c.Key}", ref value)) filters.SetSelected(c.Key, value); }
        ImGui.EndCombo();
    }

    private static void DrawRows(IEnumerable<Row> rows, ILocalizer loc)
    {
        var visible = rows.ToArray(); if (visible.Length == 0) { ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.empty")); return; }
        if (!ImGui.BeginTable("##history", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders)) return;
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.time"), ImGuiTableColumnFlags.WidthFixed, 90f); ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.kind"), ImGuiTableColumnFlags.WidthFixed, 130f); ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.change"), ImGuiTableColumnFlags.WidthStretch); ImGui.TableHeadersRow();
        foreach (var row in visible) { ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextColored(ImGuiColors.DalamudGrey, loc.FormatRelativeTimeAgo(row.Time)); ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Kind); ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Change); }
        ImGui.EndTable();
    }
}
