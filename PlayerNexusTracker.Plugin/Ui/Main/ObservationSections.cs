using NexusKit.Core.Localization;
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
    public static void DrawLive(ObservedPlayer observed, IGameDataLookups lookups, ILocalizer loc)
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
