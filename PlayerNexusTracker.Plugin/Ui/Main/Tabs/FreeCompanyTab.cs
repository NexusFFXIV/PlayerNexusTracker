using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using NexusKit.Core;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.PluginBridge.Adapters.Lifestream;
using NexusKit.Ui.Widgets;
using PlayerNexusTracker.Ui.Main;
using FreeCompanyModel = NexusKit.Modules.ExternalData.Models.FreeCompany;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class FreeCompanyTab
{
    public static void Draw(Player? player, ObservedPlayer observed,
                            IReadOnlyList<FreeCompanyModel>? fcCandidates,
                            IGameDataLookups lookups, ILocalizer loc,
                            ILifestreamAdapter lifestream,
                            ILocalPlayerContext localPlayer)
    {
        // Strong match: profile-linked FC Lodestone id resolved a catalog row.
        if (player?.FreeCompany is { } fc)
        {
            DrawHeader(fc);
            NexusGroupBox.DrawColumns("##fc_grid",
                () => DrawDetails(fc, lookups, loc),
                HasEstateContent(fc) ? () => DrawEstate(fc, lookups, loc, lifestream, localPlayer) : null);
            DrawFocus(fc, loc);
            return;
        }

        // Weak match: live observation has a tag and we found one-or-more catalog
        // rows for (tag, home_world_id). Always show the ambiguity warning, even
        // when there's only one candidate — tag+world is *not* a unique key.
        if (fcCandidates is { Count: > 0 } && !string.IsNullOrEmpty(observed.CompanyTag))
        {
            var worldName = lookups.GetWorldName(observed.HomeWorldId) ?? $"#{observed.HomeWorldId}";
            DrawAmbiguousWarning(observed.CompanyTag!, worldName, loc);
            DrawCandidates(fcCandidates, lookups, loc);
            return;
        }

        // Tag known but no candidates yet (FC catalog row not fetched) —
        // distinguish from "not in an FC" so the user knows enrichment is the
        // next step, not that the player left their FC.
        if (!string.IsNullOrEmpty(observed.CompanyTag))
        {
            ImGui.TextWrapped(string.Format(
                loc.Get("ui.main.tab.fc.profile_pending"),
                observed.CompanyTag));
            return;
        }

        // No profile yet, no live tag → reuse the Lodestone-pending placeholder.
        if (LodestoneStatusBadge.Draw(player, observed, loc)) return;

        // Profile present, profile says "no FC", live observation has no tag —
        // authoritative empty state.
        ImGui.TextDisabled(loc.Get("ui.main.tab.fc.not_in_fc"));
    }

    private static bool HasEstateContent(FreeCompanyModel fc) =>
        fc.Estate is { } est && (!string.IsNullOrEmpty(est.Name)
                                 || !string.IsNullOrEmpty(est.Greeting)
                                 || est.DistrictTerritoryId is not null
                                 || est.Ward is not null
                                 || est.PlotNumber is not null);

    private static void DrawHeader(FreeCompanyModel fc)
    {
        var heading = string.IsNullOrEmpty(fc.Tag) ? fc.Name : $"{fc.Name} «{fc.Tag}»";
        ImGui.TextUnformatted(heading);
        if (!string.IsNullOrEmpty(fc.Slogan))
            ImGui.TextWrapped(fc.Slogan);
    }

    private static void DrawAmbiguousWarning(string tag, string worldName, ILocalizer loc)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, string.Format(
            loc.Get("ui.main.tab.fc.ambiguous.warning"), tag, worldName));
        ImGui.TextWrapped(loc.Get("ui.main.tab.fc.ambiguous.candidates_header"));
        ImGui.Spacing();
    }

    private static void DrawCandidates(IReadOnlyList<FreeCompanyModel> fcs,
                                       IGameDataLookups lookups, ILocalizer loc)
    {
        var membersFormat = loc.Get("ui.main.tab.fc.candidate.members_format");
        foreach (var fc in fcs)
        {
            var heading = string.IsNullOrEmpty(fc.Tag) ? fc.Name : $"«{fc.Tag}» {fc.Name}";
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(heading);

            var parts = new List<string>(3);
            if (fc.WorldId is { } wid)
            {
                var world = lookups.GetWorldName(wid) ?? $"#{wid}";
                parts.Add(world);
            }
            if (fc.ActiveMemberCount is { } members)
                parts.Add(string.Format(membersFormat, members));
            if (fc.FormedAt is { } formed)
                parts.Add(formed.ToString("yyyy-MM-dd"));

            if (parts.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, " · " + string.Join(" · ", parts));
            }
        }
    }

    private static void DrawDetails(FreeCompanyModel fc, IGameDataLookups lookups, ILocalizer loc)
    {
        NexusGroupBox.Draw(loc.Get("ui.main.tab.fc.section.details"), () =>
        {
            var world = fc.WorldId is { } wid ? lookups.GetWorldName(wid) ?? $"#{wid}" : null;
            var gc = fc.GrandCompanyId is { } gcid ? lookups.GetGrandCompanyName(gcid) ?? $"#{gcid}" : null;

            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.world"), world);
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.gc"), gc);
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.rank"), fc.Rank?.ToString());
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.active_members"), fc.ActiveMemberCount?.ToString());
            NexusKeyValueRow.Draw(
                loc.Get("ui.main.tab.fc.details.active_state"),
                loc.Get($"fc.activestate.{fc.ActiveState.ToString().ToLowerInvariant()}"));
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.recruitment"), fc.RecruitmentOpen switch
            {
                true => loc.Get("ui.main.tab.fc.details.recruitment.open"),
                false => loc.Get("ui.main.tab.fc.details.recruitment.closed"),
                null => null,
            });
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.details.formed"), fc.FormedAt?.ToString("yyyy-MM-dd"));
        });
    }

    private static void DrawEstate(FreeCompanyModel fc, IGameDataLookups lookups,
                                   ILocalizer loc, ILifestreamAdapter lifestream,
                                   ILocalPlayerContext localPlayer)
    {
        if (fc.Estate is not { } est) return;

        // Lifestream travel becomes a header-row icon button on the Anwesen
        // section. The adapter's Preview returns a hint describing which of
        // the three command shapes (same-world / world-visit / cross-DC)
        // would fire right now — or null when travel isn't possible
        // (Lifestream unavailable, address incomplete, non-residential).
        // A non-null hint IS the "show the button" predicate; we no longer
        // duplicate the address-field checks here.
        var travelHint = lifestream.PreviewGotoFreeCompanyHouse(
            localPlayer.GetLocation(), est);

        NexusGroupBox.Draw(
            loc.Get("ui.main.tab.fc.section.estate"),
            drawContent: () =>
        {
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.estate.name"), est.Name);

            // District / ward / plot — resolved from the structured columns.
            // Locale-aware: GetTerritoryName picks the current UI language so
            // a DE client sees "Dorf des Nebels" even though the DB stores the
            // territory row id.
            string? districtName = null;
            if (est.DistrictTerritoryId is { } did)
                districtName = lookups.GetTerritoryName((ushort)did) ?? $"#{did}";
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.estate.district"), districtName);
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.estate.ward"), est.Ward?.ToString());
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.fc.estate.plot_number"), est.PlotNumber?.ToString());
            if (est.HouseSize is { } hs)
            {
                NexusKeyValueRow.Draw(
                    loc.Get("ui.main.tab.fc.estate.size"),
                    loc.Get($"ui.main.tab.fc.estate.size.{hs.ToString().ToLowerInvariant()}"));
            }
            if (est.IsSubdivision)
            {
                NexusKeyValueRow.Draw(
                    loc.Get("ui.main.tab.fc.estate.subdivision"),
                    loc.Get("ui.main.tab.fc.estate.subdivision.yes"));
            }

            if (!string.IsNullOrEmpty(est.Greeting))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(est.Greeting);
            }
        },
            headerRightAction: travelHint is { } hint
                ? (System.Action<System.Numerics.Vector2>)(size =>
                {
                    // Three-state rendering driven by the hint:
                    //   * null hint  → header-right-action is null,
                    //                  the slot isn't rendered at all
                    //                  (Lifestream unavailable / non-actionable).
                    //   * CanExecute=true  → tinted, enabled.
                    //   * CanExecute=false → tinted, BeginDisabled wraps the
                    //                        button (handled inside NexusIconButton);
                    //                        tooltip explains why it's greyed.
                    var tooltipText = hint.TooltipKey is { } tk
                        ? loc.Get(tk)
                        : loc.Get("ui.main.tab.freecompany.lifestream.travel.tooltip");
                    if (NexusIconButton.Draw(
                            FontAwesomeIcon.Home,
                            tooltipText,
                            hint,
                            size: size))
                    {
                        lifestream.TryGotoFreeCompanyHouse(localPlayer.GetLocation(), est);
                    }
                })
                : null);
    }

    private static void DrawFocus(FreeCompanyModel fc, ILocalizer loc)
    {
        if (fc.Focus is not { } f) return;

        NexusGroupBox.Draw(loc.Get("ui.main.tab.fc.section.focus"), () =>
        {
            var flags = new (bool On, string LabelKey)[]
            {
                (f.RolePlay,   "ui.main.tab.fc.focus.roleplay"),
                (f.Leveling,   "ui.main.tab.fc.focus.leveling"),
                (f.Casual,     "ui.main.tab.fc.focus.casual"),
                (f.Hardcore,   "ui.main.tab.fc.focus.hardcore"),
                (f.Dungeons,   "ui.main.tab.fc.focus.dungeons"),
                (f.Guildhests, "ui.main.tab.fc.focus.guildhests"),
                (f.Trials,     "ui.main.tab.fc.focus.trials"),
                (f.Raids,      "ui.main.tab.fc.focus.raids"),
                (f.PvP,        "ui.main.tab.fc.focus.pvp"),
            };

            var first = true;
            foreach (var (on, key) in flags)
            {
                if (!on) continue;
                if (!first) ImGui.SameLine();
                first = false;
                ImGui.TextColored(ImGuiColors.ParsedGreen, $"● {loc.Get(key)}");
            }
            if (first) ImGui.TextDisabled(loc.Get("ui.main.tab.fc.focus.empty"));
        });
    }
}
