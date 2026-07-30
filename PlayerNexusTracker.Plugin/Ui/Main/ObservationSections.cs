using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Core.Maps;
using NexusKit.GameData;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Renderers for the live-observation + session-statistics blocks. Shared between
/// the observation-only fallback view (no Lodestone data yet) and the Summary tab
/// (where they sit above the Lodestone-sourced profile rows) so a character looks
/// the same whether or not enrichment has landed.
/// </summary>
internal static class ObservationSections
{
    /// <summary>Renders the live-observation rows. <paramref name="position"/> and
    /// <paramref name="onMarkPosition"/> are the only genuinely live pieces — the
    /// rest of <paramref name="observed"/> is the last persisted snapshot, which for
    /// most rows is history. Pass a null position for anyone out of range; the row
    /// then states that instead of offering an action that can't work.</summary>
    public static void DrawLive(ObservedPlayer observed, IGameDataLookups lookups, ILocalizer loc,
                                MapPosition? position = null, Action? onMarkPosition = null)
    {
        // Race byte 0 == "no customize snapshot ever captured for this row"
        // (slim-projection sentinel) — skip the line entirely so we don't
        // mislabel such rows as the Lumina default race.
        if (observed.Race != 0)
        {
            var feminine = observed.Gender == 1;
            var raceName = lookups.GetRaceName(observed.Race, feminine) ?? $"#{observed.Race}";
            var genderLabel = loc.Get(feminine ? "ui.main.gender.female" : "ui.main.gender.male");
            NexusKeyValueRow.Draw(loc.Get("ui.main.observation.race_gender"),
                $"{raceName} · {genderLabel}");
        }

        var jobName = lookups.GetClassJobName(observed.ClassJobId) ?? $"#{observed.ClassJobId}";
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.active_job"),
            string.Format(loc.Get("ui.main.observation.active_job_value"), jobName, observed.Level));
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.home_world"), observed.HomeWorld);
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.fc_tag"), observed.CompanyTag);

        if (observed.CurrentMountId is { } mountId)
            NexusKeyValueRow.Draw(loc.Get("ui.main.observation.mounted"),
                lookups.GetMountName(mountId) ?? $"#{mountId}");
        if (observed.CurrentMinionId is { } minionId)
            NexusKeyValueRow.Draw(loc.Get("ui.main.observation.minion"),
                lookups.GetMinionName(minionId) ?? $"#{minionId}");

        DrawPosition(lookups, loc, position, onMarkPosition);
    }

    /// <summary>Where the player is standing right now, plus an inline shortcut to
    /// flag it on the map. Always rendered — "not in range" is itself the answer to
    /// whether this character is nearby, which is what the Live block is for.</summary>
    private static void DrawPosition(IGameDataLookups lookups, ILocalizer loc,
                                     MapPosition? position, Action? onMarkPosition)
    {
        var label = loc.Get("ui.main.observation.position");

        if (position is not { } pos)
        {
            NexusKeyValueRow.Draw(label, () => ImGui.TextColored(ImGuiColors.DalamudGrey,
                loc.Get("ui.main.observation.position.out_of_range")));
            return;
        }

        // One decimal, matching the coordinate format the game's own map links and
        // /coord output use, so the numbers are directly comparable.
        var zone = lookups.GetTerritoryDisplayName(pos.TerritoryId) ?? $"#{pos.TerritoryId}";
        var text = string.Format(loc.Get("ui.main.observation.position.value"),
            zone,
            pos.MapX.ToString("0.0", CultureInfo.CurrentCulture),
            pos.MapY.ToString("0.0", CultureInfo.CurrentCulture));

        if (onMarkPosition is null)
        {
            NexusKeyValueRow.Draw(label, text);
            return;
        }

        NexusKeyValueRow.DrawWithControl(label, () =>
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(text);
            ImGui.SameLine();
            NexusIconButton.Draw(FontAwesomeIcon.MapMarkerAlt,
                loc.Get("ui.main.observation.position.mark"),
                onMarkPosition,
                size: new Vector2(24f, ImGui.GetFrameHeight()));
        });
    }

    public static void DrawSessionStats(ObservedPlayer observed, ILocalizer loc, int? seenCount)
    {
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.first_seen"),
            observed.FirstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.last_seen"),
            observed.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        // SeenCount is now an async lookup against the encounter tracker; the
        // caller threads in MainWindowState.CurrentEncounterCount which is
        // null while loading and falls back to "—".
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.seen_count"),
            seenCount?.ToString() ?? "—");
    }
}
