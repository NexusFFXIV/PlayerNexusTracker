namespace PlayerNexusTracker.Settings.Filters;

/// <summary>
/// Translates the DB-resolvable subset of a compiled filter into a SQL
/// WHERE-fragment plus an ordered parameter list. Every user-supplied value
/// flows through <c>@p{n}</c> placeholders — never string-interpolated — so
/// the resulting fragment is safe to splice into
/// <c>SELECT content_id FROM nexus_filter_player WHERE …</c>.
///
/// <para>An invalid criterion (parse failure at compile time) is allowed to
/// reach this layer; we emit a literal <c>0</c> so the whole AND-conjunction
/// returns no rows — matches the "criterion never matches" contract on the
/// in-memory side.</para>
/// </summary>
internal static class PlayerFilterSqlBuilder
{
    public static (string Where, IReadOnlyList<object> Parameters) Build(IReadOnlyList<CompiledCriterion> dbCriteria)
    {
        if (dbCriteria.Count == 0)
            return ("1", Array.Empty<object>());

        var parts = new List<string>(dbCriteria.Count);
        var parameters = new List<object>(dbCriteria.Count);

        foreach (var c in dbCriteria)
        {
            if (!c.IsValid)
            {
                // One bad criterion poisons the whole filter — return a
                // never-true literal and stop building. Matches the in-memory
                // evaluator's short-circuit-on-invalid behaviour.
                return ("0", Array.Empty<object>());
            }
            parts.Add(BuildOne(c, parameters));
        }
        return (string.Join(" AND ", parts), parameters);
    }

    private static string BuildOne(CompiledCriterion c, List<object> parameters) => c.Field switch
    {
        FilterField.GearScore => IntCompare("gear_score", c, parameters),
        FilterField.MaxJobLevel => IntCompare("max_job_level", c, parameters),
        FilterField.MountCount => IntCompare("mount_count", c, parameters),
        FilterField.MinionCount => IntCompare("minion_count", c, parameters),
        FilterField.AchievementCount => IntCompare("achievement_count", c, parameters),
        FilterField.FcName => TextCompare("fc_name", c, parameters),
        FilterField.FreeCompanyLodestoneId => TextCompare("free_company_lodestone_id", c, parameters),
        FilterField.Notes => TextCompare("notes", c, parameters),
        // Days since the most recent player_encounter. NULL last_encounter_at
        // (never-encountered character) causes the comparison to evaluate to
        // NULL → falsy under SQLite WHERE semantics, so such players never
        // match a specific threshold — matches the GearScore / MountCount
        // "unenriched players never match" behaviour.
        FilterField.DaysSinceLastEncounter => IntCompare(
            "CAST((julianday('now') - julianday(last_encounter_at)) AS INTEGER)", c, parameters),
        // Compound "encountered in zone" — EXISTS subquery against the
        // encounter join, correlated through nexus_filter_player.content_id.
        // For a specific TerritoryId: single-parameter equality. For a
        // category-only criterion: IN(?, ?, …) over the resolver-expanded
        // territory set.
        FilterField.EncounteredIn => BuildEncounteredIn(c, parameters),
        // HasFreeCompany is a presence check on the joined FC row. The view
        // exposes fc_name only when the profile carried a free_company id and
        // the FC catalog had the corresponding row, so NULL there cleanly
        // means "no FC link known".
        FilterField.HasFreeCompany => c.Operator switch
        {
            FilterOperator.IsTrue => "fc_name IS NOT NULL",
            FilterOperator.IsFalse => "fc_name IS NULL",
            _ => "0",
        },
        // Routing bug — Memory-source field made it into the DB branch. Emit
        // 0 so the filter matches nothing rather than silently ignoring it.
        _ => "0",
    };

    private static string IntCompare(string column, CompiledCriterion c, List<object> parameters)
    {
        var p = NextParam(parameters, c.IntValue);
        // NULL-bearing columns (unenriched characters) reliably evaluate to
        // false on every comparison except IS NULL / IS NOT NULL — so a
        // "GearScore > 600" filter naturally excludes players we haven't
        // enriched, which matches the in-memory race/gender sentinel
        // behaviour (race == 0 → no match).
        return c.Operator switch
        {
            FilterOperator.Equals => $"{column} = {p}",
            FilterOperator.NotEquals => $"{column} <> {p}",
            FilterOperator.GreaterThan => $"{column} > {p}",
            FilterOperator.LessThan => $"{column} < {p}",
            _ => "0",
        };
    }

    private static string TextCompare(string column, CompiledCriterion c, List<object> parameters)
    {
        switch (c.Operator)
        {
            case FilterOperator.Equals:
                {
                    var p = NextParam(parameters, c.StringValue);
                    return $"{column} = {p} COLLATE NOCASE";
                }
            case FilterOperator.NotEquals:
                {
                    var p = NextParam(parameters, c.StringValue);
                    return $"{column} <> {p} COLLATE NOCASE";
                }
            case FilterOperator.Contains:
                {
                    var p = NextParam(parameters, "%" + EscapeLike(c.StringValue) + "%");
                    return $"{column} LIKE {p} ESCAPE '\\' COLLATE NOCASE";
                }
            case FilterOperator.StartsWith:
                {
                    var p = NextParam(parameters, EscapeLike(c.StringValue) + "%");
                    return $"{column} LIKE {p} ESCAPE '\\' COLLATE NOCASE";
                }
            default:
                return "0";
        }
    }

    private static string BuildEncounteredIn(CompiledCriterion c, List<object> parameters)
    {
        // The compile step has already vetted the value — when both
        // TerritoryIdSet is null and IntValue == 0 the criterion would
        // be IsValid=false and never reach here. Defensive 0 just in case
        // a future refactor breaks that invariant.
        if (c.TerritoryIdSet is { Count: > 0 } set)
        {
            var paramTokens = new List<string>(set.Count);
            foreach (var tid in set)
                paramTokens.Add(NextParam(parameters, (long)tid));
            var inList = string.Join(",", paramTokens);
            return $"EXISTS (SELECT 1 FROM nexus_internal_player_encounter pe " +
                   $"JOIN nexus_internal_encounter e ON e.id = pe.encounter_id " +
                   $"WHERE pe.content_id = nexus_filter_player.content_id " +
                   $"AND e.territory_type_id IN ({inList}))";
        }
        if (c.IntValue > 0)
        {
            var p = NextParam(parameters, (long)c.IntValue);
            return $"EXISTS (SELECT 1 FROM nexus_internal_player_encounter pe " +
                   $"JOIN nexus_internal_encounter e ON e.id = pe.encounter_id " +
                   $"WHERE pe.content_id = nexus_filter_player.content_id " +
                   $"AND e.territory_type_id = {p})";
        }
        return "0";
    }

    private static string NextParam(List<object> parameters, object value)
    {
        var index = parameters.Count;
        parameters.Add(value);
        return $"@p{index}";
    }

    /// <summary>SQLite LIKE accepts <c>%</c> (any-string) and <c>_</c>
    /// (single-char) as wildcards. When the user types a literal <c>%</c> or
    /// <c>_</c> in a "Contains foo%" filter, we need to escape it so SQLite
    /// matches it verbatim. <c>\</c> is the chosen escape char (matches the
    /// <c>ESCAPE '\'</c> clause in the LIKE site).</summary>
    private static string EscapeLike(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var buf = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '%' or '_' or '\\') buf.Append('\\');
            buf.Append(ch);
        }
        return buf.ToString();
    }
}
