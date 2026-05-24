namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Where the evaluator resolves a field's value: from the slim
/// in-memory <c>ObservedPlayer</c> projection, or from the SQL view
/// <c>nexus_filter_player</c>. The player-list panel splits a compiled
/// filter on this distinction — DB-sourced criteria become a single
/// <c>SELECT content_id ... WHERE ...</c> at filter activation; the
/// remaining in-memory criteria evaluate per-row per-frame as before.</summary>
public enum FilterEvalSource : byte
{
    Memory,
    Database,
}

/// <summary>Shape of the value input the editor should render for a given
/// <see cref="FilterField"/>. Drives the per-criterion value widget choice and
/// the parser used by <c>PlayerFilterEvaluator</c>.</summary>
public enum FilterValueKind : byte
{
    /// <summary>Free-form string. String comparison is case-insensitive.</summary>
    Text,
    /// <summary>32-bit integer. Stored as a decimal string in
    /// <c>PlayerFilterCriterion.Value</c>; the evaluator parses it once at
    /// compile-time.</summary>
    Integer,
    /// <summary>No value input — the operator (IsTrue / IsFalse) carries the
    /// bool. The value widget is hidden in the editor for these fields.</summary>
    Bool,
    /// <summary>One of the <c>JobRole</c> enum values. Editor renders a combo
    /// of the localized role names; persisted as the enum value's
    /// invariant name (e.g. "Tank") so refactoring the enum order doesn't
    /// silently break stored filters.</summary>
    JobRoleEnum,
    /// <summary>A Lumina <c>Race</c> row id. Editor renders a combo whose
    /// labels come from <c>IGameDataLookups.GetRaceName</c> (masculine form
    /// for the picker — the filter matches by id regardless of gender);
    /// persisted as the row id formatted as a decimal string.</summary>
    RaceEnum,
    /// <summary>Two-valued gender selector (0 = male, 1 = female). Editor
    /// uses a two-entry combo with localized labels reused from the
    /// observation panel's existing <c>ui.main.gender.*</c> keys; persisted
    /// as the byte value as a decimal string.</summary>
    GenderEnum,
    /// <summary>A Lumina <c>OnlineStatus</c> row id (RolePlay / AFK / Busy /
    /// Mentor / Game Master / …). Editor renders a combo whose labels come
    /// from <c>IGameDataLookups.GetOnlineStatusName</c>; persisted as the
    /// row id formatted as a decimal string, so the format is identical to
    /// the prior Integer-kind value and existing filters keep working.</summary>
    OnlineStatusEnum,
    /// <summary>Compound widget for the <see cref="FilterField.EncounteredIn"/>
    /// criterion: a category combo (<see cref="EncounterZoneFilter"/>) plus
    /// a typeable territory picker filtered by that category. Persisted as
    /// the encoded <see cref="EncounteredInValue"/> string.</summary>
    EncounteredInPicker,
}

/// <summary>Per-field static metadata: which operators are valid, and what
/// kind of value widget to render. Centralized here so the editor combo and
/// the evaluator agree on what's expressible.
/// <para>The set of allowed operators per field is intentionally small —
/// invalid pairings like <c>Level Contains</c> are not even offered in the
/// editor, so the user can't construct nonsense filters.</para></summary>
public static class FilterFieldMetadata
{
    public static FilterValueKind GetValueKind(FilterField field) => field switch
    {
        FilterField.Name => FilterValueKind.Text,
        FilterField.HomeWorld => FilterValueKind.Text,
        FilterField.DataCenter => FilterValueKind.Text,
        FilterField.ClassJob => FilterValueKind.Text,
        FilterField.JobRole => FilterValueKind.JobRoleEnum,
        FilterField.Level => FilterValueKind.Integer,
        FilterField.HoursSinceLastSeen => FilterValueKind.Integer,
        FilterField.DaysSinceFirstSeen => FilterValueKind.Integer,
        FilterField.DaysSinceLastEncounter => FilterValueKind.Integer,
        FilterField.EncounteredIn => FilterValueKind.EncounteredInPicker,
        FilterField.HasLodestoneId => FilterValueKind.Bool,
        FilterField.CompanyTag => FilterValueKind.Text,
        FilterField.HasCompanyTag => FilterValueKind.Bool,
        FilterField.IsCurrentlyVisible => FilterValueKind.Bool,
        FilterField.HasUnreadHistory => FilterValueKind.Bool,
        FilterField.OnlineStatusId => FilterValueKind.OnlineStatusEnum,
        FilterField.Race => FilterValueKind.RaceEnum,
        FilterField.Gender => FilterValueKind.GenderEnum,
        FilterField.HasNotes => FilterValueKind.Bool,
        FilterField.Notes => FilterValueKind.Text,
        FilterField.GearScore => FilterValueKind.Integer,
        FilterField.MaxJobLevel => FilterValueKind.Integer,
        FilterField.MountCount => FilterValueKind.Integer,
        FilterField.MinionCount => FilterValueKind.Integer,
        FilterField.AchievementCount => FilterValueKind.Integer,
        FilterField.FcName => FilterValueKind.Text,
        FilterField.HasFreeCompany => FilterValueKind.Bool,
        FilterField.FreeCompanyLodestoneId => FilterValueKind.Text,
        _ => FilterValueKind.Text,
    };

    /// <summary>Tells the compiled-filter pipeline whether this field is
    /// resolvable from the slim in-memory <c>ObservedPlayer</c> projection
    /// (Memory) or only via the SQL view (Database).</summary>
    public static FilterEvalSource GetEvalSource(FilterField field) => field switch
    {
        // DB-only — populated by Lodestone enrichment, not on the in-memory
        // observation. The view exposes them as columns and falls back to
        // NULL for unenriched characters.
        FilterField.GearScore => FilterEvalSource.Database,
        FilterField.MaxJobLevel => FilterEvalSource.Database,
        FilterField.MountCount => FilterEvalSource.Database,
        FilterField.MinionCount => FilterEvalSource.Database,
        FilterField.AchievementCount => FilterEvalSource.Database,
        FilterField.FcName => FilterEvalSource.Database,
        FilterField.HasFreeCompany => FilterEvalSource.Database,
        FilterField.FreeCompanyLodestoneId => FilterEvalSource.Database,
        // Notes text lives on observed_player and would be cheap to keep
        // in-memory, but holding the full content for every row is what D2
        // explicitly stripped out — defer the LIKE to SQLite instead of
        // re-introducing the side-cache.
        FilterField.Notes => FilterEvalSource.Database,
        // Encounter aggregates — built from the encounter tables via the
        // SQL view (MAX(last_seen_at)). No in-memory projection of that
        // history exists, so DB is the only path.
        FilterField.DaysSinceLastEncounter => FilterEvalSource.Database,
        FilterField.EncounteredIn => FilterEvalSource.Database,
        _ => FilterEvalSource.Memory,
    };

    public static IReadOnlyList<FilterOperator> GetAllowedOperators(FilterField field) =>
        GetValueKind(field) switch
        {
            FilterValueKind.Text => TextOps,
            FilterValueKind.Integer => IntegerOps,
            FilterValueKind.Bool => BoolOps,
            FilterValueKind.JobRoleEnum => EnumOps,
            FilterValueKind.RaceEnum => EnumOps,
            FilterValueKind.GenderEnum => EnumOps,
            FilterValueKind.OnlineStatusEnum => EnumOps,
            // The picker widget owns both selections; only equality makes
            // sense here (the user explicitly picks the territory and we
            // EXISTS-match). NotEquals would invert to "encountered
            // anywhere except this zone", but that's an edge use case —
            // expose it now since the operator list is shared.
            FilterValueKind.EncounteredInPicker => EnumOps,
            _ => TextOps,
        };

    /// <summary>True if the operator is one of the valid choices for the
    /// field. The editor uses this to clamp an out-of-range operator back to
    /// the first allowed one when the user switches fields on an existing
    /// criterion (and as a defensive check when loading persisted filters
    /// after an enum refactor).</summary>
    public static bool IsOperatorAllowed(FilterField field, FilterOperator op)
    {
        var allowed = GetAllowedOperators(field);
        for (var i = 0; i < allowed.Count; i++)
            if (allowed[i] == op) return true;
        return false;
    }

    private static readonly FilterOperator[] TextOps =
    {
        FilterOperator.Equals,
        FilterOperator.NotEquals,
        FilterOperator.Contains,
        FilterOperator.StartsWith,
    };

    private static readonly FilterOperator[] IntegerOps =
    {
        FilterOperator.Equals,
        FilterOperator.NotEquals,
        FilterOperator.GreaterThan,
        FilterOperator.LessThan,
    };

    private static readonly FilterOperator[] BoolOps =
    {
        FilterOperator.IsTrue,
        FilterOperator.IsFalse,
    };

    private static readonly FilterOperator[] EnumOps =
    {
        FilterOperator.Equals,
        FilterOperator.NotEquals,
    };

    /// <summary>Enumerates every supported field in editor display order.
    /// Used by the field-picker combo in the settings section.
    /// <para>Order here is purely cosmetic — the persisted criterion stores
    /// the underlying <see cref="FilterField"/> enum value, so reshuffling
    /// this array doesn't disturb existing saved filters. Grouped by topic so
    /// related fields sit next to each other in the picker:</para>
    /// <list type="bullet">
    ///   <item>Identity: name + where they live.</item>
    ///   <item>Class &amp; gear: current job + Lodestone-side level/gear.</item>
    ///   <item>Customisation: appearance.</item>
    ///   <item>Free Company: live-tag and catalog-id signals, from the
    ///   ambiguous live one up to the strongest precise Lodestone-id match.</item>
    ///   <item>Collections: mounts / minions / achievements totals.</item>
    ///   <item>Presence: visibility + how recently we saw them.</item>
    ///   <item>Notes &amp; tracking: user annotations + enrichment / history flags.</item>
    /// </list></summary>
    public static readonly FilterField[] AllFields =
    {
        // Identity — who and where.
        FilterField.Name,
        FilterField.HomeWorld,
        FilterField.DataCenter,

        // Class & gear — current job from the live observation, then the
        // Lodestone-aggregated metrics.
        FilterField.ClassJob,
        FilterField.JobRole,
        FilterField.Level,
        FilterField.MaxJobLevel,
        FilterField.GearScore,

        // Customisation.
        FilterField.Race,
        FilterField.Gender,

        // Free Company — ordered from the weakest live signal (tag-only,
        // ambiguous across worlds) to the strongest Lodestone-id match, with
        // the boolean "has-a-thing" filters next to their value siblings:
        //   HasCompanyTag / CompanyTag   — live, fastest, can collide.
        //   HasFreeCompany / FcName      — catalog, requires enrichment.
        //   FreeCompanyLodestoneId       — exact id, never ambiguous.
        FilterField.HasCompanyTag,
        FilterField.CompanyTag,
        FilterField.HasFreeCompany,
        FilterField.FcName,
        FilterField.FreeCompanyLodestoneId,

        // Collections.
        FilterField.MountCount,
        FilterField.MinionCount,
        FilterField.AchievementCount,

        // Presence — currently-visible and recency.
        FilterField.IsCurrentlyVisible,
        FilterField.OnlineStatusId,
        FilterField.HoursSinceLastSeen,
        FilterField.DaysSinceFirstSeen,
        FilterField.DaysSinceLastEncounter,
        FilterField.EncounteredIn,

        // User annotations.
        FilterField.HasNotes,
        FilterField.Notes,

        // Tracking flags — "did we ever enrich this character", "is there an
        // unread change". Less about the character, more about our pipeline.
        FilterField.HasLodestoneId,
        FilterField.HasUnreadHistory,
    };

    /// <summary>Job-role enum values exposed to the v1 editor. Mirrors the
    /// <c>NexusKit.GameData.JobRole</c> categories (Tank/Healer/MeleeDps/
    /// RangedDps/MagicalDps/Crafter/Gatherer). Kept here as strings to avoid
    /// a hard dependency on the GameData enum from the persistence layer —
    /// the evaluator resolves a player's <c>ClassJobId</c> via
    /// <c>IGameDataLookups</c> and compares the resulting role name.</summary>
    public static readonly string[] JobRoleNames =
    {
        "Tank",
        "Healer",
        "MeleeDps",
        "RangedDps",
        "MagicalDps",
        "Crafter",
        "Gatherer",
    };
}
