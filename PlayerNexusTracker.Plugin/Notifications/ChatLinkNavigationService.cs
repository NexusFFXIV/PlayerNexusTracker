using System.Globalization;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Ui.Abstractions;
using PlayerNexusTracker.Settings.Filters;
using PlayerNexusTracker.Ui;

namespace PlayerNexusTracker.Notifications;

/// <summary>
/// Makes player names and Free Company labels inside chat notifications
/// clickable. Each clickable segment gets a <see cref="DalamudLinkPayload"/>
/// (registered lazily, deduped by a logical key, reused across notifications,
/// removed on dispose); <see cref="Weave"/> stitches the plain-text and link
/// segments back into one <see cref="SeString"/>.
/// <list type="bullet">
/// <item>A player name links to that player's detail view.</item>
/// <item>An FC label inside a player-history line links to that same player
/// and switches to the Free Company tab.</item>
/// <item>An FC label inside an FC-catalog line (which carries no player) links
/// to a tracked member of that FC — resolved on click — and switches to the
/// Free Company tab.</item>
/// </list>
/// <para>Producers run on the thread pool, so registration is lock-guarded.
/// Window mutations go through <see cref="IWindowManager.Invoke{T}"/>, which
/// marshals onto Dalamud's framework thread — so the async FC-member lookup can
/// hand off its selection without this service touching the framework itself.</para>
/// </summary>
internal sealed class ChatLinkNavigationService : IDisposable
{
    private readonly IChatGui mChat;
    private readonly IWindowManager mWindows;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IPlayerFilterDbQueryService mFilterDb;
    private readonly ILogger<ChatLinkNavigationService> mLog;

    private readonly object mLock = new();

    // logical key -> its (commandId, payload). One handler per distinct key
    // (e.g. "p:{cid}", "pfc:{cid}", "fc:{lodestoneId}"); the handler closes over
    // the click action so no reverse map is needed. The commandId is retained
    // only so Dispose can call RemoveChatLinkHandler.
    private readonly Dictionary<string, (uint CommandId, DalamudLinkPayload Payload)> mLinks = new();
    private uint mNextCommandId = 1;
    private bool mDisposed;

    public ChatLinkNavigationService(
        IChatGui chat,
        IWindowManager windows,
        IInternalDataPlayerWatcher watcher,
        IPlayerFilterDbQueryService filterDb,
        ILogger<ChatLinkNavigationService> log)
    {
        mChat = chat;
        mWindows = windows;
        mWatcher = watcher;
        mFilterDb = filterDb;
        mLog = log;
    }

    // ----- Producer-facing builders -----------------------------------------

    /// <summary>A line whose only link is the player name (<c>{0}</c>). Used by
    /// the producers that mention just one player.</summary>
    public SeString BuildPlayerLinkedLine(ulong contentId, string displayName,
                                          string format, params object[] tailArgs)
    {
        var text = SafeFormat(format, BuildArgs(displayName, tailArgs));
        return Weave(text, PlayerSpan(displayName, contentId));
    }

    /// <summary>A player-history line (<c>{0}</c> = player name, <c>{1}</c> =
    /// change text). The name links to the player; every FC label embedded in
    /// the change links to that same player on the Free Company tab.
    /// <paramref name="fcLabels"/> must be given in the order they appear in the
    /// change text (old before new for a switch).</summary>
    public SeString BuildPlayerHistoryLine(ulong contentId, string displayName, string change,
                                           IReadOnlyList<string> fcLabels, string format)
    {
        var text = SafeFormat(format, new object[] { displayName, change });
        var spans = new List<LinkSpan>(1 + fcLabels.Count) { PlayerSpan(displayName, contentId) };
        foreach (var label in fcLabels)
            spans.Add(PlayerFcSpan(label, contentId));
        return Weave(text, spans.ToArray());
    }

    /// <summary>An FC-catalog line (<c>{0}</c> = FC label). The label links to a
    /// tracked member of that FC (resolved on click) and switches to the Free
    /// Company tab.</summary>
    public SeString BuildFreeCompanyLine(string fcLodestoneId, string fcLabel, string format)
    {
        var text = SafeFormat(format, new object[] { fcLabel });
        return Weave(text, FcMemberSpan(fcLabel, fcLodestoneId));
    }

    // ----- Span weaving ------------------------------------------------------

    private readonly record struct LinkSpan(string Text, string Key, Action OnClick);

    private LinkSpan PlayerSpan(string text, ulong contentId)
        => new(text, "p:" + contentId, () => NavigatePlayer(contentId, freeCompanyTab: false));

    private LinkSpan PlayerFcSpan(string text, ulong contentId)
        => new(text, "pfc:" + contentId, () => NavigatePlayer(contentId, freeCompanyTab: true));

    private LinkSpan FcMemberSpan(string text, string fcLodestoneId)
        => new(text, "fc:" + fcLodestoneId, () => NavigateFcMember(fcLodestoneId));

    /// <summary>Rebuilds <paramref name="text"/> as an SeString, wrapping each
    /// span's literal text in a clickable link. Spans are matched in order,
    /// each searched after the previous match, so they must be supplied in the
    /// order they occur in the text. A span whose text can't be located (or
    /// whose payload fails to register) is left as plain text.</summary>
    private SeString Weave(string text, params LinkSpan[] spans)
    {
        var b = new SeStringBuilder();
        var cursor = 0;
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            var idx = text.IndexOf(span.Text, cursor, StringComparison.Ordinal);
            if (idx < 0) continue;

            if (idx > cursor) b.AddText(text[cursor..idx]);

            DalamudLinkPayload? payload = null;
            try { payload = GetOrCreatePayload(span.Key, span.OnClick); }
            catch (Exception ex) { mLog.LogWarning(ex, "ChatLink: payload registration failed for {Key}", span.Key); }

            if (payload is null)
            {
                b.AddText(span.Text);
            }
            else
            {
                b.Add(payload);
                b.AddText(span.Text);
                b.Add(RawPayload.LinkTerminator);
            }
            cursor = idx + span.Text.Length;
        }
        if (cursor < text.Length) b.AddText(text[cursor..]);
        return b.Build();
    }

    private DalamudLinkPayload GetOrCreatePayload(string key, Action onClick)
    {
        lock (mLock)
        {
            if (mLinks.TryGetValue(key, out var entry))
                return entry.Payload;

            var commandId = mNextCommandId++;
            var payload = mChat.AddChatLinkHandler(commandId, (_, _) => onClick());
            mLinks[key] = (commandId, payload);
            return payload;
        }
    }

    // ----- Navigation --------------------------------------------------------

    private void NavigatePlayer(ulong contentId, bool freeCompanyTab)
    {
        // The player may have aged out of the in-memory watcher map — no-op
        // rather than surfacing an error for a stale chat line.
        if (!mWatcher.TryGetObserved(contentId, out var observed) || observed is not { } player)
            return;
        OpenAndSelect(player, freeCompanyTab);
    }

    private void NavigateFcMember(string fcLodestoneId)
        => _ = NavigateFcMemberAsync(fcLodestoneId);

    private async Task NavigateFcMemberAsync(string fcLodestoneId)
    {
        try
        {
            // The FC-catalog line carries no player, so pick the most-recently-
            // seen locally-tracked member of that FC via the same filter view
            // the list panel uses.
            var ids = await mFilterDb.RunAsync(
                "free_company_lodestone_id = @p0",
                new object[] { fcLodestoneId }).ConfigureAwait(false);

            ObservedPlayer? best = null;
            foreach (var id in ids)
                if (mWatcher.TryGetObserved(id, out var p) && p is { } member
                    && (best is null || member.LastSeen > best.LastSeen))
                    best = member;

            if (best is not { } chosen) return;

            // OpenAndSelect routes through IWindowManager.Invoke, which marshals
            // window mutations onto the framework thread itself — safe to call
            // from this background continuation.
            OpenAndSelect(chosen, freeCompanyTab: true);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "ChatLink: FC-member navigation failed for {FcId}", fcLodestoneId);
        }
    }

    private void OpenAndSelect(ObservedPlayer player, bool freeCompanyTab)
    {
        // IWindowManager.Invoke resolves the window and runs this on the
        // framework thread (inline if we're already on it).
        mWindows.Invoke<PnTrackerMainWindow>(w =>
        {
            w.IsOpen = true;
            if (freeCompanyTab) w.SelectPlayerOnFreeCompany(player);
            else w.SelectPlayer(player);
        });
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        lock (mLock)
        {
            foreach (var (_, entry) in mLinks)
            {
                try { mChat.RemoveChatLinkHandler(entry.CommandId); }
                catch (Exception ex) { mLog.LogWarning(ex, "ChatLink: handler removal failed for {Cmd}", entry.CommandId); }
            }
            mLinks.Clear();
        }
    }

    // ----- Helpers -----------------------------------------------------------

    private static object[] BuildArgs(object head, object[] tailArgs)
    {
        var args = new object[1 + (tailArgs?.Length ?? 0)];
        args[0] = head;
        if (tailArgs is { Length: > 0 })
            Array.Copy(tailArgs, 0, args, 1, tailArgs.Length);
        return args;
    }

    private static string SafeFormat(string format, object[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; }
    }
}
