namespace PlayerNexusTracker.Settings.Filters;

/// <summary>The root POCO persisted under <see cref="StoreKey"/> in the
/// settings store. Holds the user-defined filter list shown in the
/// player-list dropdown after the four built-in "system" filters.</summary>
public sealed class PlayerFilterCollection
{
    public const string StoreKey = "ui.pntracker.player_filters";

    public List<PlayerFilter> Filters { get; set; } = new();
}

/// <summary>A single user-defined filter. <see cref="Id"/> is the stable
/// internal identity (renames don't disturb dropdown selection across reloads);
/// <see cref="Name"/> is the user-facing label.</summary>
public sealed class PlayerFilter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>AND-conjunction (v1). Every criterion must match. An empty list
    /// matches nothing — the editor surfaces a placeholder hint instead of
    /// silently mimicking the "All players" system filter.</summary>
    public List<PlayerFilterCriterion> Criteria { get; set; } = new();
}

public sealed class PlayerFilterCriterion
{
    public FilterField Field { get; set; }

    public FilterOperator Operator { get; set; }

    /// <summary>Raw string form. Parsed per <see cref="Field"/> at compile-time
    /// by <c>PlayerFilterEvaluator</c> (ints, enum values, durations, bools).
    /// Keeping the persisted form as a string keeps the JSON schema flat and
    /// stable across enum tweaks.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>Fields the v1 evaluator can match against. All sourced from
/// <c>ObservedPlayer</c> + auxiliary in-memory state (currently-visible set,
/// unread-history index, <c>IGameDataLookups</c>). No Lodestone-enriched
/// fields and no encounter aggregates in v1 — those require per-row DB reads
/// that don't fit the per-frame evaluation budget.</summary>
public enum FilterField : byte
{
    Name = 0,
    HomeWorld = 1,
    DataCenter = 2,
    ClassJob = 3,
    JobRole = 4,
    Level = 5,
    /// <summary>Matches the race row id stored in
    /// <c>ObservedPlayer.Race</c> (derived from the in-game Customize byte 0).
    /// The persisted criterion value is the row id as a decimal string; the
    /// editor labels are pulled from Lumina at render time so locale changes
    /// flow through automatically.</summary>
    Race = 6,
    /// <summary>Numeric "how long ago did we last see this player, in hours" —
    /// `LessThan 24` selects players observed in the last day, `GreaterThan 48`
    /// selects players we haven't seen for at least two days.</summary>
    HoursSinceLastSeen = 7,
    /// <summary>Same shape as <see cref="HoursSinceLastSeen"/> but for the
    /// first-sighting timestamp, expressed in days for a saner number range
    /// ("met within the last week" → `LessThan 7`).</summary>
    DaysSinceFirstSeen = 8,
    HasLodestoneId = 9,
    CompanyTag = 10,
    HasCompanyTag = 11,
    IsCurrentlyVisible = 12,
    HasUnreadHistory = 13,
    OnlineStatusId = 14,
    /// <summary>Matches the gender byte stored in <c>ObservedPlayer.Gender</c>
    /// (derived from the in-game Customize byte 1; 0 = male, 1 = female).
    /// Editor renders a combo of localized gender labels; persisted as the
    /// decimal byte value to stay stable across language changes.</summary>
    Gender = 15,
    /// <summary>True when the user has authored any non-whitespace notes
    /// for the character. Persisted on <c>ObservedPlayer.Notes</c>.</summary>
    HasNotes = 16,
    /// <summary>Text match against the user-authored notes. Use the Contains
    /// operator for substring search; Equals / StartsWith are available too.</summary>
    Notes = 17,
    /// <summary>Average gear iLvl across all equipped slots except the soul
    /// crystal (slot 12). Resolved through the <c>nexus_filter_player</c>
    /// SQL view — null for unenriched characters, which never match a
    /// specific-threshold criterion.</summary>
    GearScore = 18,
    /// <summary>Highest job level the character has reached on any class /
    /// job, sourced from the Lodestone class-job page. Unenriched players
    /// never match.</summary>
    MaxJobLevel = 19,
    /// <summary>Number of mounts the character has unlocked, from the
    /// FFXIVCollect collection stats.</summary>
    MountCount = 20,
    /// <summary>Number of minions unlocked.</summary>
    MinionCount = 21,
    /// <summary>Number of achievements completed.</summary>
    AchievementCount = 22,
    /// <summary>Text match against the Lodestone-resolved Free Company
    /// name. Distinct from <see cref="CompanyTag"/>, which matches the
    /// live in-game tag observation.</summary>
    FcName = 23,
    /// <summary>True when the character has a Lodestone-resolved Free
    /// Company link on file. Distinct from <see cref="HasCompanyTag"/>,
    /// which checks the live observation tag.</summary>
    HasFreeCompany = 24,
    /// <summary>Exact-match filter on the Lodestone FC id stored on the
    /// player profile. The strongest available FC identifier — unaffected by
    /// tag collisions across worlds (multiple FCs can legitimately share a
    /// tag on the same world). Prefer this over <see cref="CompanyTag"/>
    /// and <see cref="FcName"/> when you want to pin down a single FC.</summary>
    FreeCompanyLodestoneId = 25,
    /// <summary>Days since the player was last seen as part of an
    /// <c>nexus_internal_player_encounter</c> row — i.e. the most recent
    /// shared territory session. Distinct from <see cref="HoursSinceLastSeen"/>,
    /// which comes from the live observation watcher (visibility-window
    /// limited); the encounter-based measure persists across plugin
    /// reloads and goes back as far as the encounter history exists.
    /// Unenriched / never-encountered players never match a specific-day
    /// threshold (NULL last_encounter_at).</summary>
    DaysSinceLastEncounter = 26,
    /// <summary>Compound criterion: "did this player share a territory
    /// with the local player from one of these zones / categories of
    /// content?" The persisted value encodes both a coarse
    /// <see cref="EncounterZoneFilter"/> category and an optional
    /// concrete TerritoryId — see <c>EncounteredInValueCodec</c> for the
    /// exact format. Matches via an EXISTS subquery against the
    /// encounter tables joined through the player's content_id.</summary>
    EncounteredIn = 27,
}

public enum FilterOperator : byte
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    StartsWith = 3,
    GreaterThan = 4,
    LessThan = 5,
    IsTrue = 6,
    IsFalse = 7,
}

