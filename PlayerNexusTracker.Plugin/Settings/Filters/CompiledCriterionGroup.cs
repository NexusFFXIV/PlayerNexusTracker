namespace PlayerNexusTracker.Settings.Filters;

/// <summary>All criteria of one <see cref="FilterField"/>, split by operator
/// polarity. Grouping is derived from the field alone — nothing about it is
/// persisted, so no stored filter needed migrating when the rule changed.
///
/// <para>Group semantics:
/// <c>(Inclusive.Count == 0 || any Inclusive matches) &amp;&amp; (all Restrictive match)</c>.
/// Groups AND with each other. So two "Name contains" criteria are alternatives,
/// while "Level &gt; 80" plus "Level &lt; 90" still brackets a range.</para>
///
/// <para>An empty <see cref="Inclusive"/> list means "this field states no
/// alternatives", which must not be read as "nothing matches" — a group holding
/// only restrictive criteria is satisfied by passing all of them.</para>
/// </summary>
internal readonly struct CompiledCriterionGroup
{
    public required FilterField Field { get; init; }

    /// <summary>OR-ed members. Empty when the field only carries restrictions.</summary>
    public required IReadOnlyList<CompiledCriterion> Inclusive { get; init; }

    /// <summary>AND-ed members. Empty when the field only states alternatives.</summary>
    public required IReadOnlyList<CompiledCriterion> Restrictive { get; init; }
}

/// <summary>Builds <see cref="CompiledCriterionGroup"/>s from a flat criterion
/// list. Deliberately the single implementation shared by the in-memory
/// evaluator and the SQL builder: if the two phases grouped independently they
/// could disagree about how one filter reads, and the disagreement would only
/// surface as a wrong player list.
///
/// <para>Called once per compile per phase — never per row. <c>Match</c> runs
/// inside a <c>Where</c> over every candidate on every frame, so grouping there
/// would allocate per player per frame.</para>
/// </summary>
internal static class CompiledCriterionGrouper
{
    /// <summary>Groups by <see cref="CompiledCriterion.Field"/>, preserving the
    /// order in which fields first appear and, within each polarity bucket, the
    /// original criterion order. Stable order keeps the emitted SQL — and its
    /// <c>@pN</c> numbering — reproducible for a given filter.
    /// <para>The filter editor renders its rows with the same field-first-appearance
    /// grouping (<c>PlayerFilterSettingsSection.BuildEditorGroups</c>) so the blocks
    /// on screen are the blocks evaluated here. It cannot call this method — it works
    /// on the persisted <see cref="PlayerFilterCriterion"/>, not the compiled form —
    /// so if the ordering rule changes, change it in both places or the editor will
    /// misrepresent the semantics.</para></summary>
    public static IReadOnlyList<CompiledCriterionGroup> Group(
        IReadOnlyList<CompiledCriterion> criteria)
    {
        if (criteria.Count == 0) return Array.Empty<CompiledCriterionGroup>();

        // Field order of first appearance. A plain list beats a dictionary here:
        // a filter holds a handful of criteria, so the linear scan is cheaper
        // than hashing and it gives deterministic ordering for free.
        var fields = new List<FilterField>(criteria.Count);
        for (var i = 0; i < criteria.Count; i++)
        {
            if (!fields.Contains(criteria[i].Field))
                fields.Add(criteria[i].Field);
        }

        var groups = new List<CompiledCriterionGroup>(fields.Count);
        foreach (var field in fields)
        {
            List<CompiledCriterion>? inclusive = null;
            List<CompiledCriterion>? restrictive = null;
            for (var i = 0; i < criteria.Count; i++)
            {
                var c = criteria[i];
                if (c.Field != field) continue;
                if (FilterFieldMetadata.IsInclusive(c.Operator))
                    (inclusive ??= new List<CompiledCriterion>(2)).Add(c);
                else
                    (restrictive ??= new List<CompiledCriterion>(2)).Add(c);
            }

            groups.Add(new CompiledCriterionGroup
            {
                Field = field,
                Inclusive = inclusive ?? (IReadOnlyList<CompiledCriterion>)Array.Empty<CompiledCriterion>(),
                Restrictive = restrictive ?? (IReadOnlyList<CompiledCriterion>)Array.Empty<CompiledCriterion>(),
            });
        }
        return groups;
    }
}
