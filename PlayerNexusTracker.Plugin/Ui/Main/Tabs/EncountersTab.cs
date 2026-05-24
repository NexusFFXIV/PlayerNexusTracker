using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Settings.Filters;

namespace PlayerNexusTracker.Ui.Main.Tabs;

/// <summary>
/// Territory-bounded encounter history for the selected character. Sourced
/// from <see cref="IInternalDataEncounterTracker"/>; one row per
/// <c>player_encounter</c> entry, joined with its parent encounter so we
/// can render the zone the local player was in at the time.
/// </summary>
internal static class EncountersTab
{
    public static void Draw(IReadOnlyList<EncounterEntry>? entries,
                            uint? currentLocalWorldId,
                            IGameDataLookups lookups, ILocalizer loc,
                            EncountersFilterPreferences filters)
    {
        // Lazy-load on first frame so the persisted choice is visible from
        // the user's first paint of the tab.
        filters.EnsureLoaded();

        if (entries is null)
        {
            ImGui.Spacing();
            NexusLoadingSpinner.Draw(20f);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.encounters.loading"));
            return;
        }

        if (entries.Count == 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudGrey, loc.Get("ui.main.tab.encounters.empty"));
            ImGui.PopTextWrapPos();
            return;
        }

        DrawFilterBar(loc, filters);

        var filtered = ApplyTimeFilter(entries, filters.TimeRange);
        filtered = ApplyZoneFilter(filtered, lookups, filters.ZoneFilter);
        if (filtered.Count == 0)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                loc.Get("ui.main.tab.encounters.filter.no_results"));
            ImGui.PopTextWrapPos();
            return;
        }

        NexusTable.Draw(
            "##encounters",
            new[]
            {
                new NexusTableColumn(loc.Get("ui.main.tab.encounters.col.time"),     Width: 135f),
                new NexusTableColumn(loc.Get("ui.main.tab.encounters.col.duration"), Width: 80f),
                new NexusTableColumn(loc.Get("ui.main.tab.encounters.col.zone")),
                new NexusTableColumn(loc.Get("ui.main.tab.encounters.col.job"),      Width: 60f),
                new NexusTableColumn(loc.Get("ui.main.tab.encounters.col.level"),    Width: 60f),
            },
            filtered,
            entry =>
            {
                ImGui.TableNextColumn();
                NexusTable.CellText(entry.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    ImGuiColors.DalamudGrey);

                ImGui.TableNextColumn();
                NexusTable.CellText(loc.FormatTimeSpan(entry.LastSeenAt - entry.FirstSeenAt),
                    ImGuiColors.DalamudGrey);

                ImGui.TableNextColumn();
                // TerritoryTypeId == 0 is the migration sentinel for "legacy
                // pre-encounter-tracker row" — show an em dash so those rows
                // don't yell "#0" at the user. Otherwise: short form (zone
                // only) when the encounter happened on the world the local
                // player is currently on (we're back where it took place);
                // extended form "DC · Server · Zone" when the worlds don't
                // match so the user sees this was on a different world from
                // where they are now. Legacy rows (entry.WorldId NULL) or
                // unknown local world (between zones / not logged in) fall
                // back to the short form — no comparison is possible.
                string zoneText;
                Vector4? zoneColor = null;
                if (entry.TerritoryTypeId == 0)
                {
                    zoneText = "—";
                }
                else
                {
                    // For instanced content (Dungeons / Trials / Raids / PvP /
                    // Eureka / …) the TerritoryType is the underlying map and
                    // its PlaceName is the map's geography — the duty itself
                    // lives on TerritoryType.ContentFinderCondition.
                    // GetInstancedContentName filters out CFC entries that
                    // only exist for city/housing/roulette plumbing (Limsa,
                    // Old Sharlayan, …) so it returns non-null only for
                    // actual instanced content. We prefix it with the
                    // localized ContentType name when we can resolve it
                    // (e.g. "Prüfung: …", "Schlachtzug: …", "Verließ: …"),
                    // falling back to the generic "Inhalt: " label when no
                    // ContentType is linked.
                    string zoneOnly;
                    var instancedName = lookups.GetInstancedContentName(entry.TerritoryTypeId);
                    if (!string.IsNullOrEmpty(instancedName))
                    {
                        var contentType = lookups.GetContentTypeName(entry.TerritoryTypeId);
                        var prefix = string.IsNullOrEmpty(contentType)
                            ? loc.Get("ui.main.tab.encounters.zone.duty_prefix")
                            : $"{contentType}: ";
                        zoneOnly = $"{prefix}{instancedName}";
                        zoneColor = GetContentTypeColor(lookups.GetContentTypeRowId(entry.TerritoryTypeId));
                    }
                    else
                    {
                        // GetTerritoryDisplayName combines PlaceNameZone with
                        // PlaceName so city sub-districts like Limsa's "Obere
                        // Decks" surface as "Limsa Lominsa - Obere Decks"
                        // instead of just "Obere Decks".
                        zoneOnly = lookups.GetTerritoryDisplayName(entry.TerritoryTypeId)
                                   ?? $"#{entry.TerritoryTypeId}";
                    }
                    var showFullPath = entry.WorldId is { } encounterWorld
                                       && currentLocalWorldId is { } localWorld
                                       && encounterWorld != localWorld;
                    if (showFullPath)
                    {
                        var dc = lookups.GetDataCenterNameByWorldId(entry.WorldId!.Value);
                        var world = lookups.GetWorldName(entry.WorldId!.Value);
                        zoneText = string.IsNullOrEmpty(dc) || string.IsNullOrEmpty(world)
                            ? zoneOnly
                            : $"{dc} · {world} · {zoneOnly}";
                    }
                    else
                    {
                        zoneText = zoneOnly;
                    }
                }
                NexusTable.CellText(zoneText, zoneColor);

                ImGui.TableNextColumn();
                NexusTable.CellText(
                    lookups.GetClassJobAbbreviation(entry.JobId) ?? "—",
                    lookups.GetClassJobRole(entry.JobId).ToRoleColor());

                ImGui.TableNextColumn();
                NexusTable.CellText(entry.Level.ToString());
            });
    }

    private static void DrawFilterBar(ILocalizer loc, EncountersFilterPreferences filters)
    {
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo(loc.Get("ui.main.tab.encounters.filter.time.label"),
                             TimeRangeLabel(filters.TimeRange, loc)))
        {
            foreach (var range in (EncounterTimeRange[])System.Enum.GetValues(typeof(EncounterTimeRange)))
            {
                var isSelected = range == filters.TimeRange;
                if (ImGui.Selectable(TimeRangeLabel(range, loc), isSelected))
                    filters.SetTimeRange(range);
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo(loc.Get("ui.main.tab.encounters.filter.zone.label"),
                             ZoneFilterLabel(filters.ZoneFilter, loc)))
        {
            foreach (var zf in (EncounterZoneFilter[])System.Enum.GetValues(typeof(EncounterZoneFilter)))
            {
                var isSelected = zf == filters.ZoneFilter;
                if (ImGui.Selectable(ZoneFilterLabel(zf, loc), isSelected))
                    filters.SetZoneFilter(zf);
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();
    }

    private static string TimeRangeLabel(EncounterTimeRange range, ILocalizer loc) => range switch
    {
        EncounterTimeRange.All    => loc.Get("ui.main.tab.encounters.filter.time.all"),
        EncounterTimeRange.Days7  => loc.Get("ui.main.tab.encounters.filter.time.7d"),
        EncounterTimeRange.Days30 => loc.Get("ui.main.tab.encounters.filter.time.30d"),
        EncounterTimeRange.Days90 => loc.Get("ui.main.tab.encounters.filter.time.90d"),
        _ => string.Empty,
    };

    private static IReadOnlyList<EncounterEntry> ApplyTimeFilter(
        IReadOnlyList<EncounterEntry> entries, EncounterTimeRange range)
    {
        if (range == EncounterTimeRange.All) return entries;
        var cutoff = System.DateTime.UtcNow - range switch
        {
            EncounterTimeRange.Days7  => System.TimeSpan.FromDays(7),
            EncounterTimeRange.Days30 => System.TimeSpan.FromDays(30),
            EncounterTimeRange.Days90 => System.TimeSpan.FromDays(90),
            _ => System.TimeSpan.Zero,
        };
        // Filter on FirstSeenAt (encounter start). Long-running sessions that
        // started before the cutoff drop out even if LastSeenAt is recent —
        // matches the user mental model of "encounters younger than X".
        return entries.Where(e => e.FirstSeenAt >= cutoff).ToList();
    }

    private static string ZoneFilterLabel(EncounterZoneFilter zf, ILocalizer loc) => zf switch
    {
        EncounterZoneFilter.All       => loc.Get("ui.main.tab.encounters.filter.zone.all"),
        EncounterZoneFilter.OpenWorld => loc.Get("ui.main.tab.encounters.filter.zone.open_world"),
        EncounterZoneFilter.AnyDuty   => loc.Get("ui.main.tab.encounters.filter.zone.any_duty"),
        EncounterZoneFilter.Dungeons  => loc.Get("ui.main.tab.encounters.filter.zone.dungeons"),
        EncounterZoneFilter.Trials    => loc.Get("ui.main.tab.encounters.filter.zone.trials"),
        EncounterZoneFilter.Raids     => loc.Get("ui.main.tab.encounters.filter.zone.raids"),
        EncounterZoneFilter.Pvp       => loc.Get("ui.main.tab.encounters.filter.zone.pvp"),
        EncounterZoneFilter.Field     => loc.Get("ui.main.tab.encounters.filter.zone.field"),
        EncounterZoneFilter.OtherDuty => loc.Get("ui.main.tab.encounters.filter.zone.other"),
        _ => string.Empty,
    };

    private static IReadOnlyList<EncounterEntry> ApplyZoneFilter(
        IReadOnlyList<EncounterEntry> entries, IGameDataLookups lookups, EncounterZoneFilter zf)
    {
        if (zf == EncounterZoneFilter.All) return entries;
        return entries.Where(e => MatchesZoneFilter(e, lookups, zf)).ToList();
    }

    private static bool MatchesZoneFilter(EncounterEntry e, IGameDataLookups lookups, EncounterZoneFilter zf)
    {
        // Legacy rows (TerritoryTypeId == 0) have no zone info — keep them
        // out of any non-default filter so they don't pollute the result.
        if (e.TerritoryTypeId == 0) return false;
        var isInstanced = !string.IsNullOrEmpty(lookups.GetInstancedContentName(e.TerritoryTypeId));
        switch (zf)
        {
            case EncounterZoneFilter.OpenWorld: return !isInstanced;
            case EncounterZoneFilter.AnyDuty:   return isInstanced;
        }
        if (!isInstanced) return false;
        var ct = lookups.GetContentTypeRowId(e.TerritoryTypeId);
        return zf switch
        {
            EncounterZoneFilter.Dungeons  => ct == 2,
            EncounterZoneFilter.Trials    => ct == 4,
            EncounterZoneFilter.Raids     => ct == 5,
            EncounterZoneFilter.Pvp       => ct == 6,
            EncounterZoneFilter.Field     => ct == 21 || ct == 26,
            EncounterZoneFilter.OtherDuty => ct is not (2 or 4 or 5 or 6 or 21 or 26),
            _ => true,
        };
    }

    // Per-ContentType color palette for the zone cell. Keys are
    // ContentType.RowId — language-independent so the mapping survives
    // client-language switches. Unknown ContentTypes fall back to the
    // table's default cell color (null).
    private static Vector4? GetContentTypeColor(uint? contentTypeRowId) => contentTypeRowId switch
    {
        2  => ImGuiColors.ParsedBlue,    // Dungeons
        4  => ImGuiColors.ParsedGold,    // Trials (incl. Guildhests)
        5  => ImGuiColors.ParsedPurple,  // Raids
        6  => ImGuiColors.DPSRed,        // PvP
        7  => ImGuiColors.HealerGreen,   // Quest Battles (story duties)
        21 => ImGuiColors.ParsedGreen,   // Disciples of the Land (Diadem)
        26 => ImGuiColors.ParsedGreen,   // Eureka / Bozja / Field Operations
        28 => ImGuiColors.ParsedPink,    // Variant Dungeons
        30 => ImGuiColors.ParsedPink,    // Criterion Dungeons
        _  => null,
    };
}
