using NexusKit.GameData;
using NexusKit.Modules.InternalData.Players;

namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Per-evaluation context — assembled once before applying a
/// <see cref="CompiledFilter"/> to the full player list. Keeps the
/// per-criterion evaluator allocation-free; the watcher's currently-visible
/// set and the unread-history index are both cheap lookups.</summary>
public sealed class EvalContext
{
    /// <summary>ContentIds currently visible in-game. Used by the
    /// <see cref="FilterField.IsCurrentlyVisible"/> criterion.</summary>
    public required IReadOnlySet<ulong> CurrentlyVisible { get; init; }

    /// <summary>Returns true if the given content_id has any unread history
    /// rows. The list panel passes <c>MainWindowState.GetUnreadKinds</c> here;
    /// the criterion only needs the predicate result, not the kinds list, so
    /// this is the cheapest API to expose.</summary>
    public required Func<ulong, bool> HasUnreadHistory { get; init; }

    /// <summary>World / DC / class-job lookups, used for the
    /// <see cref="FilterField.DataCenter"/>, <see cref="FilterField.ClassJob"/>
    /// and <see cref="FilterField.JobRole"/> criteria.</summary>
    public required IGameDataLookups Lookups { get; init; }

    /// <summary>Frozen "now" for the duration of one filter pass — so every
    /// row in the same pass uses the same cutoff for time-window criteria,
    /// even if evaluation straddles a millisecond boundary.</summary>
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}
