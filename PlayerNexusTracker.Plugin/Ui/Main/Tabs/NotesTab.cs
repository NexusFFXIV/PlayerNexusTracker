using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Widgets;

namespace PlayerNexusTracker.Ui.Main.Tabs;

internal static class NotesTab
{
    private const int MaxNotesLength = 8000;
    private const int AutosaveDebounceMs = 800;

    // Per-character edit buffer so switching tabs / players preserves unsaved input.
    // Keyed by ContentId — stable for every observed character, and the source of
    // truth for the persisted notes column lives on observed_player (not on the
    // Lodestone-side player table) so characters without a resolved Lodestone id
    // still get a working notes editor.
    private static readonly Dictionary<ulong, EditState> sBuffers = new();

    public static void Draw(ObservedPlayer observed, ObservedPlayerDetail? detail,
                            MainWindowState state, ILocalizer loc)
    {
        NexusSection.Header(loc.Get("ui.main.tab.notes.header"));

        // Sweep first: any *other* character's buffer that went dirty and aged past
        // the debounce gets flushed too. Without this, a user who types something
        // and immediately switches characters without re-visiting the tab would
        // lose the edit. Cheap — there are at most a handful of buffered players
        // in a session and the predicate short-circuits on the first non-dirty
        // entry.
        FlushOverdueSaves(state, exceptContentId: observed.ContentId);

        var entry = GetOrCreate(observed, detail);

        var edited = ImGui.InputTextMultiline("##notes_editor", ref entry.Buffer, MaxNotesLength,
            new Vector2(-1, 220), ImGuiInputTextFlags.AllowTabInput);
        var deactivated = ImGui.IsItemDeactivatedAfterEdit();

        if (edited) entry.LastEditAt = DateTime.UtcNow;

        var dirty = !string.Equals(entry.Buffer, entry.LoadedSnapshot, StringComparison.Ordinal);

        // Two save triggers, OR'd together:
        //   1. Focus left the input AFTER an edit — capture the most-recent value
        //      immediately so a click into another tab/player flushes synchronously.
        //   2. The user has paused typing past the debounce — typical autosave path.
        if (dirty && (deactivated ||
                      (entry.LastEditAt is { } at &&
                       (DateTime.UtcNow - at).TotalMilliseconds >= AutosaveDebounceMs)))
        {
            TriggerSave(state, observed.ContentId, entry);
        }

        ImGui.Spacing();
        var status = dirty || entry.SaveInFlight
            ? loc.Get("ui.main.tab.notes.status.saving")
            : loc.Get("ui.main.tab.notes.status.saved");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, status);
    }

    private static void TriggerSave(MainWindowState state, ulong contentId, EditState entry)
    {
        var snapshot = entry.Buffer;
        // Mark not-dirty synchronously so the UI doesn't loop-fire saves on every
        // subsequent frame before the save completes. SaveInFlight is the
        // "still spinning" indicator the status line keys on.
        entry.LoadedSnapshot = snapshot;
        entry.LastEditAt = null;
        entry.SaveInFlight = true;
        _ = state.SaveNotesAsync(contentId, snapshot)
            .ContinueWith(_ => entry.SaveInFlight = false,
                TaskScheduler.Default);
    }

    private static void FlushOverdueSaves(MainWindowState state, ulong exceptContentId)
    {
        foreach (var (cid, entry) in sBuffers)
        {
            if (cid == exceptContentId) continue;
            if (string.Equals(entry.Buffer, entry.LoadedSnapshot, StringComparison.Ordinal)) continue;
            if (entry.LastEditAt is not { } at) continue;
            if ((DateTime.UtcNow - at).TotalMilliseconds < AutosaveDebounceMs) continue;
            TriggerSave(state, cid, entry);
        }
    }

    /// <summary>Pulls (and caches) the edit state for this character. The
    /// persisted notes value lives on the lazy-loaded
    /// <see cref="ObservedPlayerDetail"/> — while that's still null (first
    /// frame after a selection switch), we just keep whatever's in the buffer
    /// and don't initialize a "loaded snapshot". Once the detail lands and
    /// the user has no pending edits, we re-sync so the new value shows up;
    /// if the user has dirty changes we leave them alone — overwriting their
    /// typing on every redraw would be hostile.</summary>
    private static EditState GetOrCreate(ObservedPlayer observed, ObservedPlayerDetail? detail)
    {
        // Detail still loading → only set up an empty buffer the first time we
        // see this player; never clobber a typed-but-not-yet-saved value.
        if (detail is null)
        {
            if (!sBuffers.TryGetValue(observed.ContentId, out var pending))
            {
                pending = new EditState();
                sBuffers[observed.ContentId] = pending;
            }
            return pending;
        }

        var persisted = detail.Notes ?? string.Empty;
        if (!sBuffers.TryGetValue(observed.ContentId, out var entry))
        {
            entry = new EditState { Buffer = persisted, LoadedSnapshot = persisted };
            sBuffers[observed.ContentId] = entry;
            return entry;
        }

        if (entry.LoadedSnapshot != persisted &&
            string.Equals(entry.Buffer, entry.LoadedSnapshot, StringComparison.Ordinal))
        {
            entry.Buffer = persisted;
            entry.LoadedSnapshot = persisted;
        }
        return entry;
    }

    private sealed class EditState
    {
        public string Buffer = string.Empty;
        public string LoadedSnapshot = string.Empty;
        public DateTime? LastEditAt;
        public volatile bool SaveInFlight;
    }
}
