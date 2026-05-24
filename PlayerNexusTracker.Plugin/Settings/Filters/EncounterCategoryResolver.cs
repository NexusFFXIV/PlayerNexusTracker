using Lumina.Excel.Sheets;
using NexusKit.GameData;

namespace PlayerNexusTracker.Settings.Filters;

/// <summary>
/// Maps every Lumina TerritoryType row id to the
/// <see cref="EncounterZoneFilter"/> category it belongs to and exposes the
/// reverse lookup ("give me every TerritoryId in category X"). Lazy-built
/// on first access — Lumina sheets are immutable at runtime, so the
/// per-category lists are cached for the singleton's lifetime.
///
/// <para>Used by:</para>
/// <list type="bullet">
///   <item>The player-filter compile path, to expand a "category-only"
///   "Encountered in" criterion into a concrete IN(…) clause of territory
///   ids.</item>
///   <item>The settings UI's zone picker, to populate the "specific zone"
///   dropdown after the user narrowed the category.</item>
/// </list>
///
/// <para>Classification is intentionally identical to
/// <see cref="EncountersTab"/> rendering: a territory only counts as
/// instanced when <see cref="IGameDataLookups.GetInstancedContentName"/>
/// returns non-null (city CFC plumbing is filtered out). Within instanced
/// content the category derives from <see cref="ContentType"/> row ids
/// (2 = Dungeons, 4 = Trials, 5 = Raids, 6 = PvP, 21 / 26 = field
/// operations, everything else = OtherDuty).</para>
/// </summary>
public sealed class EncounterCategoryResolver
{
    private readonly IGameDataLookups mLookups;
    private readonly ISheetsProvider mSheets;
    private readonly object mLock = new();
    private IReadOnlyDictionary<EncounterZoneFilter, IReadOnlyList<ushort>>? mByCategory;
    private IReadOnlyDictionary<ushort, EncounterZoneFilter>? mPrimaryCategoryOf;

    public EncounterCategoryResolver(IGameDataLookups lookups, ISheetsProvider sheets)
    {
        mLookups = lookups;
        mSheets = sheets;
    }

    /// <summary>Every TerritoryId classified under <paramref name="category"/>.
    /// Returns an empty list for <see cref="EncounterZoneFilter.All"/> — that
    /// value is a "no filter" sentinel, not a real bucket.</summary>
    public IReadOnlyList<ushort> GetTerritoriesForCategory(EncounterZoneFilter category)
    {
        if (category == EncounterZoneFilter.All) return System.Array.Empty<ushort>();
        var cache = GetByCategory();
        return cache.TryGetValue(category, out var list) ? list : System.Array.Empty<ushort>();
    }

    /// <summary>The primary category of a TerritoryId — i.e. its bucket without
    /// the AnyDuty / All aliases. Useful to drive the "category" dropdown's
    /// default when the user picked a specific TerritoryId.</summary>
    public EncounterZoneFilter GetPrimaryCategory(ushort territoryTypeId)
    {
        var primary = GetPrimary();
        return primary.TryGetValue(territoryTypeId, out var c) ? c : EncounterZoneFilter.OpenWorld;
    }

    private IReadOnlyDictionary<EncounterZoneFilter, IReadOnlyList<ushort>> GetByCategory()
    {
        if (mByCategory is { } cached) return cached;
        lock (mLock)
        {
            if (mByCategory is { } got) return got;
            Build();
            return mByCategory!;
        }
    }

    private IReadOnlyDictionary<ushort, EncounterZoneFilter> GetPrimary()
    {
        if (mPrimaryCategoryOf is { } cached) return cached;
        lock (mLock)
        {
            if (mPrimaryCategoryOf is { } got) return got;
            Build();
            return mPrimaryCategoryOf!;
        }
    }

    private void Build()
    {
        var lists = new Dictionary<EncounterZoneFilter, List<ushort>>();
        foreach (var cat in (EncounterZoneFilter[])System.Enum.GetValues(typeof(EncounterZoneFilter)))
            lists[cat] = new List<ushort>();

        var primaryOf = new Dictionary<ushort, EncounterZoneFilter>();

        var territorySheet = mSheets.GetSheet<TerritoryType>();
        if (territorySheet is not null)
        {
            foreach (var row in territorySheet)
            {
                var ttId = (ushort)row.RowId;
                // Skip the migration sentinel — territory 0 isn't a real
                // location and the encounter table uses it for legacy rows.
                if (ttId == 0) continue;

                var primary = ClassifyPrimary(ttId);
                primaryOf[ttId] = primary;
                lists[primary].Add(ttId);

                // AnyDuty covers every primary category except OpenWorld.
                if (primary != EncounterZoneFilter.OpenWorld)
                    lists[EncounterZoneFilter.AnyDuty].Add(ttId);
            }
        }

        var byCategory = new Dictionary<EncounterZoneFilter, IReadOnlyList<ushort>>(lists.Count);
        foreach (var pair in lists) byCategory[pair.Key] = pair.Value;
        mByCategory = byCategory;
        mPrimaryCategoryOf = primaryOf;
    }

    private EncounterZoneFilter ClassifyPrimary(ushort territoryTypeId)
    {
        // Identical semantics to EncountersTab so the player-filter's
        // "Raids" bucket matches exactly what the encounters tab shows
        // as a Raid row.
        var instancedName = mLookups.GetInstancedContentName(territoryTypeId);
        if (string.IsNullOrEmpty(instancedName)) return EncounterZoneFilter.OpenWorld;

        var ct = mLookups.GetContentTypeRowId(territoryTypeId);
        return ct switch
        {
            2 => EncounterZoneFilter.Dungeons,
            4 => EncounterZoneFilter.Trials,
            5 => EncounterZoneFilter.Raids,
            6 => EncounterZoneFilter.Pvp,
            21 => EncounterZoneFilter.Field,
            26 => EncounterZoneFilter.Field,
            _ => EncounterZoneFilter.OtherDuty,
        };
    }
}
