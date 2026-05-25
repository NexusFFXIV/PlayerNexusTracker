using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class AchievementsTab
{
    private const float RowHeight = 22f;

    public static void Draw(Player? player, ObservedPlayer observed,
                            IReadOnlyDictionary<int, AchievementEntry>? catalog,
                            ILocalizer loc)
    {
        if (LodestoneStatusBadge.Draw(player, observed, loc)) return;

        var stats = player.Collections?.Achievements;
        var owned = player.Collections?.OwnedAchievements;

        if (owned is not { Count: > 0 })
        {
            ImGui.TextDisabled(loc.Get("ui.main.tab.achievements.empty"));
            return;
        }

        // Newest first when we have timestamps (Lodestone path); fall back to id
        // descending for FFXIVCollect-only data which doesn't expose dates.
        var sorted = owned
            .OrderByDescending(a => a.AchievedAt ?? DateTime.MinValue)
            .ThenByDescending(a => a.Id)
            .ToList();

        string? titleSuffix = null;
        if (stats is not null && stats.Total > 0)
        {
            var pct = sorted.Count * 100 / stats.Total;
            titleSuffix = $"{sorted.Count} / {stats.Total} ({pct}%)";
        }
        else if (stats is not null)
        {
            titleSuffix = $"{sorted.Count} / {stats.Total}";
        }

        NexusGroupBox.Draw(
            loc.Get("ui.main.tab.achievements.section.list"),
            () =>
            {
                if (catalog is null)
                {
                    ImGui.TextDisabled(loc.Get("ui.main.tab.achievements.catalog_loading"));
                    ImGui.SameLine();
                    NexusLoadingSpinner.Draw(14f);
                }

                NexusTable.Draw(
                    "##ach_table",
                    new[]
                    {
                        new NexusTableColumn(loc.Get("ui.main.tab.achievements.col.date"),   Width: 90f),
                        new NexusTableColumn(loc.Get("ui.main.tab.achievements.col.name")),
                        new NexusTableColumn(loc.Get("ui.main.tab.achievements.col.points"), Width: 60f),
                        new NexusTableColumn(loc.Get("ui.main.tab.achievements.col.patch"),  Width: 50f),
                    },
                    sorted,
                    entry =>
                    {
                        var meta = catalog is not null && catalog.TryGetValue(entry.Id, out var m) ? m : null;
                        ImGui.TableNextColumn();
                        NexusTable.CellText(
                            entry.AchievedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—",
                            ImGuiColors.DalamudGrey);
                        ImGui.TableNextColumn();
                        NexusTable.CellText(meta?.Name ?? $"#{entry.Id}");
                        ImGui.TableNextColumn();
                        if (meta is { Points: { } pts and > 0 })
                            NexusTable.CellText(pts.ToString(), ImGuiColors.DalamudYellow);
                        else
                            ImGui.TextDisabled("—");
                        ImGui.TableNextColumn();
                        if (meta?.Patch is { } patch)
                            NexusTable.CellText(patch, ImGuiColors.DalamudGrey3);
                        else
                            ImGui.TextDisabled("—");
                    },
                    rowHeight: RowHeight);
            },
            titleSuffix: titleSuffix);
    }
}
