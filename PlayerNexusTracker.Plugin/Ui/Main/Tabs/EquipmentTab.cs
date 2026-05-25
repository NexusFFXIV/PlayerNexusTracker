using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class EquipmentTab
{
    public static void Draw(Player? player, ObservedPlayer observed,
                            IGameDataLookups lookups, ILocalizer loc)
    {
        if (LodestoneStatusBadge.Draw(player, observed, loc)) return;

        if (player.Gear is not { Count: > 0 } gear)
        {
            ImGui.TextDisabled(loc.Get("ui.main.tab.equipment.empty"));
            return;
        }

        var sorted = gear.OrderBy(g => g.SlotIndex).ToList();

        NexusTable.Draw(
            "##equipment",
            new[]
            {
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.slot"),    Width: 110f),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.item")),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.ilvl"),    Width: 50f),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.glamour")),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.materia")),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.colors"),  Width: 80f),
                new NexusTableColumn(loc.Get("ui.main.tab.equipment.col.creator")),
            },
            sorted,
            slot =>
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.DalamudGrey, SlotName(slot.SlotIndex, loc));

                ImGui.TableNextColumn();
                NexusTable.CellText(lookups.GetItemName(slot.ItemId) ?? $"#{slot.ItemId}");
                if (slot.IsHq)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "[HQ]");
                }

                ImGui.TableNextColumn();
                if (slot.ItemLevel is { } ilvl) ImGui.TextUnformatted(ilvl.ToString());
                else ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                if (slot.GlamourItemId is { } gid)
                    NexusTable.CellText(lookups.GetItemName(gid) ?? $"#{gid}");
                else
                    ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                if (slot.Materia.Count > 0)
                    NexusTable.CellText(string.Join(", ", slot.Materia));
                else
                    ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                if (slot.Colors.Count > 0)
                    NexusTable.CellText(string.Join(", ", slot.Colors));
                else
                    ImGui.TextDisabled("—");

                ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(slot.CreatorName))
                    NexusTable.CellText(slot.CreatorName);
                else
                    ImGui.TextDisabled("—");
            });
    }

    /// <summary>Localized slot name for the Lodestone gear-slot index. The 13
    /// indices follow the engine's gear-slot order (mainhand → offhand → head →
    /// body → hands → legs → feet → earrings → necklace → bracelets → ring 1 →
    /// ring 2 → soul crystal). Missing localization falls back to "#N" so an
    /// out-of-range slot still renders something visible instead of a blank.</summary>
    private static string SlotName(int slotIndex, ILocalizer loc)
    {
        if (slotIndex is < 0 or > 12) return $"#{slotIndex}";
        return loc.Get($"ui.main.tab.equipment.slot.{slotIndex}");
    }
}
