namespace PlayerNexusTracker.Settings.Filters;

/// <summary>
/// Runs the SQL pre-filter (and optional ordering) for compiled filters whose
/// criteria — or the active sort key — touch Lodestone-side fields. Splices
/// the WHERE-fragment produced by <see cref="PlayerFilterSqlBuilder"/> into a
/// <c>SELECT content_id FROM nexus_filter_player</c>; an optional ORDER BY
/// + LIMIT layers cleanly on top when a DB-resolved sort is active.
/// </summary>
public interface IPlayerFilterDbQueryService
{
    /// <summary>Unordered pre-narrow. Used when sort is resolvable in-memory
    /// — the panel still needs the candidate <see cref="HashSet"/> from the
    /// view but does final ordering on the slim <c>ObservedPlayer</c>.</summary>
    Task<HashSet<ulong>> RunAsync(string sqlWhere, IReadOnlyList<object> parameters, CancellationToken ct = default);

    /// <summary>Ordered candidate page, capped at <paramref name="limit"/>
    /// and starting after <paramref name="offset"/> rows. Used when the
    /// active sort is DB-resolved (gear score, mount count, …); the panel
    /// pages through results as the user scrolls toward the end of the
    /// rendered list. A returned page shorter than <paramref name="limit"/>
    /// signals end-of-stream — no further OFFSETs will yield more rows.
    /// <paramref name="sqlWhere"/> may be null when only the sort needs the
    /// view and no user filter is active.</summary>
    Task<IReadOnlyList<ulong>> RunOrderedAsync(
        string? sqlWhere,
        IReadOnlyList<object>? parameters,
        string sortColumn,
        bool descending,
        int limit,
        int offset,
        CancellationToken ct = default);
}
