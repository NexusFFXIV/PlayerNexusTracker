using Dalamud.Plugin.Services;
using NexusKit.Core;

namespace PlayerNexusTracker.Composition;

/// <summary>
/// Adapter that exposes Dalamud's <see cref="IClientState"/> as the
/// framework-level <see cref="ISessionStateProvider"/>. Registered as a
/// singleton; the host's <c>LifetimeBridge</c> wires its events into
/// <c>PluginLifetime</c> automatically.
/// </summary>
internal sealed class DalamudSessionStateProvider : ISessionStateProvider, IDisposable
{
    private readonly IClientState mClient;
    private readonly IClientState.LogoutDelegate mLogoutForwarder;
    private bool mDisposed;

    public DalamudSessionStateProvider(IClientState client)
    {
        mClient = client;
        // Login is parameterless on Dalamud's IClientState; Logout carries
        // (int type, int code) which we discard — the bridge only cares
        // that the session ended, not why.
        mLogoutForwarder = (_, _) => Deactivated?.Invoke();
        mClient.Login += OnLogin;
        mClient.Logout += mLogoutForwarder;
    }

    public bool IsActive => mClient.IsLoggedIn;
    public event Action? Activated;
    public event Action? Deactivated;

    private void OnLogin() => Activated?.Invoke();

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mClient.Login -= OnLogin;
        mClient.Logout -= mLogoutForwarder;
    }
}
