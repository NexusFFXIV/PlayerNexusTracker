using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NexusKit.Persistence.Settings;
using PlayerNexusTracker.Settings.Filters;

namespace PlayerNexusTracker.Ui.Main.Tabs;

/// <summary>
/// Persisted filter selection for <see cref="EncountersTab"/>. Held as a
/// singleton because the encounters tab renders as a static class; this
/// preferences service is the smallest seam that lets the tab round-trip
/// the user's filter choice through <see cref="ISettingsStore"/> across
/// plugin reloads.
/// </summary>
public sealed class EncountersFilterPreferences
{
    // Single flat key — the JSON shape is internal and only this service
    // reads/writes it, so the structured AddSettings<T> schema would be
    // overkill (no UI exposure, no migration story needed yet).
    private const string StoreKey = "ui.encounters.filter";

    private readonly ISettingsStore mStore;
    private readonly ILogger<EncountersFilterPreferences> mLog;
    private bool mLoaded;

    public EncounterTimeRange  TimeRange  { get; private set; } = EncounterTimeRange.All;
    public EncounterZoneFilter ZoneFilter { get; private set; } = EncounterZoneFilter.All;

    public EncountersFilterPreferences(ISettingsStore store,
                                       ILogger<EncountersFilterPreferences> log)
    {
        mStore = store;
        mLog = log;
    }

    /// <summary>
    /// Block-loads the persisted state on first draw. Sync because the
    /// settings store is a single SQLite row read (sub-ms warm, a couple
    /// ms cold) and the alternative — showing default filters for a frame
    /// before the saved choice snaps in — flickers visibly.
    /// </summary>
    public void EnsureLoaded()
    {
        if (mLoaded) return;
        mLoaded = true;
        try
        {
            var state = mStore.GetAsync<PersistedState>(StoreKey).GetAwaiter().GetResult();
            if (state is not null)
            {
                TimeRange = state.TimeRange;
                ZoneFilter = state.ZoneFilter;
            }
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Failed to load encounter filter state; using defaults");
        }
    }

    public void SetTimeRange(EncounterTimeRange value)
    {
        if (TimeRange == value) return;
        TimeRange = value;
        _ = PersistAsync();
    }

    public void SetZoneFilter(EncounterZoneFilter value)
    {
        if (ZoneFilter == value) return;
        ZoneFilter = value;
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        var snapshot = new PersistedState { TimeRange = TimeRange, ZoneFilter = ZoneFilter };
        try
        {
            await mStore.SetAsync(StoreKey, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Failed to persist encounter filter state");
        }
    }

    public sealed class PersistedState
    {
        public EncounterTimeRange  TimeRange  { get; set; }
        public EncounterZoneFilter ZoneFilter { get; set; }
    }
}
