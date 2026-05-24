using System.Numerics;
using Dalamud.Interface;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main;

/// <summary>
/// Fallback avatar swatch for characters whose Lodestone portrait isn't loaded
/// yet (or doesn't exist). Maps the (race, gender) bytes from the observation's
/// Customize array to a (colour, icon) pair and delegates the actual rendering
/// to <see cref="NexusRoundedAvatar"/>.
/// </summary>
internal static class AvatarPlaceholder
{
    // FFXIV race IDs (Lumina Race.RowId, 1-8). Index 0 is a neutral fallback for
    // observations where we never got Customize bytes (e.g. older rows hydrated
    // from a pre-feature DB). Hues picked to be distinguishable at a glance —
    // they aren't trying to match any in-game theme exactly.
    private static readonly Vector4[] RaceColors =
    {
        new(0.30f, 0.30f, 0.34f, 1f), // 0 unknown — slate
        new(0.62f, 0.34f, 0.32f, 1f), // 1 Hyur     — terra-red
        new(0.32f, 0.50f, 0.35f, 1f), // 2 Elezen   — forest
        new(0.77f, 0.65f, 0.41f, 1f), // 3 Lalafell — sandstone
        new(0.42f, 0.56f, 0.60f, 1f), // 4 Miqo'te  — teal
        new(0.40f, 0.40f, 0.45f, 1f), // 5 Roegadyn — steel
        new(0.54f, 0.31f, 0.49f, 1f), // 6 Au Ra    — plum
        new(0.55f, 0.42f, 0.27f, 1f), // 7 Hrothgar — bronze
        new(0.48f, 0.36f, 0.56f, 1f), // 8 Viera    — lavender
    };

    public static void Draw(byte[]? customize, float size)
    {
        var raceId = customize is { Length: >= 1 } ? customize[0] : (byte)0;
        var genderId = customize is { Length: >= 2 } ? customize[1] : (byte)255;
        Draw(raceId, genderId, size);
    }

    /// <summary>Overload that takes the race/gender bytes directly. Used by
    /// callers that already have the slim <c>ObservedPlayer.Race</c> /
    /// <c>.Gender</c> values and don't want to allocate a stub byte array
    /// just to call the array-based path. <paramref name="genderId"/> values
    /// outside <c>{0, 1}</c> render as the generic User glyph (matches the
    /// "no customize bytes ever captured" sentinel from the slim
    /// <c>ObservedPlayer</c>).</summary>
    public static void Draw(byte raceId, byte genderId, float size)
    {
        var bg = raceId < RaceColors.Length ? RaceColors[raceId] : RaceColors[0];
        var icon = genderId switch
        {
            0 => FontAwesomeIcon.Mars,
            1 => FontAwesomeIcon.Venus,
            _ => FontAwesomeIcon.User,
        };
        NexusRoundedAvatar.Draw(bg, icon, size);
    }
}
