namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Catchall bus that every notification producer feeds when its own
/// detection lands. The bus debounces by contentId and re-emits a single
/// aggregated event after a short quiet period — the general-change
/// producer subscribes and publishes one "X has changed" line per coalesced
/// burst regardless of how many producers (history, collection-growth,
/// background-worker resolves / failures, …) reported events for the same
/// player.
/// <para>Every producer must call <see cref="Signal"/> after its own
/// publish so users running with only the general notification on still
/// hear about every change. The general-change producer is the only
/// subscriber to the aggregator's <c>Aggregated</c> event.</para>
/// <para>Public so producers can inject the interface without taking a
/// dependency on the concrete <see cref="PlayerChangeAggregator"/>.</para>
/// </summary>
public interface IPlayerChangeSignal
{
    /// <summary>Record that something changed for the given character.
    /// Caller doesn't need to specify what — the debouncer just needs the
    /// id. Repeated calls within the debounce window coalesce into one
    /// aggregated event for that character.</summary>
    void Signal(ulong contentId);
}
