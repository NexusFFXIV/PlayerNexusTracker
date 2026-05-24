using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData;
using NexusKit.Modules.ExternalData.Models;
using FreeCompany = NexusKit.Modules.ExternalData.Models.FreeCompany;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.Abstractions;

namespace PlayerNexusTracker.Ui;

/// <summary>
/// Manual exploration surface for the plugin's services. One tab per subsystem;
/// extend with more <c>ImGui.BeginTabItem</c> blocks as more areas need testing.
/// </summary>
public sealed class DebugWindow : NexusWindow
{
    private readonly IExternalDataService mExternalData;
    private readonly IGameDataLookups mGameDataLookups;
    private readonly IGameDataResolver mGameDataResolver;
    private readonly ISheetsProvider mSheets;

    private string mLodestoneIdInput = string.Empty;
    private bool mIncludeProfile;
    private bool mIncludeMounts;
    private bool mIncludeMinions;
    private bool mIncludeAchievements;
    private bool mIncludeItems;
    private bool mIncludeClassJobs;
    private bool mIncludeFreeCompany;
    private bool mIncludeGear;
    private string mSearchName = string.Empty;
    private string mSearchWorld = string.Empty;
    private string mCatalogIdInput = string.Empty;
    private string mFreeCompanyIdInput = string.Empty;

    private string mGdRowIdInput = string.Empty;
    private string mGdNameInput = string.Empty;
    private string mGdDcIdInput = string.Empty;

    /// <summary>0 = default (provider's CurrentLanguage), 1..4 = en/ja/de/fr explicit.</summary>
    private int mGdLanguageSelected;

    private string mOutput = string.Empty;
    private volatile bool mBusy;

    public DebugWindow(
        ISettingsStore store,
        IExternalDataService externalData,
        IGameDataLookups gameDataLookups,
        IGameDataResolver gameDataResolver,
        ISheetsProvider sheets,
        IWindowManager windows)
        : base(
            "PlayerNexusTracker Debug###PNT_Debug",
            store,
            windowManager: windows,
            showSettingsButton: false)
    {
        mExternalData = externalData;
        mGameDataLookups = gameDataLookups;
        mGameDataResolver = gameDataResolver;
        mSheets = sheets;
        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##pnt_debug_tabs"))
            return;

        if (ImGui.BeginTabItem("ExternalData"))
        {
            DrawExternalDataTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("GameData"))
        {
            DrawGameDataTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static readonly string[] GdLanguageLabels = { "(default)", "English", "Japanese", "German", "French" };

    private GameDataClientLanguage? SelectedLanguage =>
        mGdLanguageSelected switch
        {
            1 => GameDataClientLanguage.English,
            2 => GameDataClientLanguage.Japanese,
            3 => GameDataClientLanguage.German,
            4 => GameDataClientLanguage.French,
            _ => null,
        };

    private void DrawGameDataTab()
    {
        ImGui.TextDisabled($"Provider default language: {mSheets.CurrentLanguage}");

        Heading("Lookup by RowId");
        ImGui.InputText("RowId##gd_rowid", ref mGdRowIdInput, 16);
        ImGui.Combo("Language##gd_lang", ref mGdLanguageSelected, GdLanguageLabels, GdLanguageLabels.Length);

        if (ImGui.Button("World##gd_world")) RunGdWorld();
        ImGui.SameLine();
        if (ImGui.Button("ClassJob##gd_cj")) RunGdClassJob();
        ImGui.SameLine();
        if (ImGui.Button("Territory##gd_terr")) RunGdTerritory();
        ImGui.SameLine();
        if (ImGui.Button("Mount##gd_mount")) RunGdMount();
        ImGui.SameLine();
        if (ImGui.Button("Minion##gd_minion")) RunGdMinion();
        if (ImGui.Button("Title M##gd_title_m")) RunGdTitle(feminine: false);
        ImGui.SameLine();
        if (ImGui.Button("Title F##gd_title_f")) RunGdTitle(feminine: true);
        ImGui.SameLine();
        if (ImGui.Button("Race M##gd_race_m")) RunGdRace(feminine: false);
        ImGui.SameLine();
        if (ImGui.Button("Race F##gd_race_f")) RunGdRace(feminine: true);
        ImGui.SameLine();
        if (ImGui.Button("GrandCompany##gd_gc")) RunGdGrandCompany();

        Heading("Resolve name → RowId (uses Language above)");
        ImGui.InputText("Name##gd_name", ref mGdNameInput, 128);
        foreach (var kind in (GameDataKind[])Enum.GetValues(typeof(GameDataKind)))
        {
            if (ImGui.Button($"as {kind}##gd_resolve_{kind}")) RunGdResolve(kind);
            ImGui.SameLine();
        }
        ImGui.NewLine();

        Heading("Worlds in DataCenter");
        ImGui.InputText("DC RowId##gd_dc", ref mGdDcIdInput, 8);
        if (ImGui.Button("List##gd_dc_list")) RunGdWorldsInDataCenter();

        DrawOutputPanel();
    }

    private bool TryParseRowId(out uint rowId)
    {
        if (uint.TryParse(mGdRowIdInput, out rowId)) return true;
        mOutput = "RowId must be a non-negative integer.";
        return false;
    }

    private void RunGdWorld()
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetWorldName(id);
        var dc = mGameDataLookups.GetDataCenterNameByWorldId(id);
        mOutput = name is null
            ? $"World #{id}: <not found / private>"
            : $"World #{id}: {name}   (Datacenter: {dc ?? "—"})";
    }

    private void RunGdClassJob()
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetClassJobName(id, SelectedLanguage);
        var abbr = mGameDataLookups.GetClassJobAbbreviation(id);
        var role = mGameDataLookups.GetClassJobRole(id);
        mOutput = name is null
            ? $"ClassJob #{id}: <not found>"
            : $"ClassJob #{id}: {name}   abbr={abbr ?? "—"}   role={role}";
    }

    private void RunGdTerritory()
    {
        if (!TryParseRowId(out var id)) return;
        var ttid = (ushort)Math.Min(id, ushort.MaxValue);
        var territory = mGameDataLookups.GetTerritoryName(ttid, SelectedLanguage);
        var cfc = mGameDataLookups.GetContentFinderConditionName(ttid, SelectedLanguage);
        mOutput = territory is null
            ? $"Territory #{ttid}: <not found>"
            : $"Territory #{ttid}: {territory}   ContentFinder: {cfc ?? "—"}";
    }

    private void RunGdMount()
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetMountName(id, SelectedLanguage);
        mOutput = $"Mount #{id}: {name ?? "<not found>"}";
    }

    private void RunGdMinion()
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetMinionName(id, SelectedLanguage);
        mOutput = $"Minion #{id}: {name ?? "<not found>"}";
    }

    private void RunGdTitle(bool feminine)
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetTitleName(id, feminine, SelectedLanguage);
        mOutput = $"Title #{id} ({(feminine ? "F" : "M")}): {name ?? "<not found>"}";
    }

    private void RunGdRace(bool feminine)
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetRaceName(id, feminine, SelectedLanguage);
        mOutput = $"Race #{id} ({(feminine ? "F" : "M")}): {name ?? "<not found>"}";
    }

    private void RunGdGrandCompany()
    {
        if (!TryParseRowId(out var id)) return;
        var name = mGameDataLookups.GetGrandCompanyName(id, SelectedLanguage);
        mOutput = $"GrandCompany #{id}: {name ?? "<not found>"}";
    }

    private void RunGdResolve(GameDataKind kind)
    {
        if (string.IsNullOrWhiteSpace(mGdNameInput))
        {
            mOutput = "Name is required.";
            return;
        }
        var lang = SelectedLanguage ?? mSheets.CurrentLanguage;
        var id = mGameDataResolver.ResolveIdByName(mGdNameInput, kind, lang);
        mOutput = id is null
            ? $"{kind} '{mGdNameInput}' ({lang}): <not found>"
            : $"{kind} '{mGdNameInput}' ({lang}) → RowId {id}";
    }

    private void RunGdWorldsInDataCenter()
    {
        if (!uint.TryParse(mGdDcIdInput, out var dcId))
        {
            mOutput = "DC RowId must be a non-negative integer.";
            return;
        }
        var worlds = mGameDataLookups.GetWorldsInDataCenter(dcId);
        if (worlds.Count == 0)
        {
            mOutput = $"No public worlds in DataCenter #{dcId}.";
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"DataCenter #{dcId} — {worlds.Count} worlds:");
        foreach (var w in worlds.OrderBy(w => w.Name))
            sb.AppendLine($"  {w.RowId,4}  {w.Name}");
        mOutput = sb.ToString();
    }

    private void DrawExternalDataTab()
    {
        ImGui.BeginDisabled(mBusy);

        Heading("Player.GetAsync");
        ImGui.InputText("LodestoneId##player_get", ref mLodestoneIdInput, 32);
        ImGui.Checkbox("Profile", ref mIncludeProfile); ImGui.SameLine();
        ImGui.Checkbox("Mounts", ref mIncludeMounts); ImGui.SameLine();
        ImGui.Checkbox("Minions", ref mIncludeMinions); ImGui.SameLine();
        ImGui.Checkbox("Achievements", ref mIncludeAchievements); ImGui.SameLine();
        ImGui.Checkbox("Items", ref mIncludeItems);
        ImGui.Checkbox("ClassJobs", ref mIncludeClassJobs); ImGui.SameLine();
        ImGui.Checkbox("FreeCompany", ref mIncludeFreeCompany); ImGui.SameLine();
        ImGui.Checkbox("Gear", ref mIncludeGear);
        if (ImGui.Button("Get player"))
            RunGetPlayer();

        ImGui.Spacing();
        Heading("Player.SearchAsync");
        ImGui.InputText("Name##player_search_name", ref mSearchName, 64);
        ImGui.InputText("World##player_search_world", ref mSearchWorld, 32);
        if (ImGui.Button("Search"))
            RunSearch();

        ImGui.Spacing();
        Heading("Catalogs");
        if (ImGui.Button("List mounts")) RunListMounts(); ImGui.SameLine();
        if (ImGui.Button("List minions")) RunListMinions(); ImGui.SameLine();
        if (ImGui.Button("List achievements")) RunListAchievements(); ImGui.SameLine();
        if (ImGui.Button("List items")) RunListItems();

        ImGui.InputText("Id##cat_id", ref mCatalogIdInput, 16);
        if (ImGui.Button("Get mount")) RunGetCatalogEntry(CatalogKind.Mount); ImGui.SameLine();
        if (ImGui.Button("Get minion")) RunGetCatalogEntry(CatalogKind.Minion); ImGui.SameLine();
        if (ImGui.Button("Get achievement")) RunGetCatalogEntry(CatalogKind.Achievement); ImGui.SameLine();
        if (ImGui.Button("Get item")) RunGetCatalogEntry(CatalogKind.Item);

        ImGui.Spacing();
        Heading("FreeCompanies.GetAsync");
        ImGui.InputText("FC Lodestone Id##fc_id", ref mFreeCompanyIdInput, 32);
        if (ImGui.Button("Get free company"))
            RunGetFreeCompany();

        ImGui.EndDisabled();

        DrawOutputPanel();
    }

    private void DrawOutputPanel()
    {
        ImGui.Spacing();
        Heading(mBusy ? "Output (working...)" : "Output");
        var avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##output", avail, true))
        {
            ImGui.TextUnformatted(mOutput);
        }
        ImGui.EndChild();
    }

    private void RunGetPlayer()
    {
        if (!ulong.TryParse(mLodestoneIdInput, out var id))
        {
            mOutput = "LodestoneId must be a positive integer.";
            return;
        }
        var include = PlayerInclude.None;
        if (mIncludeProfile) include |= PlayerInclude.Profile;
        if (mIncludeMounts) include |= PlayerInclude.Mounts;
        if (mIncludeMinions) include |= PlayerInclude.Minions;
        if (mIncludeAchievements) include |= PlayerInclude.Achievements;
        if (mIncludeItems) include |= PlayerInclude.Items;
        if (mIncludeClassJobs) include |= PlayerInclude.ClassJobs;
        if (mIncludeFreeCompany) include |= PlayerInclude.FreeCompany;
        if (mIncludeGear) include |= PlayerInclude.Gear;

        RunAsync(async ct =>
        {
            var player = await mExternalData.Players.GetAsync(id, include, ct: ct).ConfigureAwait(false);
            return player is null ? "<null>" : FormatPlayer(player);
        });
    }

    private void RunSearch()
    {
        var name = mSearchName;
        var world = mSearchWorld;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
        {
            mOutput = "Name and world are required.";
            return;
        }

        RunAsync(async ct =>
        {
            var results = await mExternalData.Players.SearchAsync(name, world, ct).ConfigureAwait(false);
            if (results.Count == 0) return "no results";
            var sb = new StringBuilder();
            foreach (var r in results)
                sb.AppendLine($"{r.LodestoneId,-15}  {r.Name}  ({r.Server})");
            return sb.ToString();
        });
    }

    private void RunListMounts() => RunAsync(async ct =>
    {
        var list = await mExternalData.Mounts.ListAsync(ct).ConfigureAwait(false);
        return $"{list.Count} mounts";
    });

    private void RunListMinions() => RunAsync(async ct =>
    {
        var list = await mExternalData.Minions.ListAsync(ct).ConfigureAwait(false);
        return $"{list.Count} minions";
    });

    private void RunListAchievements() => RunAsync(async ct =>
    {
        var list = await mExternalData.Achievements.ListAsync(ct).ConfigureAwait(false);
        return $"{list.Count} achievements";
    });

    private void RunListItems() => RunAsync(async ct =>
    {
        var list = await mExternalData.Items.ListAsync(ct).ConfigureAwait(false);
        return $"{list.Count} items";
    });

    private void RunGetFreeCompany()
    {
        var id = mFreeCompanyIdInput.Trim();
        if (string.IsNullOrEmpty(id))
        {
            mOutput = "FC Lodestone id is required.";
            return;
        }
        RunAsync(async ct =>
        {
            var fc = await mExternalData.FreeCompanies.GetAsync(id, ct).ConfigureAwait(false);
            return fc is null ? "<null>" : FormatFreeCompany(fc);
        });
    }

    private void RunGetCatalogEntry(CatalogKind kind)
    {
        if (!int.TryParse(mCatalogIdInput, out var id))
        {
            mOutput = "Catalog id must be an integer.";
            return;
        }
        RunAsync(async ct => kind switch
        {
            CatalogKind.Mount => (await mExternalData.Mounts.GetAsync(id, ct).ConfigureAwait(false))?.ToString() ?? "<null>",
            CatalogKind.Minion => (await mExternalData.Minions.GetAsync(id, ct).ConfigureAwait(false))?.ToString() ?? "<null>",
            CatalogKind.Achievement => (await mExternalData.Achievements.GetAsync(id, ct).ConfigureAwait(false))?.ToString() ?? "<null>",
            CatalogKind.Item => (await mExternalData.Items.GetAsync(id, ct).ConfigureAwait(false))?.ToString() ?? "<null>",
            _ => "<unknown>",
        });
    }

    private void RunAsync(Func<CancellationToken, Task<string>> action)
    {
        if (mBusy) return;
        mBusy = true;
        mOutput = "...";
        _ = Task.Run(async () =>
        {
            try
            {
                mOutput = await action(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                mOutput = $"Exception: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                mBusy = false;
            }
        });
    }

    private string FormatPlayer(Player p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LodestoneId : {p.LodestoneId}");
        sb.AppendLine($"Name        : {p.Name}");
        sb.AppendLine($"HomeWorld   : {mGameDataLookups.GetWorldName(p.HomeWorldId) ?? $"#{p.HomeWorldId}"}");
        sb.AppendLine($"DataCenter  : {mGameDataLookups.GetDataCenterName(p.DataCenterId) ?? $"#{p.DataCenterId}"}");

        if (p.Profile is { } profile)
        {
            sb.AppendLine();
            sb.AppendLine("-- Profile --");
            sb.AppendLine($"Bio                : {profile.Bio ?? "—"}");
            sb.AppendLine($"Nameday            : {profile.Nameday ?? "—"}");
            sb.AppendLine($"GuardianDeity      : {profile.GuardianDeity ?? "—"}");
            sb.AppendLine($"StartingCity       : {profile.StartingCity ?? "—"}");
            sb.AppendLine($"AvatarUrl          : {profile.AvatarUrl ?? "—"}");
            sb.AppendLine($"PortraitUrl        : {profile.PortraitUrl ?? "—"}");
            sb.AppendLine($"FreeCompany id     : {profile.FreeCompanyLodestoneId ?? "—"}");
            sb.AppendLine($"GrandCompany       : {FormatGrandCompany(profile.GrandCompanyId)}");
            sb.AppendLine($"GC Rank            : {FormatGrandCompanyRank(profile)}");
        }

        if (p.Collections is { } c)
        {
            sb.AppendLine();
            sb.AppendLine("-- Collections --");
            sb.AppendLine($"Mounts       : {FormatStats(c.Mounts)}   ({c.OwnedMountIds.Count} owned)");
            sb.AppendLine($"Minions      : {FormatStats(c.Minions)}   ({c.OwnedMinionIds.Count} owned)");
            var datedAchievements = c.OwnedAchievements.Count(a => a.AchievedAt is not null);
            sb.AppendLine($"Achievements : {FormatStats(c.Achievements)}   ({c.OwnedAchievements.Count} owned, {datedAchievements} dated)");
            if (c.OwnedItemIds.Count > 0)
                sb.AppendLine($"Items        : —   ({c.OwnedItemIds.Count} via gear)");
        }

        if (p.ClassJobs is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("-- Class Jobs --");
            foreach (var j in p.ClassJobs.OrderByDescending(j => j.Level).ThenBy(j => j.ClassJobId))
            {
                var name = mGameDataLookups.GetClassJobName(j.ClassJobId) ?? $"#{j.ClassJobId}";
                sb.AppendLine($"  {name,-20} lvl {j.Level}");
            }
        }

        if (p.FreeCompany is { } fc)
        {
            sb.AppendLine();
            sb.AppendLine("-- Free Company --");
            sb.Append(FormatFreeCompany(fc));
        }

        if (p.Gear is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"-- Gear (score: {p.GearScore?.ToString() ?? "—"}) --");
            foreach (var g in p.Gear.OrderBy(g => g.SlotIndex))
            {
                var hq = g.IsHq ? " [HQ]" : "";
                var itemName = mGameDataLookups.GetItemName(g.ItemId) ?? $"#{g.ItemId}";
                sb.AppendLine($"  [{g.SlotIndex:D2}] {itemName}{hq}");
                if (g.GlamourItemId is { } gid)
                    sb.AppendLine($"        glamour: {mGameDataLookups.GetItemName(gid) ?? $"#{gid}"}");
                if (g.Colors.Count > 0) sb.AppendLine($"        colors:  {string.Join(", ", g.Colors)}");
                if (g.Materia.Count > 0) sb.AppendLine($"        materia: {string.Join(", ", g.Materia)}");
                if (g.CreatorName is not null) sb.AppendLine($"        creator: {g.CreatorName}");
                if (g.ItemLevel is not null) sb.AppendLine($"        ilvl:    {g.ItemLevel}");
            }
        }

        return sb.ToString();
    }

    private string FormatFreeCompany(FreeCompany fc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LodestoneId       : {fc.LodestoneId}");
        sb.AppendLine($"Name              : {fc.Name}");
        sb.AppendLine($"Tag               : {fc.Tag ?? "—"}");
        sb.AppendLine($"Slogan            : {fc.Slogan ?? "—"}");
        sb.AppendLine($"FormedAt          : {fc.FormedAt?.ToString("yyyy-MM-dd") ?? "—"}");
        sb.AppendLine($"World             : {(fc.WorldId is { } wid ? mGameDataLookups.GetWorldName(wid) ?? $"#{wid}" : "—")}");
        sb.AppendLine($"Rank              : {fc.Rank?.ToString() ?? "—"}");
        sb.AppendLine($"ActiveMembers     : {fc.ActiveMemberCount?.ToString() ?? "—"}");
        sb.AppendLine($"ActiveState       : {fc.ActiveState}");
        sb.AppendLine($"RecruitmentOpen   : {fc.RecruitmentOpen?.ToString() ?? "—"}");
        sb.AppendLine($"GrandCompany      : {FormatGrandCompany(fc.GrandCompanyId)}");
        if (fc.Estate is { } est)
        {
            sb.AppendLine($"Estate.Name       : {est.Name ?? "—"}");
            sb.AppendLine($"Estate.District   : {(est.DistrictTerritoryId is { } did ? mGameDataLookups.GetTerritoryName((ushort)did) ?? $"#{did}" : "—")}");
            sb.AppendLine($"Estate.Ward       : {est.Ward?.ToString() ?? "—"}");
            sb.AppendLine($"Estate.Plot       : {est.PlotNumber?.ToString() ?? "—"}");
            sb.AppendLine($"Estate.Size       : {est.HouseSize?.ToString() ?? "—"}");
            sb.AppendLine($"Estate.Subdivision: {est.IsSubdivision}");
            sb.AppendLine($"Estate.Greeting   : {est.Greeting ?? "—"}");
        }
        if (fc.Focus is { } f)
        {
            sb.AppendLine($"Focus             : "
                + string.Join(", ", new[]
                {
                    (f.RolePlay,   "RolePlay"),
                    (f.Leveling,   "Leveling"),
                    (f.Casual,     "Casual"),
                    (f.Hardcore,   "Hardcore"),
                    (f.Dungeons,   "Dungeons"),
                    (f.Guildhests, "Guildhests"),
                    (f.Trials,     "Trials"),
                    (f.Raids,      "Raids"),
                    (f.PvP,        "PvP"),
                }.Where(t => t.Item1).Select(t => t.Item2)));
        }
        return sb.ToString();
    }

    private static string FormatStats(PlayerCollectionStats? s)
        => s is null ? "—" : $"{s.Count}/{s.Total}" + (s.Ranking is { } r ? $"  rank {r}" : "");

    private string FormatGrandCompany(byte? gcId)
        => gcId is { } id ? mGameDataLookups.GetGrandCompanyName(id) ?? $"#{id}" : "—";

    private string FormatGrandCompanyRank(PlayerProfile profile)
    {
        if (profile.GrandCompanyId is not { } gc) return "—";
        if (profile.GrandCompanyRankId is not { } rank) return "—";
        var feminine = profile.GrandCompanyRankIsFeminine ?? false;
        return mGameDataLookups.GetGrandCompanyRankName(gc, rank, feminine)
            ?? $"#{rank} ({(feminine ? "F" : "M")})";
    }

    private static void Heading(string label)
    {
        ImGui.Spacing();
        ImGui.TextDisabled(label);
        ImGui.Separator();
    }

    private enum CatalogKind
    { Mount, Minion, Achievement, Item }
}