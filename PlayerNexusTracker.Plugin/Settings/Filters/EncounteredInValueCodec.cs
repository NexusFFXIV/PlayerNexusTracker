using System.Globalization;

namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Serialised form of the <see cref="FilterField.EncounteredIn"/>
/// criterion value. Persisted as a flat string in
/// <see cref="PlayerFilterCriterion.Value"/> with the shape
/// <c>"&lt;category&gt;:&lt;territoryId&gt;"</c>:
/// <list type="bullet">
///   <item><c>"5:1234"</c> — category 5 (Raids), territory 1234 (a specific
///   raid map). The category serves as the editor's pre-filter for the
///   zone picker and as a sanity check at evaluation time.</item>
///   <item><c>"5:0"</c> — category 5 (Raids), no specific territory; matches
///   players encountered in <em>any</em> raid territory.</item>
///   <item><c>"0:0"</c> or empty string — unset. The criterion compiles
///   as invalid and the filter matches nothing.</item>
/// </list>
/// Stored as a string to keep the persisted JSON shape stable; numerics
/// rather than enum names so a rename of <see cref="EncounterZoneFilter"/>
/// members doesn't silently shift stored values.</summary>
public readonly record struct EncounteredInValue(
    EncounterZoneFilter Category,
    ushort TerritoryId)
{
    public static readonly EncounteredInValue Empty = new(EncounterZoneFilter.All, 0);

    public string Encode()
        => $"{(int)Category}:{TerritoryId.ToString(CultureInfo.InvariantCulture)}";

    public static EncounteredInValue Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;
        var sep = raw.IndexOf(':');
        if (sep <= 0 || sep >= raw.Length - 1) return Empty;
        var catSpan = raw.AsSpan(0, sep);
        var tidSpan = raw.AsSpan(sep + 1);
        if (!int.TryParse(catSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var catInt)
            || !ushort.TryParse(tidSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
            return Empty;
        if (!System.Enum.IsDefined(typeof(EncounterZoneFilter), catInt)) return Empty;
        return new EncounteredInValue((EncounterZoneFilter)catInt, tid);
    }

    /// <summary>True when the value targets something concrete enough to
    /// produce a real SQL clause. Both "category All + territory 0" and a
    /// bogus parse short-circuit to false.</summary>
    public bool IsSpecified => Category != EncounterZoneFilter.All || TerritoryId != 0;
}
