using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class SummaryTab
{
    public static void Draw(Player? player, ObservedPlayer observed,
                            IGameDataLookups lookups, ILocalizer loc,
                            IReadOnlyList<PlayerHistoryEntry>? history = null,
                            int? mountTotal = null,
                            int? minionTotal = null,
                            int? achievementTotal = null,
                            int? seenCount = null)
    {
        // Top-of-tab status banner; self-guarding (no-op when player is loaded).
        LodestoneStatusBadge.Draw(player, observed, loc);

        // Stat header (gear score / collections) is entirely Lodestone-derived —
        // skip when enrichment hasn't landed, so the user just sees the
        // observation grid instead of a row of "—" cards.
        if (player is not null)
        {
            DrawStatHeader(player, loc, mountTotal, minionTotal, achievementTotal);
            ImGui.Dummy(new Vector2(0, 4f));
        }
        DrawTwoColumnGrid(player, observed, lookups, loc, history, seenCount);
        if (player is not null) DrawBio(player, loc);
    }

    private static void DrawTwoColumnGrid(Player? player, ObservedPlayer observed,
                                          IGameDataLookups lookups, ILocalizer loc,
                                          IReadOnlyList<PlayerHistoryEntry>? history,
                                          int? seenCount)
    {
        // Each DrawColumns call is its own table row; both columns inside a call
        // are guaranteed to start at the same y. Live/Stats always render (they
        // are observation-only); the Lodestone-backed Profile/GC row is dropped
        // entirely until enrichment lands — the top-of-tab banner already
        // explains why those sections are missing.
        NexusGroupBox.DrawColumns("##summary_grid_live",
            () => NexusGroupBox.Draw(loc.Get("ui.main.observation.section.live"),
                () => ObservationSections.DrawLive(observed, lookups, loc)),
            () => NexusGroupBox.Draw(loc.Get("ui.main.observation.section.stats"),
                () => ObservationSections.DrawSessionStats(observed, loc, seenCount)));

        if (player is null) return;

        NexusGroupBox.DrawColumns("##summary_grid_profile",
            () => NexusGroupBox.Draw(loc.Get("ui.main.tab.summary.section.profile"),
                () => DrawProfileRows(player, loc, history)),
            HasGrandCompany(player)
                ? () => NexusGroupBox.Draw(loc.Get("ui.main.tab.summary.section.gc"),
                    () => DrawGrandCompanyRows(player, lookups, loc))
                : null);
    }

    private static void DrawStatHeader(Player player, ILocalizer loc,
                                       int? mountTotal, int? minionTotal, int? achievementTotal)
    {
        // Four equal-width cards on one row via the shared grid widget. The widget
        // hands each cell its own column width so the cards size naturally.
        NexusGroupBox.DrawGrid("##summary_stats", 4,
            () => new NexusStatCard
            {
                Label = loc.Get("ui.main.tab.summary.stat.gear_score"),
                Value = player.GearScore?.ToString() ?? "—",
            }.Draw(ImGui.GetContentRegionAvail().X),
            () => DrawCollectionStatCard(loc.Get("ui.main.tab.summary.stat.mounts"),
                player.Collections?.Mounts, mountTotal),
            () => DrawCollectionStatCard(loc.Get("ui.main.tab.summary.stat.minions"),
                player.Collections?.Minions, minionTotal),
            () => DrawCollectionStatCard(loc.Get("ui.main.tab.summary.stat.achievements"),
                player.Collections?.Achievements, achievementTotal));
    }

    private static void DrawCollectionStatCard(string label, PlayerCollectionStats? stats,
                                               int? fallbackTotal)
    {
        var width = ImGui.GetContentRegionAvail().X;

        // When stats aren't loaded yet, fall back to 0 / catalog-total so the card
        // never reads as "—" once the global catalogs have populated.
        var count = stats?.Count ?? 0;
        var total = stats?.Total ?? fallbackTotal ?? 0;
        var suffix = total == 0 ? null : $"({count * 100 / total}%)";

        new NexusStatCard
        {
            Label = label,
            LabelSuffix = suffix,
            Value = total == 0 ? "—" : $"{count} / {total}",
        }.Draw(width);
    }

    private static void DrawProfileRows(Player player, ILocalizer loc,
                                        IReadOnlyList<PlayerHistoryEntry>? history)
    {
        if (player.Profile is not { } p)
        {
            ImGui.TextDisabled("—");
            return;
        }

        NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.profile.nameday"), p.Nameday);
        NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.profile.guardian"), p.GuardianDeity);
        NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.profile.starting_city"), p.StartingCity);
        NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.profile.fc"), () =>
        {
            if (string.IsNullOrEmpty(p.FreeCompanyLodestoneId))
            {
                ImGui.TextDisabled("—");
            }
            else if (player.FreeCompany is { } fc)
            {
                var label = string.IsNullOrEmpty(fc.Tag) ? fc.Name : $"«{fc.Tag}» {fc.Name}";
                ImGui.TextUnformatted(label);
            }
            else
            {
                ImGui.TextDisabled("…");
            }
            HistoryHint.Draw(history, PlayerHistoryKind.FreeCompanyChange, loc);
        });
    }

    private static bool HasGrandCompany(Player player) =>
        player.Profile is { GrandCompanyId: not null };

    private static void DrawGrandCompanyRows(Player player, IGameDataLookups lookups, ILocalizer loc)
    {
        if (player.Profile is not { } p) return;
        if (p.GrandCompanyId is not { } gcId) return;

        var gcName = lookups.GetGrandCompanyName(gcId) ?? $"#{gcId}";
        NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.gc.allegiance"), gcName);

        if (p.GrandCompanyRankId is { } rankId)
        {
            var feminine = p.GrandCompanyRankIsFeminine ?? false;
            var rankName = lookups.GetGrandCompanyRankName(gcId, rankId, feminine)
                           ?? $"#{rankId} ({(feminine ? "F" : "M")})";
            NexusKeyValueRow.Draw(loc.Get("ui.main.tab.summary.gc.rank"), rankName);
        }
    }

    private static void DrawBio(Player player, ILocalizer loc)
    {
        var bio = player.Profile?.Bio;
        if (string.IsNullOrWhiteSpace(bio)) return;

        ImGui.Dummy(new Vector2(0, 4f));
        ImGui.TextColored(ImGuiColors.DalamudYellow, loc.Get("ui.main.tab.summary.profile.bio"));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 2f));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(bio);
        ImGui.PopTextWrapPos();
    }
}
