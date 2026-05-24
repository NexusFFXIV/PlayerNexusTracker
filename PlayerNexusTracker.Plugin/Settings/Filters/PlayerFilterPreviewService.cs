using Microsoft.Extensions.Logging;
using NexusKit.GameData;
using NexusKit.Modules.InternalData.Players;

namespace PlayerNexusTracker.Settings.Filters;

internal sealed class PlayerFilterPreviewService : IPlayerFilterPreviewService
{
    private static readonly HashSet<ulong> EmptyVisibleSet = new();

    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IPlayerFilterDbQueryService mDbQuery;
    private readonly IGameDataLookups mLookups;
    private readonly EncounterCategoryResolver mCategoryResolver;
    private readonly ILogger<PlayerFilterPreviewService> mLog;

    public PlayerFilterPreviewService(
        IInternalDataPlayerWatcher watcher,
        IPlayerFilterDbQueryService dbQuery,
        IGameDataLookups lookups,
        EncounterCategoryResolver categoryResolver,
        ILogger<PlayerFilterPreviewService> log)
    {
        mWatcher = watcher;
        mDbQuery = dbQuery;
        mLookups = lookups;
        mCategoryResolver = categoryResolver;
        mLog = log;
    }

    public async Task<int> CountMatchesAsync(PlayerFilter draft, CancellationToken ct = default)
    {
        try
        {
            var compiled = PlayerFilterEvaluator.Compile(draft, mCategoryResolver);
            if (compiled.IsEmpty) return 0;

            HashSet<ulong>? allowed = null;
            if (compiled.RequiresDbQuery && compiled.SqlWhere is { } where)
            {
                allowed = await mDbQuery
                    .RunAsync(where, compiled.SqlParameters ?? Array.Empty<object>(), ct)
                    .ConfigureAwait(false);
            }

            // Volatile fields are stubbed so the count reflects "characters
            // this filter could match" rather than "characters matching right
            // this frame". Empty IsCurrentlyVisible set + always-false
            // HasUnreadHistory mean filters touching those criteria will
            // under-count — caveat surfaced via tooltip in the editor.
            var ctx = new EvalContext
            {
                CurrentlyVisible = EmptyVisibleSet,
                HasUnreadHistory = _ => false,
                Lookups = mLookups,
                UtcNow = DateTime.UtcNow,
            };

            var count = 0;
            foreach (var p in mWatcher.Recent)
            {
                if (ct.IsCancellationRequested) return 0;
                if (allowed is not null && !allowed.Contains(p.ContentId)) continue;
                if (PlayerFilterEvaluator.Match(compiled, p, ctx)) count++;
            }
            return count;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Player-filter preview count failed");
            return 0;
        }
    }
}
