using NexusKit.Ui.AutoSettings;
using PlayerNexusTracker.Settings;

namespace PlayerNexusTracker.Localization;

/// <summary>Applies settings changes that affect runtime localization immediately.</summary>
public sealed class PlayerTrackerSettingsChangeObserver : ISettingsChangeObserver
{
    private readonly PlayerTrackerLanguageService mLanguage;

    public PlayerTrackerSettingsChangeObserver(PlayerTrackerLanguageService language)
    {
        mLanguage = language;
    }

    public void OnChanged(object settings, string propertyName)
    {
        if (settings is TrackerSettings tracker
            && propertyName == nameof(TrackerSettings.LanguageMode))
            mLanguage.SetMode(PlayerTrackerLanguageService.ParseMode(tracker.LanguageMode));
    }
}
