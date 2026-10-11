namespace PlayerNexusTracker.Localization;

/// <summary>How PlayerNexusTracker chooses its UI language.</summary>
public enum PlayerTrackerLanguageMode
{
    /// <summary>Follow the host/game language.</summary>
    Auto,

    /// <summary>Follow the language reported by Dalamud/the game.</summary>
    Game,

    English,
    German,
}
