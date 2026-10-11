using System.Globalization;
using NexusKit.Core.Localization;

namespace PlayerNexusTracker.Localization;

/// <summary>
/// Plugin-facing language mode. The framework remains responsible for applying
/// cultures; this service only translates the user's mode into its existing API.
/// </summary>
public sealed class PlayerTrackerLanguageService
{
    private readonly LocalizationManager mLocalization;

    public PlayerTrackerLanguageService(LocalizationManager localization)
    {
        mLocalization = localization;
        Mode = PlayerTrackerLanguageMode.Auto;
    }

    public PlayerTrackerLanguageMode Mode { get; private set; }

    public CultureInfo CurrentCulture => mLocalization.CurrentCulture;

    public event Action<CultureInfo>? CultureChanged
    {
        add => mLocalization.CultureChanged += value;
        remove => mLocalization.CultureChanged -= value;
    }

    /// <summary>Apply a mode immediately; no plugin restart is required.</summary>
    public void SetMode(PlayerTrackerLanguageMode mode)
    {
        Mode = mode;
        mLocalization.SetOverride(mode switch
        {
            PlayerTrackerLanguageMode.English => "en",
            PlayerTrackerLanguageMode.German => "de",
            // Auto and Game intentionally follow the host bridge. This keeps
            // Dalamud language changes live while retaining both user-facing modes.
            _ => null,
        });
    }

    /// <summary>Returns the persisted enum value, tolerating old/invalid settings.</summary>
    public static PlayerTrackerLanguageMode ParseMode(string? value)
        => Enum.TryParse<PlayerTrackerLanguageMode>(value, ignoreCase: true, out var mode)
            ? mode
            : PlayerTrackerLanguageMode.Auto;
}
