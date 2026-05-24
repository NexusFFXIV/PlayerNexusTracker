using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusKit.Persistence.Settings;

namespace PlayerNexusTracker.Settings.Filters;

/// <summary>Outcome of <see cref="PlayerFilterRegistry.ImportFromString"/>.
/// Numbers cover what was actually applied; <see cref="OverwrittenNames"/>
/// gives the user a glance at which existing filters were replaced, so the
/// warning in the UI isn't just a count.</summary>
public sealed class FilterImportResult
{
    public bool ParseError { get; init; }
    public int Added { get; init; }
    public int Overwritten { get; init; }
    public int SkippedAtCap { get; init; }
    public IReadOnlyList<string> OverwrittenNames { get; init; } = Array.Empty<string>();

    public bool HasChanges => Added > 0 || Overwritten > 0;
}

/// <summary>
/// Owns the in-memory copy of <see cref="PlayerFilterCollection"/> and
/// brokers persistence to the underlying <see cref="ISettingsStore"/>.
/// Mirrors <c>NexusKit.ChatNotifications.Internal.ChatNotificationRegistry</c>
/// one-to-one — settings UI mutates the cached list in place, then calls
/// <see cref="PersistAsync"/>; the player-list panel reads the cached list
/// every frame, so edits become visible without a plugin reload.
/// <para>All mutation paths run on the framework thread (settings UI + list
/// panel both render through Dalamud's draw callback), so no internal lock is
/// needed for the list itself. Persistence runs on a thread-pool task —
/// concurrent persists are coalesced so a rapid sequence of editor changes
/// only commits the latest snapshot once the previous write completes.</para>
/// <para>Filter values flow user-input string → JSON blob → EF Core
/// parameter on persist; no raw SQL is generated anywhere in this path, so
/// the persisted strings cannot influence query structure.</para>
/// </summary>
public sealed class PlayerFilterRegistry
{
    /// <summary>Hard ceiling on how many filters can survive a load. Beyond
    /// this we trim and log — protects against a corrupted settings JSON
    /// or a deliberate denial-of-service via hand-edit. The combo dropdown
    /// is also unusable past a few dozen entries, so the cap is generous.</summary>
    public const int HardCap = 200;

    /// <summary>Prefix on every exported string — acts as a magic number
    /// (lets the importer reject random pastes) AND a format version. Bump
    /// the trailing digit if the serialized shape ever changes
    /// incompatibly; new imports recognise both versions during a grace
    /// period, old plugin builds reject the newer one cleanly.</summary>
    private const string ExportPrefix = "PNTF1:";

    /// <summary>Soft ceiling: the editor refuses to create a new filter past
    /// this number but still tolerates load-time counts up to
    /// <see cref="HardCap"/>. Keeps everyday usage from accidentally building
    /// an unmanageable dropdown.</summary>
    public const int SoftCap = 50;

    private readonly ISettingsStore mStore;
    private readonly ILogger<PlayerFilterRegistry> mLog;
    private PlayerFilterCollection mCollection = new();

    // Persist coalescing — see PersistAsync. Read on the framework thread,
    // mutated through Interlocked.Exchange so two concurrent persist requests
    // serialize cleanly without a SemaphoreSlim.
    private Task mInFlightPersist = Task.CompletedTask;
    private bool mPendingFollowupPersist;
    private readonly object mPersistLock = new();

    public PlayerFilterRegistry(ISettingsStore store, ILogger<PlayerFilterRegistry> log)
    {
        mStore = store;
        mLog = log;
        _ = LoadAsync();
    }

    /// <summary>Cached filter list. Mutated in place by the settings UI;
    /// read every frame by the list panel. Reference is replaced wholesale
    /// only on load.</summary>
    public IReadOnlyList<PlayerFilter> Filters => mCollection.Filters;

    /// <summary>Returns the filter with the given id, or null when no longer
    /// present. The list panel uses this to resolve the currently-selected
    /// filter on every frame — a null return drives the silent fallback to
    /// the "Current" system filter.</summary>
    public PlayerFilter? FindById(Guid id)
    {
        var list = mCollection.Filters;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) return list[i];
        return null;
    }

    /// <summary>Adds a new filter (assigning a fresh Guid) and returns it.
    /// Returns null if the soft cap is reached — caller should surface a
    /// localized "limit reached" message to the user. Caller is responsible
    /// for invoking <see cref="PersistAsync"/>.</summary>
    public PlayerFilter? Add(string name)
    {
        if (mCollection.Filters.Count >= SoftCap) return null;
        var f = new PlayerFilter { Id = Guid.NewGuid(), Name = name ?? string.Empty };
        mCollection.Filters.Add(f);
        return f;
    }

    /// <summary>Removes the filter with the given id. Returns true if a
    /// filter was actually removed.</summary>
    public bool Remove(Guid id)
    {
        var list = mCollection.Filters;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id)
            {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Reorders the filter with <paramref name="id"/> by
    /// <paramref name="delta"/> positions (negative = move toward the start,
    /// positive = move toward the end). Returns true if the move was
    /// applied; false if the filter isn't present or the new position would
    /// fall outside the list. The list-panel dropdown reads from the same
    /// backing collection, so the new order is visible the next frame.
    /// Caller is responsible for invoking <see cref="PersistAsync"/>.</summary>
    public bool Move(Guid id, int delta)
    {
        if (delta == 0) return false;
        var list = mCollection.Filters;
        var idx = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id) { idx = i; break; }
        }
        if (idx < 0) return false;

        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= list.Count) return false;

        var f = list[idx];
        list.RemoveAt(idx);
        list.Insert(newIdx, f);
        return true;
    }

    /// <summary>Clones the filter with <paramref name="sourceId"/>, deep-copies
    /// its criteria, and returns the new filter. The clone gets a fresh Guid
    /// (so dropdown selection and persistence stay independent) and the
    /// caller-supplied <paramref name="cloneName"/> as its display name.
    /// Returns null when the soft cap is reached or the source has gone
    /// missing — the editor's clone button surfaces the cap reason inline.
    /// Caller is responsible for invoking <see cref="PersistAsync"/>.</summary>
    public PlayerFilter? Duplicate(Guid sourceId, string cloneName)
    {
        if (mCollection.Filters.Count >= SoftCap) return null;
        var source = FindById(sourceId);
        if (source is null) return null;

        var clone = new PlayerFilter
        {
            Id = Guid.NewGuid(),
            Name = cloneName ?? string.Empty,
            // Each criterion is a small POCO with three value-type-ish props
            // (Field, Operator, Value). Independent copies are mandatory so
            // editing the clone doesn't reach back into the source.
            Criteria = source.Criteria
                .Select(c => new PlayerFilterCriterion
                {
                    Field = c.Field,
                    Operator = c.Operator,
                    Value = c.Value,
                })
                .ToList(),
        };
        mCollection.Filters.Add(clone);
        return clone;
    }

    /// <summary>Persists the cached collection to the settings store.
    /// Coalesces overlapping calls: while a persist is in flight, additional
    /// requests set a "do another one when this finishes" flag rather than
    /// queuing N independent writes. Settling cost is one extra write at
    /// most, even under editor spam (keyboard input on the rename field).</summary>
    public Task PersistAsync(CancellationToken ct = default)
    {
        lock (mPersistLock)
        {
            if (!mInFlightPersist.IsCompleted)
            {
                mPendingFollowupPersist = true;
                return mInFlightPersist;
            }
            mInFlightPersist = PersistCoreAsync(ct);
            return mInFlightPersist;
        }
    }

    private async Task PersistCoreAsync(CancellationToken ct)
    {
        while (true)
        {
            // Snapshot the collection's CURRENT shape — the editor may have
            // mutated it further between WaitAsync and now; the persist always
            // writes the freshest state, which is what the user expects.
            try
            {
                await mStore.SetAsync(PlayerFilterCollection.StoreKey, mCollection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                mLog.LogWarning(ex, "PlayerFilterRegistry: persist failed");
                return;
            }

            lock (mPersistLock)
            {
                if (!mPendingFollowupPersist) return;
                mPendingFollowupPersist = false;
            }
        }
    }

    /// <summary>Produces a single-line opaque token that round-trips through
    /// <see cref="ImportFromString"/>. The payload is gzip-compressed JSON in
    /// base64 with a version prefix — compact enough for chat / email and
    /// stable across plugin restarts because the inner JSON mirrors the
    /// persisted <c>PlayerFilterCollection</c> POCO exactly. All filter
    /// values flow through JSON serialization and base64 encoding before
    /// leaving the registry; no SQL is involved on either side.</summary>
    public string ExportToString() => Encode(mCollection);

    /// <summary>Same format as <see cref="ExportToString"/> but with just the
    /// one filter — wraps it in an ad-hoc collection so the importer can use
    /// the single decode path for both bulk and single-filter payloads.
    /// Returns null when the id isn't in the registry.</summary>
    public string? ExportFilter(Guid id)
    {
        var f = FindById(id);
        if (f is null) return null;
        var wrapper = new PlayerFilterCollection();
        wrapper.Filters.Add(f);
        return Encode(wrapper);
    }

    private static string Encode(PlayerFilterCollection collection)
    {
        var json = JsonSerializer.Serialize(collection);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            sw.Write(json);
        }
        return ExportPrefix + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Applies an exported token to the cached collection. Filters
    /// whose Id already exists are overwritten in place (preserving list
    /// position); new Ids are appended until <see cref="HardCap"/> is hit,
    /// after which further new filters are skipped. Returns a summary the
    /// caller can render as a result line.
    /// <para>Caller is responsible for invoking <see cref="PersistAsync"/>
    /// when <see cref="FilterImportResult.HasChanges"/> is true.</para></summary>
    public FilterImportResult ImportFromString(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return new FilterImportResult { ParseError = true };
        var trimmed = encoded.Trim();
        if (!trimmed.StartsWith(ExportPrefix, StringComparison.Ordinal))
            return new FilterImportResult { ParseError = true };

        PlayerFilterCollection? incoming;
        try
        {
            var b64 = trimmed.Substring(ExportPrefix.Length);
            var bytes = Convert.FromBase64String(b64);
            using var ms = new MemoryStream(bytes);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, new UTF8Encoding(false));
            var json = sr.ReadToEnd();
            incoming = JsonSerializer.Deserialize<PlayerFilterCollection>(json);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "PlayerFilterRegistry: import token parse failed");
            return new FilterImportResult { ParseError = true };
        }

        if (incoming?.Filters is null) return new FilterImportResult { ParseError = true };

        var added = 0;
        var overwritten = 0;
        var skipped = 0;
        var overwrittenNames = new List<string>();

        for (var i = 0; i < incoming.Filters.Count; i++)
        {
            var f = incoming.Filters[i];
            // Defensive: a hand-edited or corrupt payload could carry zero-
            // Guid filters. Skip rather than reassign — the user explicitly
            // imported a malformed payload and we'd rather under-import than
            // synthesize an id that doesn't match the export.
            if (f is null || f.Id == Guid.Empty) continue;

            var existingIdx = -1;
            for (var j = 0; j < mCollection.Filters.Count; j++)
            {
                if (mCollection.Filters[j].Id == f.Id) { existingIdx = j; break; }
            }

            if (existingIdx >= 0)
            {
                var oldName = mCollection.Filters[existingIdx].Name;
                overwrittenNames.Add(string.IsNullOrWhiteSpace(oldName) ? "(unnamed)" : oldName);
                mCollection.Filters[existingIdx] = f;
                overwritten++;
            }
            else
            {
                if (mCollection.Filters.Count >= HardCap)
                {
                    skipped++;
                    continue;
                }
                mCollection.Filters.Add(f);
                added++;
            }
        }

        return new FilterImportResult
        {
            Added = added,
            Overwritten = overwritten,
            SkippedAtCap = skipped,
            OverwrittenNames = overwrittenNames,
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await mStore.GetAsync<PlayerFilterCollection>(PlayerFilterCollection.StoreKey)
                .ConfigureAwait(false);
            if (loaded is null) return;

            if (loaded.Filters.Count > HardCap)
            {
                mLog.LogWarning(
                    "PlayerFilterRegistry: loaded {Count} filters, trimming to hard cap {Cap}",
                    loaded.Filters.Count, HardCap);
                loaded.Filters.RemoveRange(HardCap, loaded.Filters.Count - HardCap);
            }

            // Defensive: a hand-edited JSON could carry zero-Guid ids (the
            // default JSON value for Guid). Re-assign so FindById doesn't
            // collide multiple "fresh" filters.
            for (var i = 0; i < loaded.Filters.Count; i++)
                if (loaded.Filters[i].Id == Guid.Empty)
                    loaded.Filters[i].Id = Guid.NewGuid();

            mCollection = loaded;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "PlayerFilterRegistry: settings load failed; using defaults");
        }
    }
}
