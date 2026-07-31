using NexusKit.GameData;
using NexusKit.Modules.InternalData.Players;

namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Why a compiled criterion may not be usable. The two failure modes
/// are handled differently on purpose.
/// <list type="bullet">
/// <item><see cref="Incomplete"/> — the user hasn't finished authoring the line
/// (a text box still empty). Dropped from the compiled filter entirely, so the
/// other criteria keep working while typing. Under the same-field OR rule an
/// empty "contains" would otherwise widen its whole group to "anything".</item>
/// <item><see cref="Invalid"/> — broken data (unparseable integer, unresolvable
/// encounter category, hand-edited JSON). Kept as a member that never matches,
/// rather than dropped: dropping it would make its group vacuously satisfied and
/// silently <em>widen</em> the filter, which is the worst way for a filter to
/// fail. As a never-matching member it makes an all-invalid group evaluate to
/// false, exactly as before.</item>
/// </list></summary>
internal enum CriterionStatus : byte
{
    /// <summary>Default so <c>default(CompiledCriterion)</c> is never
    /// accidentally treated as usable-but-broken.</summary>
    Ready = 0,
    Incomplete = 1,
    Invalid = 2,
}

/// <summary>Pre-parsed form of a <see cref="PlayerFilterCriterion"/>. Values
/// are converted once at compile time; the per-row evaluator just dispatches
/// on the field. See <see cref="CriterionStatus"/> for how unusable criteria
/// are treated.</summary>
internal readonly struct CompiledCriterion
{
    public FilterField Field { get; init; }
    public FilterOperator Operator { get; init; }
    public string StringValue { get; init; }
    public int IntValue { get; init; }
    public JobRole RoleValue { get; init; }
    public CriterionStatus Status { get; init; }
    /// <summary>Set by the EncounteredIn compiler when the criterion's
    /// category expands to many territory ids (e.g. "any Raid" → every
    /// Raid TerritoryId). The SQL builder splices these as an
    /// <c>IN (…)</c> clause. Null for any non-picker criterion or when a
    /// specific TerritoryId was picked (handled via <see cref="IntValue"/>
    /// alone).</summary>
    public IReadOnlyList<ushort>? TerritoryIdSet { get; init; }

    public bool IsValid => Status == CriterionStatus.Ready;
    public bool IsIncomplete => Status == CriterionStatus.Incomplete;
}

/// <summary>Compiled form of a <see cref="PlayerFilter"/>. Built once per
/// filter activation; reused across frames until the filter's criteria
/// change.
///
/// <para>Criteria are grouped by field (see
/// <see cref="CompiledCriterionGroup"/>): alternatives on one field OR together,
/// groups AND with each other. Because <c>GetEvalSource</c> is a pure function of
/// the field, every group lands wholly in one phase — a group never straddles the
/// memory/SQL split, which is what lets the two phases stay a plain
/// intersection.</para>
///
/// <para>D3 splits a compiled filter into two phases:</para>
/// <list type="bullet">
/// <item><see cref="InMemoryGroups"/> evaluate per-row per-frame against the
/// slim <c>ObservedPlayer</c> (cheap, volatile fields like
/// <c>IsCurrentlyVisible</c> stay here).</item>
/// <item><see cref="SqlWhere"/> (when non-null) is the WHERE clause for the
/// <c>nexus_filter_player</c> view query — covers Lodestone-side aggregates
/// that we explicitly do NOT hold in memory (gear score, FC name,
/// collection counts, notes content). The panel runs the query at filter
/// activation, stores the resulting set on <see cref="DbAllowedContentIds"/>,
/// and re-queries when the watcher's <c>Revision</c> bumps. Empty filters
/// (no criteria at all) match nothing — see the editor's "Add a rule"
/// placeholder behaviour.</item>
/// </list>
/// </summary>
internal sealed class CompiledFilter
{
    public required Guid SourceId { get; init; }
    public required IReadOnlyList<CompiledCriterionGroup> InMemoryGroups { get; init; }
    /// <summary>Snapshot of the source filter's criterion-list revision (its
    /// count + a hash of field/operator/value triples). The list panel uses
    /// this to detect editor-side mutations and rebuild the cache.</summary>
    public required int SourceRevision { get; init; }
    /// <summary>WHERE-clause fragment to splice into
    /// <c>SELECT content_id FROM nexus_filter_player WHERE …</c>. Null when
    /// no criterion is DB-resolvable.</summary>
    public required string? SqlWhere { get; init; }
    /// <summary>Parameter values in the order the WHERE clause's
    /// <c>@p0</c>, <c>@p1</c>, … placeholders expect them.</summary>
    public required IReadOnlyList<object>? SqlParameters { get; init; }
    /// <summary>Total criterion count (memory + would-be DB). Used by
    /// callers that need to distinguish "filter has no criteria → match
    /// nothing" from "filter has DB criteria but the query hasn't landed
    /// yet → wait one frame".</summary>
    public required int TotalCriterionCount { get; init; }

    /// <summary>Criteria that survived compilation, i.e. excluding the ones
    /// dropped as <see cref="CriterionStatus.Incomplete"/>. Distinct from
    /// <see cref="TotalCriterionCount"/> because a filter whose every criterion
    /// is still half-typed must match nothing — with no groups and no
    /// <see cref="SqlWhere"/> it would otherwise read as "no constraints at
    /// all", i.e. match everyone.</summary>
    public required int EffectiveCriterionCount { get; init; }

    // Mutable panel-owned fields, filled asynchronously when the DB query
    // returns. The panel reads <see cref="DbDataVersion"/> against
    // <c>watcher.Revision</c> to decide whether the cached set is stale.

    /// <summary>Result of the most recent unordered <c>nexus_filter_player</c>
    /// query. Set when sort is in-memory (the panel still wants the candidate
    /// set for membership testing). Null while the query is in flight, or
    /// when DB-ordered results are active instead — see
    /// <see cref="DbOrderedContentIds"/>.</summary>
    public HashSet<ulong>? DbAllowedContentIds { get; set; }
    /// <summary>Result of the ordered <c>nexus_filter_player</c> query when
    /// the active sort resolves through the view (gear score, mount count,
    /// …). Mutable + appended-to as the user scrolls past the end of the
    /// rendered list — the panel pages additional content_ids in via
    /// OFFSET/LIMIT and tacks them onto this list. Null when sort is
    /// in-memory or no DB query has run yet.</summary>
    public List<ulong>? DbOrderedContentIds { get; set; }
    /// <summary>True once a DB page came back shorter than the requested
    /// LIMIT — the panel stops kicking follow-up pages from then on. Reset
    /// to false whenever a fresh first-page query starts (filter/sort/
    /// revision change).</summary>
    public bool DbOrderedEndOfStream { get; set; }
    /// <summary>View column the cached DB result was sorted by, or null when
    /// the cached result is unordered. Paired with
    /// <see cref="CachedSortDescending"/>; both together let the panel detect
    /// a sort change and invalidate the cache without rebuilding the whole
    /// compiled filter.</summary>
    public string? CachedSortColumn { get; set; }
    public bool CachedSortDescending { get; set; }
    /// <summary>Last <c>watcher.Revision</c> value the query ran against;
    /// stale once the watcher bumps it via an observation tick or a notes
    /// save. -1 means "never queried".</summary>
    public long DbDataVersion { get; set; } = -1;

    public bool IsEmpty => TotalCriterionCount == 0;

    /// <summary>True when the filter cannot match anyone: it has no criteria at
    /// all, or every criterion it has is still incomplete. Callers must gate on
    /// this rather than <see cref="IsEmpty"/>, which only covers the first
    /// case.</summary>
    public bool MatchesNothing => EffectiveCriterionCount == 0;

    public bool RequiresDbQuery => SqlWhere is not null;
}

public static class PlayerFilterEvaluator
{
    internal static CompiledFilter Compile(PlayerFilter filter,
                                           EncounterCategoryResolver? categoryResolver = null)
    {
        var inMemory = new List<CompiledCriterion>(filter.Criteria.Count);
        var dbCriteria = new List<CompiledCriterion>(filter.Criteria.Count);
        for (var i = 0; i < filter.Criteria.Count; i++)
        {
            var compiled = CompileOne(filter.Criteria[i], categoryResolver);
            // Incomplete criteria are dropped before bucketing, not later: if a
            // filter's only DB criterion were still half-typed and we dropped it
            // inside the builder, Build would hit its "no criteria → 1" path,
            // RequiresDbQuery would stay true, and every frame would kick a
            // full-table scan returning the entire view.
            if (compiled.IsIncomplete) continue;
            if (FilterFieldMetadata.GetEvalSource(compiled.Field) == FilterEvalSource.Database)
                dbCriteria.Add(compiled);
            else
                inMemory.Add(compiled);
        }

        var inMemoryGroups = CompiledCriterionGrouper.Group(inMemory);

        string? sqlWhere = null;
        IReadOnlyList<object>? sqlParams = null;
        if (dbCriteria.Count > 0)
        {
            var (where, parameters) =
                PlayerFilterSqlBuilder.Build(CompiledCriterionGrouper.Group(dbCriteria));
            sqlWhere = where;
            sqlParams = parameters;
        }

        return new CompiledFilter
        {
            SourceId = filter.Id,
            InMemoryGroups = inMemoryGroups,
            SqlWhere = sqlWhere,
            SqlParameters = sqlParams,
            SourceRevision = ComputeSourceRevision(filter),
            TotalCriterionCount = filter.Criteria.Count,
            EffectiveCriterionCount = inMemory.Count + dbCriteria.Count,
        };
    }

    /// <summary>Cheap, stable hash of a filter's criterion list — used by the
    /// list panel to detect editor-side mutations without holding a deep copy
    /// of the previous criterion list.
    /// <para>Criterion order is hashed even though it no longer affects the
    /// result (grouping by field is order-independent), so a purely cosmetic
    /// reorder in the editor still triggers a rebuild. Harmless, and cheaper
    /// than an order-insensitive hash.</para></summary>
    public static int ComputeSourceRevision(PlayerFilter filter)
    {
        var hash = new HashCode();
        hash.Add(filter.Criteria.Count);
        for (var i = 0; i < filter.Criteria.Count; i++)
        {
            var c = filter.Criteria[i];
            hash.Add((byte)c.Field);
            hash.Add((byte)c.Operator);
            hash.Add(c.Value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>Evaluates the in-memory half of a compiled filter against one
    /// player. Groups AND together; inside a group the alternatives OR and the
    /// restrictions AND.
    /// <para>Runs for every candidate on every frame, so it stays allocation-free:
    /// indexed loops, no LINQ, grouping already done at compile time.</para></summary>
    internal static bool Match(CompiledFilter filter, ObservedPlayer player, EvalContext ctx)
    {
        // No usable criteria (none at all, or all still half-typed) matches
        // nothing — see the design note in PlayerFilter.
        if (filter.MatchesNothing) return false;

        var groups = filter.InMemoryGroups;
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];

            // At least one alternative must hold. An invalid member simply
            // contributes false here, so a group whose alternatives are all
            // broken fails — same outcome as the old poison-the-filter rule,
            // without letting a broken criterion widen anything.
            var inclusive = group.Inclusive;
            if (inclusive.Count > 0)
            {
                var any = false;
                for (var i = 0; i < inclusive.Count && !any; i++)
                {
                    var c = inclusive[i];
                    any = c.IsValid && MatchOne(c, player, ctx);
                }
                if (!any) return false;
            }

            // Every restriction must hold. Invalid is indistinguishable from
            // "didn't match" for a conjunct, so both fail the group.
            var restrictive = group.Restrictive;
            for (var i = 0; i < restrictive.Count; i++)
            {
                var c = restrictive[i];
                if (!c.IsValid || !MatchOne(c, player, ctx)) return false;
            }
        }
        return true;
    }

    private static CompiledCriterion CompileOne(PlayerFilterCriterion source,
                                                EncounterCategoryResolver? categoryResolver)
    {
        var kind = FilterFieldMetadata.GetValueKind(source.Field);
        // Defensive: clamp the operator if it doesn't belong to the field's
        // allowlist (can happen after an enum refactor or a hand-edited JSON).
        var op = FilterFieldMetadata.IsOperatorAllowed(source.Field, source.Operator)
            ? source.Operator
            : FilterFieldMetadata.GetAllowedOperators(source.Field)[0];

        var c = new CompiledCriterion
        {
            Field = source.Field,
            Operator = op,
            StringValue = source.Value ?? string.Empty,
            Status = CriterionStatus.Ready,
        };

        // A text field with nothing typed yet is authoring state, not a rule:
        // freshly added rows start out as "Name contains ''". Under the same-field
        // OR rule such a criterion would match everything and drag its whole group
        // along, so the list would explode mid-keystroke. Dropping it also matches
        // what the old AND semantics did in practice, where an empty Contains was
        // a no-op. Applies to every text operator — "not equal to ''" is just as
        // meaningless; use "Has notes / Has FC tag is false" for blank-ness.
        if (kind == FilterValueKind.Text && string.IsNullOrEmpty(source.Value))
            return c with { Status = CriterionStatus.Incomplete };

        switch (kind)
        {
            case FilterValueKind.Integer:
                if (!int.TryParse(source.Value, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var n))
                    return c with { Status = CriterionStatus.Invalid };
                return c with { IntValue = n };

            case FilterValueKind.JobRoleEnum:
                if (!Enum.TryParse<JobRole>(source.Value, ignoreCase: true, out var role) || role == JobRole.Unknown)
                    return c with { Status = CriterionStatus.Invalid };
                return c with { RoleValue = role };

            case FilterValueKind.RaceEnum:
                // Stored as the decimal row id (1..N). 0 / unparseable means
                // "no race chosen yet" — the row never matches, which is the
                // expected behaviour for an incomplete criterion.
                if (!int.TryParse(source.Value, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var raceId)
                    || raceId <= 0)
                    return c with { Status = CriterionStatus.Invalid };
                return c with { IntValue = raceId };

            case FilterValueKind.GenderEnum:
                // Stored as 0 (male) or 1 (female). Anything else is treated
                // as "unset" — same never-match semantics as the other enums.
                if (!int.TryParse(source.Value, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var genderId)
                    || genderId < 0 || genderId > 1)
                    return c with { Status = CriterionStatus.Invalid };
                return c with { IntValue = genderId };

            case FilterValueKind.OnlineStatusEnum:
                // Persisted as the Lumina OnlineStatus row id (same wire format
                // as the prior Integer kind — pre-existing filters that picked
                // "AFK = 17" keep matching after the editor upgrade).
                if (!int.TryParse(source.Value, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var statusId)
                    || statusId <= 0)
                    return c with { Status = CriterionStatus.Invalid };
                return c with { IntValue = statusId };

            case FilterValueKind.Bool:
                // Bool fields don't take a value — the operator carries it.
                return c;

            case FilterValueKind.EncounteredInPicker:
                {
                    var parsed = EncounteredInValue.Decode(source.Value);
                    if (!parsed.IsSpecified) return c with { Status = CriterionStatus.Invalid };
                    if (parsed.TerritoryId != 0)
                    {
                        // Concrete zone — IntValue carries the TerritoryId,
                        // the SQL builder uses a single-parameter EXISTS.
                        return c with { IntValue = parsed.TerritoryId };
                    }
                    // Category-only — expand to the full set of territory
                    // ids via the resolver. With no resolver available
                    // (legacy code path), the criterion is treated as
                    // invalid: it can't be turned into a usable WHERE.
                    if (categoryResolver is null) return c with { Status = CriterionStatus.Invalid };
                    var ids = categoryResolver.GetTerritoriesForCategory(parsed.Category);
                    if (ids.Count == 0) return c with { Status = CriterionStatus.Invalid };
                    return c with { TerritoryIdSet = ids };
                }

            default: // Text
                return c;
        }
    }

    private static bool MatchOne(CompiledCriterion c, ObservedPlayer p, EvalContext ctx) => c.Field switch
    {
        FilterField.Name => CmpText(p.Name, c),
        FilterField.HomeWorld => CmpText(p.HomeWorld, c),
        FilterField.DataCenter => CmpText(ResolveDataCenter(p, ctx) ?? string.Empty, c),
        FilterField.ClassJob => CmpClassJob(p, ctx, c),
        FilterField.JobRole => CmpEnum(ctx.Lookups.GetClassJobRole(p.ClassJobId), c.RoleValue, c.Operator),
        FilterField.Level => CmpInt(p.Level, c.IntValue, c.Operator),
        FilterField.HoursSinceLastSeen => CmpInt(
            (int)Math.Floor((ctx.UtcNow - p.LastSeen).TotalHours), c.IntValue, c.Operator),
        FilterField.DaysSinceFirstSeen => CmpInt(
            (int)Math.Floor((ctx.UtcNow - p.FirstSeen).TotalDays), c.IntValue, c.Operator),
        FilterField.HasLodestoneId => CmpBool(p.LodestoneId is not null, c.Operator),
        FilterField.CompanyTag => CmpText(p.CompanyTag ?? string.Empty, c),
        FilterField.HasCompanyTag => CmpBool(!string.IsNullOrEmpty(p.CompanyTag), c.Operator),
        FilterField.IsCurrentlyVisible => CmpBool(ctx.CurrentlyVisible.Contains(p.ContentId), c.Operator),
        FilterField.HasUnreadHistory => CmpBool(ctx.HasUnreadHistory(p.ContentId), c.Operator),
        FilterField.HasNotes => CmpBool(p.HasNotes, c.Operator),
        FilterField.OnlineStatusId => CmpInt((int)p.OnlineStatusId, c.IntValue, c.Operator),
        // Race / gender are first-class bytes on the slim ObservedPlayer now.
        // Race == 0 is the sentinel for "no customize bytes ever captured"; any
        // specific-race criterion fails for those until a snapshot lands.
        FilterField.Race => p.Race != 0 && CmpInt(p.Race, c.IntValue, c.Operator),
        FilterField.Gender => p.Race != 0 && CmpInt(p.Gender, c.IntValue, c.Operator),
        // Anything else is DB-resolvable and shouldn't have landed here — the
        // compile step routes those into SqlWhere instead. Return false so a
        // routing bug surfaces as "filter matches nothing" rather than
        // silently ignoring criteria.
        _ => false,
    };

    // ─── Operator helpers ─────────────────────────────────────────────────

    private static bool CmpText(string actual, CompiledCriterion c) => c.Operator switch
    {
        FilterOperator.Equals => actual.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase),
        FilterOperator.NotEquals => !actual.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase),
        FilterOperator.Contains => actual.Contains(c.StringValue, StringComparison.OrdinalIgnoreCase),
        FilterOperator.StartsWith => actual.StartsWith(c.StringValue, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool CmpInt(int actual, int expected, FilterOperator op) => op switch
    {
        FilterOperator.Equals => actual == expected,
        FilterOperator.NotEquals => actual != expected,
        FilterOperator.GreaterThan => actual > expected,
        FilterOperator.LessThan => actual < expected,
        _ => false,
    };

    private static bool CmpBool(bool actual, FilterOperator op) => op switch
    {
        FilterOperator.IsTrue => actual,
        FilterOperator.IsFalse => !actual,
        _ => false,
    };

    private static bool CmpEnum<TEnum>(TEnum actual, TEnum expected, FilterOperator op)
        where TEnum : struct, Enum => op switch
    {
        FilterOperator.Equals => EqualityComparer<TEnum>.Default.Equals(actual, expected),
        FilterOperator.NotEquals => !EqualityComparer<TEnum>.Default.Equals(actual, expected),
        _ => false,
    };

    private static bool CmpClassJob(ObservedPlayer p, EvalContext ctx, CompiledCriterion c)
    {
        // Resolve both the localized name and the 3-letter abbreviation so a
        // user typing "WHM" or "White Mage" or "Weißmagier" all match — the
        // GameData lookup uses the current culture; the abbreviation is
        // language-stable.
        var jobName = ctx.Lookups.GetClassJobName(p.ClassJobId) ?? string.Empty;
        var jobAbbr = ctx.Lookups.GetClassJobAbbreviation(p.ClassJobId) ?? string.Empty;

        return c.Operator switch
        {
            FilterOperator.Equals =>
                jobName.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase) ||
                jobAbbr.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotEquals =>
                !jobName.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase) &&
                !jobAbbr.Equals(c.StringValue, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Contains =>
                jobName.Contains(c.StringValue, StringComparison.OrdinalIgnoreCase) ||
                jobAbbr.Contains(c.StringValue, StringComparison.OrdinalIgnoreCase),
            FilterOperator.StartsWith =>
                jobName.StartsWith(c.StringValue, StringComparison.OrdinalIgnoreCase) ||
                jobAbbr.StartsWith(c.StringValue, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string? ResolveDataCenter(ObservedPlayer p, EvalContext ctx)
    {
        // ObservedPlayer carries the resolved home-world string but not the
        // home-world id — the watcher drops the id when hydrating. Reverse-
        // lookup via name; cheap dictionary scan in IGameDataLookups.
        var worldId = ctx.Lookups.GetWorldIdByName(p.HomeWorld);
        if (worldId is not { } id) return null;
        return ctx.Lookups.GetDataCenterNameByWorldId(id);
    }
}
