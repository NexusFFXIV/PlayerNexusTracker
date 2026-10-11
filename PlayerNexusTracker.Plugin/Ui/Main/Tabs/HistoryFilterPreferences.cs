using Microsoft.Extensions.Logging;
using NexusKit.Persistence.Settings;

namespace PlayerNexusTracker.Ui.Main.Tabs;

public sealed class HistoryFilterPreferences
{
    private const string StoreKey = "ui.history.filters";
    private readonly ISettingsStore mStore;
    private readonly ILogger<HistoryFilterPreferences> mLog;
    private bool mLoaded;
    private HashSet<string> mSelected = new(StringComparer.Ordinal);

    public HistoryFilterPreferences(ISettingsStore store, ILogger<HistoryFilterPreferences> log)
    { mStore = store; mLog = log; }

    public void EnsureLoaded(IEnumerable<string> all)
    {
        if (mLoaded) return;
        mLoaded = true;
        try
        {
            mSelected = mStore.GetAsync<string[]>(StoreKey).GetAwaiter().GetResult()?.ToHashSet(StringComparer.Ordinal) ?? all.ToHashSet(StringComparer.Ordinal);
            if (mSelected.Remove("snapshot.class_job_id") || mSelected.Remove("snapshot.level"))
                mSelected.Add("snapshot.active_class_job");
        }
        catch (Exception ex) { mLog.LogWarning(ex, "Failed to load history filters; using all"); mSelected = all.ToHashSet(StringComparer.Ordinal); }
    }

    public bool IsSelected(string key) => mSelected.Contains(key);
    public void SetSelected(string key, bool selected)
    {
        if (selected) mSelected.Add(key); else mSelected.Remove(key);
        _ = mStore.SetAsync(StoreKey, mSelected.ToArray());
    }
}
