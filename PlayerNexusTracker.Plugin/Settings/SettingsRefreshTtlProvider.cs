using NexusKit.Modules.PlayerEnrichment;
using NexusKit.Persistence.Settings;

namespace PlayerNexusTracker.Settings;

/// <summary>
/// Plugin-side <see cref="IRefreshTtlProvider"/> that pulls
/// <see cref="TrackerSettings.RefreshTtlDays"/> from <see cref="ISettingsStore"/>.
/// <para>The store API is async-only; we eagerly load the value on construction
/// and cache it, then re-read in the background whenever the queue asks. The
/// auto-settings UI saves changes back to the store, so the cached value will
/// drift until the next reload — acceptable for a TTL knob the user doesn't
/// change often.</para>
/// </summary>
internal sealed class SettingsRefreshTtlProvider : IRefreshTtlProvider
{
    private const int FallbackDays = 7;
    private readonly ISettingsStore mStore;
    private int mCachedDays = FallbackDays;

    public SettingsRefreshTtlProvider(ISettingsStore store)
    {
        mStore = store;
        _ = LoadAsync();
    }

    public TimeSpan GetTtl() => TimeSpan.FromDays(Math.Max(1, mCachedDays));

    private async Task LoadAsync()
    {
        try
        {
            var s = await mStore.GetAsync<TrackerSettings>("config").ConfigureAwait(false);
            if (s is { RefreshTtlDays: > 0 }) mCachedDays = s.RefreshTtlDays;
        }
        catch { /* keep the default — TTL load is non-critical */ }
    }
}
