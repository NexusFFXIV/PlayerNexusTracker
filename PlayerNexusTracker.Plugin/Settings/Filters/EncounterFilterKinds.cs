namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Discrete time-window buckets used by both the encounters tab's
/// per-frame filter and the player-filter editor. Persisted as an int value
/// via JSON; member order is fixed because changing it would silently
/// remap stored preferences. Append new values at the end.</summary>
public enum EncounterTimeRange
{
    All = 0,
    Days7 = 1,
    Days30 = 2,
    Days90 = 3,
}

/// <summary>Coarse-grained category for encounter zones. Distinguishes
/// open-world / city sightings from instanced content, and within instanced
/// content groups by Lumina <c>ContentType</c> row id buckets. Used by:
/// <list type="bullet">
///   <item>The encounters tab as a row filter.</item>
///   <item>The player-filter editor as the first stage of the
///   "encountered in" criterion (narrows the zone picker below it).</item>
/// </list>
/// Persisted as an int; do not reorder members.</summary>
public enum EncounterZoneFilter
{
    All = 0,
    OpenWorld = 1,
    AnyDuty = 2,
    Dungeons = 3,
    Trials = 4,
    Raids = 5,
    Pvp = 6,
    Field = 7,
    OtherDuty = 8,
}
