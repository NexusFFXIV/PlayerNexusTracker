namespace PlayerNexusTracker.Settings.Filters;

/// <summary>
/// Background preview-evaluation of a draft filter for the editor's "Matches:
/// N" counter. Reuses the production compile + match pipeline but stubs out
/// volatile fields (IsCurrentlyVisible / HasUnreadHistory) — the preview
/// reflects "rows the filter could ever match" rather than the
/// frame-precise list-panel result. Same DB layer as the runtime path.
/// </summary>
public interface IPlayerFilterPreviewService
{
    Task<int> CountMatchesAsync(PlayerFilter draft, CancellationToken ct = default);
}
