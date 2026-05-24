namespace PlayerNexusTracker.Settings;

/// <summary>Persisted player-list sort state. Stored as a single POCO under
/// <see cref="StoreKey"/>, like <c>PlayerFilterCollection</c>, instead of two
/// scattered scalars — keeps the settings shape tidy and makes future
/// additions (per-sort metadata, multi-key sorts) painless.
///
/// <para>The field is persisted as the underlying <c>PlayerListSortField</c>
/// enum's int value to avoid leaking that internal type into a public
/// settings DTO. The list panel maps it back at load time.</para></summary>
public sealed class PlayerListSortPreference
{
    public const string StoreKey = "ui.pntracker.main.list.sort.preference";

    /// <summary>Index into <c>PlayerListSortField</c>. 0 = Name, see the
    /// enum definition in <c>PlayerListPanel</c> for the full ordering.</summary>
    public int FieldIndex { get; set; }

    /// <summary>True when the active sort applies in descending order.</summary>
    public bool Descending { get; set; }
}
