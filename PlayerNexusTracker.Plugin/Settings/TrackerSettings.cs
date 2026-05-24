namespace PlayerNexusTracker.Settings;

public sealed class TrackerSettings
{
    public int MaxRecentPlayers { get; set; } = 100;

    /// <summary>How many days a cached Lodestone/FFXIVCollect sub-resource
    /// stays "fresh" before the refresh queue re-fetches it. Per-category;
    /// e.g. setting 7 means each of (profile, class-jobs, gear, FC, mounts,
    /// minions, achievements) is independently re-fetched after 7 days.</summary>
    public int RefreshTtlDays { get; set; } = 7;
}