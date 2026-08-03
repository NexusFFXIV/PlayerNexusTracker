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
    /// then states that instead of offering an action that can't work.
    /// <para><paramref name="searchComment"/> comes from the lazily-loaded
    /// <c>ObservedPlayerDetail</c>, so it is null for the first frames after a
    /// selection change as well as for everyone the user never examined.</para></summary>
    public static void DrawLive(ObservedPlayer observed, IGameDataLookups lookups, ILocalizer loc,
                                string? searchComment = null,
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
        DrawSearchComment(loc, searchComment);
        NexusKeyValueRow.Draw(loc.Get("ui.main.observation.fc_tag"), observed.CompanyTag);

        if (observed.CurrentMountId is { } mountId)
            NexusKeyValueRow.Draw(loc.Get("ui.main.observation.mounted"),
                lookups.GetMountName(mountId) ?? $"#{mountId}");
        if (observed.CurrentMinionId is { } minionId)
            NexusKeyValueRow.Draw(loc.Get("ui.main.observation.minion"),
                lookups.GetMinionName(minionId) ?? $"#{minionId}");

        DrawPosition(lookups, loc, position, onMarkPosition);
    }

    /// <summary>The character's own search comment, as captured the last time the
    /// user examined them. Always rendered so the row doesn't jump around as
    /// characters with and without one are selected — an em-dash reads as "nothing
    /// on file", same as every other optional row here.
    /// <para>Search comments run to 192 bytes and may contain line breaks, while
    /// the value column is whatever is left of one half of the Summary grid after
    /// a 140px label. So: newlines flattened, text clipped to the available width
    /// with an ellipsis, full text on hover. <c>NexusTable.CellText</c> does the
    /// same job for table cells but measures against <c>GetColumnWidth()</c>, which
    /// here would report the grid column rather than the space left beside the
    /// label.</para></summary>
    private static void DrawSearchComment(ILocalizer loc, string? searchComment)
    {
        var label = loc.Get("ui.main.observation.search_comment");
        if (string.IsNullOrWhiteSpace(searchComment))
        {
            NexusKeyValueRow.Draw(label, (string?)null);
            return;
        }

        var full = searchComment.Trim();
        // Line breaks would blow the row height apart; a middle dot keeps the
        // structure of a multi-line comment visible on one line.
        var flat = full.ReplaceLineEndings(" · ");

        NexusKeyValueRow.Draw(label, () =>
        {
            var avail = ImGui.GetContentRegionAvail().X;
            var text = Ellipsize(flat, avail);
            ImGui.TextUnformatted(text);
            // Hover the value, not the row: the label column is shared with every
            // other row and a tooltip there would be a surprise.
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(full);
        });
    }

    /// <summary>Trims <paramref name="text"/> until it fits <paramref name="maxWidth"/>
    /// pixels, appending an ellipsis. Returns the input untouched when it already
    /// fits or when the width isn't known yet (first frame of a freshly opened
    /// panel reports zero).</summary>
    private static string Ellipsize(string text, float maxWidth)
    {
        if (maxWidth <= 0f) return text;
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        const string Ellipsis = "…";
        var ellipsisWidth = ImGui.CalcTextSize(Ellipsis).X;
        var budget = maxWidth - ellipsisWidth;
        if (budget <= 0f) return Ellipsis;

        // Binary search on the cut point — a per-character walk would call
        // CalcTextSize up to 192 times per frame for a long comment.
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X <= budget) low = mid;
            else high = mid - 1;
        }
        return text[..low].TrimEnd() + Ellipsis;
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
