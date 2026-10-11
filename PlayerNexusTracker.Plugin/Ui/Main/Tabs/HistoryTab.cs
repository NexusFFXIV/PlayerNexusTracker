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
    private static int sFilter;

    public static void Draw(Player? player, IReadOnlyList<PlayerHistoryEntry>? entries,
                            Func<string, string?> fcLabelResolver, ILocalizer loc,
                            PlayerSnapshotDiff? fullSnapshotDiff = null,
                            PlayerSnapshotDiff? selectedSnapshotDiff = null,
                            IReadOnlyList<PlayerSnapshotRecord>? snapshots = null,
                            IGameDataLookups? lookups = null,
                            int currentSnapshotIndex = 0,
                            int previousSnapshotIndex = 1,
                            Action<int, int>? selectSnapshots = null)
    {
        if (entries is null)
        {
            ImGui.Spacing(); NexusLoadingSpinner.Draw(20f); ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.loading"));
            return;
        }
        if (!ImGui.BeginTabBar("##history_subtabs")) return;
        if (ImGui.BeginTabItem(loc.Get("ui.main.tab.history")))
        {
            DrawHistory(entries, fullSnapshotDiff, fcLabelResolver, loc, lookups);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(loc.Get("ui.main.tab.history.compare")))
        {
            DrawComparison(selectedSnapshotDiff, snapshots, lookups, loc,
                currentSnapshotIndex, previousSnapshotIndex, selectSnapshots);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static void DrawHistory(IReadOnlyList<PlayerHistoryEntry> entries,
                                    PlayerSnapshotDiff? snapshotDiff,
                                    Func<string, string?> fcLabelResolver,
                                    ILocalizer loc, IGameDataLookups? lookups)
    {
        var kinds = new[] { (PlayerHistoryKind?)null, PlayerHistoryKind.NameChange,
            PlayerHistoryKind.HomeWorldChange, PlayerHistoryKind.CustomizeChange,
            PlayerHistoryKind.FreeCompanyChange, PlayerHistoryKind.SearchCommentChange };
        sFilter = Math.Clamp(sFilter, 0, kinds.Length - 1);
        var labels = kinds.Select(k => k is null ? loc.Get("ui.main.tab.history.filter.all")
            : HistoryFormatting.KindLabel(k.Value, loc)).ToArray();
        ImGui.SetNextItemWidth(220f);
        ImGui.Combo("##history_kind_filter", ref sFilter, labels, labels.Length);
        var selected = kinds[sFilter];
        var rows = entries.Where(e => selected is null || e.Kind == selected)
            .Select(e => new Row(e.ChangedAt, HistoryFormatting.KindLabel(e.Kind, loc),
                HistoryFormatting.FormatChange(e, loc, fcLabelResolver))).ToList();
        if (snapshotDiff is { HasChanges: true } diff)
            rows.AddRange(diff.Changes.Select(c => SnapshotRow(diff.CurrentCapturedAt, c, loc, lookups)));
        DrawRows(rows.OrderByDescending(r => r.Time), loc);
    }

    private static void DrawComparison(PlayerSnapshotDiff? diff,
                                       IReadOnlyList<PlayerSnapshotRecord>? snapshots,
                                       IGameDataLookups? lookups, ILocalizer loc,
                                       int currentIndex, int previousIndex,
                                       Action<int, int>? selectSnapshots)
    {
        if (snapshots is not { Count: > 1 } list || selectSnapshots is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.empty"));
            return;
        }
        var labels = list.Select((s, i) => $"{i}: {s.Snapshot.CapturedAt.ToLocalTime():g}").ToArray();
        var from = Math.Clamp(previousIndex, 0, labels.Length - 1);
        var to = Math.Clamp(currentIndex, 0, labels.Length - 1);
        ImGui.TextUnformatted(loc.Get("ui.main.tab.history.snapshot.from")); ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##history_snapshot_from", ref from, labels, labels.Length)) selectSnapshots(to, from);
        ImGui.SameLine(); ImGui.TextUnformatted(loc.Get("ui.main.tab.history.snapshot.to")); ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##history_snapshot_to", ref to, labels, labels.Length)) selectSnapshots(to, from);
        var rows = diff is { HasChanges: true }
            ? diff.Changes.Select(c => SnapshotRow(diff.CurrentCapturedAt, c, loc, lookups))
            : Enumerable.Empty<Row>();
        DrawRows(rows, loc);
    }

    private static Row SnapshotRow(DateTime time, PlayerSnapshotChange change,
                                   ILocalizer loc, IGameDataLookups? lookups)
    {
        var field = change.Field switch
        {
            "home_world_id" => "world", "class_job_id" => "classjob", "company_tag" => "company_tag",
            _ => change.Field,
        };
        string Format(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            if (!uint.TryParse(value, out var id)) return value;
            return field switch
            {
                "world" => lookups?.GetWorldName(id) ?? $"#{id}",
                "classjob" => lookups?.GetClassJobName(id) ?? $"#{id}",
                "race" => lookups?.GetRaceName(id, false) ?? $"#{id}",
                _ => value,
            };
        }
        return new(time, loc.Get("ui.main.tab.history.snapshot.field." + field),
            $"{Format(change.OldValue)} → {Format(change.NewValue)}");
    }

    private static void DrawRows(IEnumerable<Row> rows, ILocalizer loc)
    {
        var visible = rows.ToArray();
        if (visible.Length == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.history.empty"));
            return;
        }
        if (!ImGui.BeginTable("##history", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders)) return;
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.time"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.kind"), ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn(loc.Get("ui.main.tab.history.col.change"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var row in visible)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.FormatRelativeTimeAgo(row.Time));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Kind);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Change);
        }
        ImGui.EndTable();
    }
}
