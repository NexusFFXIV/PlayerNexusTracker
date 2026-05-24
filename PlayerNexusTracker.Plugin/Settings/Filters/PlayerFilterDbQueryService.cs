using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Persistence;

namespace PlayerNexusTracker.Settings.Filters;

internal sealed class PlayerFilterDbQueryService : IPlayerFilterDbQueryService
{
    private readonly INexusDbContextFactory mFactory;
    private readonly ILogger<PlayerFilterDbQueryService> mLog;

    public PlayerFilterDbQueryService(
        INexusDbContextFactory factory,
        ILogger<PlayerFilterDbQueryService> log)
    {
        mFactory = factory;
        mLog = log;
    }

    public async Task<HashSet<ulong>> RunAsync(
        string sqlWhere, IReadOnlyList<object> parameters, CancellationToken ct = default)
    {
        var result = new HashSet<ulong>();
        try
        {
            await using var ctx = await mFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var connection = ctx.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            // sqlWhere comes from PlayerFilterSqlBuilder which only ever emits
            // `@p{n}` placeholders for user-controlled values — never inlines
            // user input. The column / operator parts are switch-mapped from
            // the enum, so they're trusted constants.
            cmd.CommandText = $"SELECT content_id FROM nexus_filter_player WHERE {sqlWhere};";
            BindParameters(cmd, parameters);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add((ulong)reader.GetInt64(0));
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Player-filter DB query failed (where='{Where}')", sqlWhere);
        }
        return result;
    }

    public async Task<IReadOnlyList<ulong>> RunOrderedAsync(
        string? sqlWhere,
        IReadOnlyList<object>? parameters,
        string sortColumn,
        bool descending,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var result = new List<ulong>(Math.Min(limit, 4096));
        try
        {
            // Guard the sort column against accidental injection by allowlisting:
            // the caller (PlayerListPanel) maps a PlayerListSort enum value to one
            // of the known column names, but a defensive check here keeps the
            // bug surface tiny if a future caller forgets the mapping. Anything
            // unrecognised falls back to the always-safe content_id column,
            // making the worst case "no useful ordering" rather than SQL injection.
            var safeColumn = SortColumnGuard(sortColumn);
            var direction = descending ? "DESC" : "ASC";
            // Clamp negative offsets defensively — a bug at the call site
            // shouldn't trip an opaque SQLite syntax error.
            var safeOffset = Math.Max(0, offset);

            await using var ctx = await mFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var connection = ctx.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            var wherePart = string.IsNullOrEmpty(sqlWhere) ? string.Empty : $"WHERE {sqlWhere} ";
            // NULLS LAST regardless of sort direction so unenriched characters
            // (NULL sort_value) consistently land at the bottom instead of
            // jumping to the top on DESC ordering. SQLite supports the syntax
            // since 3.30.
            cmd.CommandText =
                $"SELECT content_id FROM nexus_filter_player {wherePart}" +
                $"ORDER BY {safeColumn} {direction} NULLS LAST LIMIT {limit} OFFSET {safeOffset};";
            if (parameters is not null) BindParameters(cmd, parameters);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add((ulong)reader.GetInt64(0));
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Player-filter ordered query failed (where='{Where}', column='{Col}', offset={Off})",
                sqlWhere ?? string.Empty, sortColumn, offset);
        }
        return result;
    }

    private static void BindParameters(System.Data.Common.DbCommand cmd, IReadOnlyList<object> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = parameters[i];
            cmd.Parameters.Add(p);
        }
    }

    /// <summary>Allowlist of view columns the ORDER BY can safely reference.
    /// Anything off-list falls back to <c>content_id</c> so a future caller
    /// passing a typo doesn't open an injection vector. Columns mirror the
    /// view defined in <c>PlayerFilterViewBuilder</c>.</summary>
    private static string SortColumnGuard(string column) => column switch
    {
        "gear_score" or "max_job_level" or "mount_count"
            or "minion_count" or "achievement_count" or "fc_name"
            or "level" or "name" or "content_id" => column,
        _ => "content_id",
    };
}
