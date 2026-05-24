using Dalamud.Bindings.ImGui;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class ClassJobsTab
{
    public static void Draw(Player? player, ObservedPlayer observed,
                            IGameDataLookups lookups, ILocalizer loc)
    {
        if (player is null) { LodestonePlaceholder.Draw(observed, loc); return; }

        if (player.ClassJobs is not { Count: > 0 } jobs)
        {
            ImGui.TextDisabled(loc.Get("ui.main.tab.classjobs.empty"));
            return;
        }

        // Group by role first (tank → healer → DPS triplet → crafter →
        // gatherer → unknown via JobRoleExtensions.ToSortOrder), then by
        // level desc within the group, then alphabetically by abbreviation
        // as the deterministic tiebreaker. Mirrors the in-game class-job
        // UI's grouped layout.
        var ordered = jobs
            .OrderBy(j => lookups.GetClassJobRole(j.ClassJobId).ToSortOrder())
            .ThenByDescending(j => j.Level)
            .ThenBy(j => lookups.GetClassJobAbbreviation(j.ClassJobId) ?? "")
            .ToList();

        NexusTable.Draw(
            "##classjobs",
            new[]
            {
                new NexusTableColumn(loc.Get("ui.main.tab.classjobs.col.abbr"),  Width: 60f),
                new NexusTableColumn(loc.Get("ui.main.tab.classjobs.col.job")),
                new NexusTableColumn(loc.Get("ui.main.tab.classjobs.col.level"), Width: 80f),
            },
            ordered,
            job =>
            {
                ImGui.TableNextColumn();
                // Abbreviation cell tinted by role color (tank blue, healer
                // green, etc.) — matches the Encounters tab so the same job
                // reads the same color across the detail panel.
                NexusTable.CellText(
                    lookups.GetClassJobAbbreviation(job.ClassJobId) ?? "—",
                    lookups.GetClassJobRole(job.ClassJobId).ToRoleColor());
                ImGui.TableNextColumn();
                NexusTable.CellText(lookups.GetClassJobName(job.ClassJobId) ?? $"#{job.ClassJobId}");
                ImGui.TableNextColumn();
                NexusTable.CellText(job.Level.ToString());
            });
    }

}
