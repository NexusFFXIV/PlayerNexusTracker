namespace PlayerNexusTracker.Settings.Filters;

/// <summary>
/// Translates the DB-resolvable subset of a compiled filter into a SQL
/// WHERE-fragment plus an ordered parameter list. Every user-supplied value
/// flows through <c>@p{n}</c> placeholders — never string-interpolated — so
/// the resulting fragment is safe to splice into
/// <c>SELECT content_id FROM nexus_filter_player WHERE …</c>.
///
/// <para>An invalid criterion (parse failure at compile time) is allowed to
/// reach this layer; it emits a literal <c>0</c> so it can never match — the
/// same "criterion never matches" contract the in-memory evaluator applies.</para>
///
/// <para><b>Emission invariant:</b> the returned fragment is either a single
/// atom or an AND-chain whose every element is an atom or a parenthesised
/// expression — it never contains a top-level <c>OR</c>. That is what makes the
/// raw splice into <c>WHERE {fragment}</c> safe, in both
/// <c>PlayerFilterDbQueryService</c> overloads and in anything that later
/// appends its own condition. <c>AND</c> binds tighter than <c>OR</c> in SQL, so
/// an unparenthesised OR group would silently reassociate and ignore its
/// siblings.</para>
///
/// <para><b>No formula-level <c>NOT</c>.</b> The view is built from LEFT JOINs,
/// so most columns are frequently NULL, and <c>NOT NULL</c> is NULL rather than
/// true. Every atom here is either a NULL-propagating comparison or a two-valued
/// predicate (<c>EXISTS</c>, <c>NOT EXISTS</c>, <c>IS NULL</c>), and the formula
/// only ever combines them with AND/OR. A formula monotone in AND/OR matches
/// exactly the two-valued reading "NULL means no match", which is why OR groups
/// behave the same as the old AND chains for unenriched players.</para>
/// </summary>
internal static class PlayerFilterSqlBuilder
{
    /// <summary>Renders field groups into a WHERE fragment. Within a group the
    /// inclusive criteria OR and the restrictive ones AND; groups AND with each
    /// other. See the emission invariant on the class.</summary>
    public static (string Where, IReadOnlyList<object> Parameters) Build(
        IReadOnlyList<CompiledCriterionGroup> groups)
    {
        if (groups.Count == 0)
            return ("1", Array.Empty<object>());

        var groupParts = new List<string>(groups.Count);
        var parameters = new List<object>(groups.Count);

        // Single emission pass in final text order. Grouping already happened at
        // compile time, and nothing may reorder text afterwards: NextParam stamps
        // absolute indices, and parts are not 1:1 with parameters (HasFreeCompany
        // adds none, an EncounteredIn category adds many), so shuffling would
        // desynchronise @pN from the value list — a failure that surfaces only as
        // a silently empty result set.
        foreach (var group in groups)
        {
            var rendered = BuildGroup(group, parameters);
            if (rendered is not null) groupParts.Add(rendered);
        }

        if (groupParts.Count == 0)
            return ("1", Array.Empty<object>());

        return (string.Join(" AND ", groupParts), parameters);
    }

    /// <summary>Renders one field's criteria. Returns null when the group holds
    /// nothing to emit.</summary>
    private static string? BuildGroup(CompiledCriterionGroup group, List<object> parameters)
    {
        var terms = new List<string>(group.Restrictive.Count + 1);

        // Alternatives: OR-ed, and parenthesised whenever there is more than one
        // so the outer AND-chain cannot capture them.
        if (group.Inclusive.Count > 0)
        {
            var alternatives = new List<string>(group.Inclusive.Count);
            for (var i = 0; i < group.Inclusive.Count; i++)
                alternatives.Add(BuildOne(group.Inclusive[i], parameters));

            terms.Add(alternatives.Count == 1
                ? alternatives[0]
                : "(" + string.Join(" OR ", alternatives) + ")");
        }

        // Restrictions: plain conjuncts.
        for (var i = 0; i < group.Restrictive.Count; i++)
            terms.Add(BuildOne(group.Restrictive[i], parameters));

        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            // Wrapped so a mixed group ((a OR b) AND c) stays one unit for the
            // caller's join, keeping the invariant true by construction.
            _ => "(" + string.Join(" AND ", terms) + ")",
        };
    }

    private static string BuildOne(CompiledCriterion c, List<object> parameters)
    {
        // Broken data never matches. Handled per-criterion rather than aborting
        // the whole build: inside an OR group a never-matching member drops out
        // harmlessly, while an all-invalid group still yields false overall.
        if (!c.IsValid) return "0";
        return BuildAtom(c, parameters);
    }

    private static string BuildAtom(CompiledCriterion c, List<object> parameters) => c.Field switch
    {
        FilterField.GearScore => IntCompare("gear_score", c, parameters),
        FilterField.MaxJobLevel => IntCompare("max_job_level", c, parameters),
        FilterField.MountCount => IntCompare("mount_count", c, parameters),
        FilterField.MinionCount => IntCompare("minion_count", c, parameters),
        FilterField.AchievementCount => IntCompare("achievement_count", c, parameters),
        FilterField.FcName => TextCompare("fc_name", c, parameters),
        FilterField.FreeCompanyLodestoneId => TextCompare("free_company_lodestone_id", c, parameters),
        FilterField.Notes => TextCompare("notes", c, parameters),
        FilterField.SearchComment => TextCompare("search_comment", c, parameters),
        // Presence check on the captured search comment. The capture path
        // normalises blank input to NULL, but the empty-string guard keeps a
        // hand-edited or legacy row from reading as "has one".
        FilterField.HasSearchComment => c.Operator switch
        {
            FilterOperator.IsTrue => "(search_comment IS NOT NULL AND search_comment <> '')",
            FilterOperator.IsFalse => "(search_comment IS NULL OR search_comment = '')",
            _ => "0",
        },
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
        // be invalid and never reach here. Defensive 0 just in case
        // a future refactor breaks that invariant.
        string predicate;
        if (c.TerritoryIdSet is { Count: > 0 } set)
        {
            var paramTokens = new List<string>(set.Count);
            foreach (var tid in set)
                paramTokens.Add(NextParam(parameters, (long)tid));
            predicate = $"e.territory_type_id IN ({string.Join(",", paramTokens)})";
        }
        else if (c.IntValue > 0)
        {
            predicate = $"e.territory_type_id = {NextParam(parameters, (long)c.IntValue)}";
        }
        else
        {
            return "0";
        }

        var exists = $"EXISTS (SELECT 1 FROM nexus_internal_player_encounter pe " +
                     $"JOIN nexus_internal_encounter e ON e.id = pe.encounter_id " +
                     $"WHERE pe.content_id = nexus_filter_player.content_id " +
                     $"AND {predicate})";

        // The operator used to be ignored here, so "not encountered in X" quietly
        // behaved as "encountered in X". Now that NotEquals is classified as a
        // restriction and AND-ed into its group, emitting the positive form would
        // turn "not in X" into "in X" — the exact opposite. NOT EXISTS is
        // two-valued, so it does not disturb the no-formula-level-NOT rule.
        return c.Operator == FilterOperator.NotEquals ? $"NOT {exists}" : exists;
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
