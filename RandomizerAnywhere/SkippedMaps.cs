using System.Text.Json;

namespace RandomizerAnywhere;

// This server's own "never play these" list, for maps that can't be played here - a map built with
// blocks the game doesn't have, say, which fails to load for every player. Deliberately separate
// from ImpossibleMaps: that one is the shared, community-reviewed list pulled from the sheet and
// reported to Discord. This one is local to the server, is never sent anywhere, and is not
// overwritten by the sheet refresh. Persisted to skipped-maps.json so it survives restarts, and
// plain JSON so it can be edited by hand (a flat array of TMX track ids). A hand edit takes effect
// while the server runs: RunReloadLoopAsync picks it up within seconds, no restart needed.
internal sealed class SkippedMaps
{
    // What a brand-new TMNF server's list starts with, written into skipped-maps.json only when the
    // file does not exist yet. After that they are ordinary entries: /unblockmap and the website's
    // admin panel can take them off like any other. (Until 2026-11-22 they were merged back in on
    // every load and reload, so they could never be removed and the file did not tell the truth.)
    //   8468597  "Lunatic Sextreme 002" - built with blocks the game does not have, fails to load
    private static readonly int[] InitialSkippedMapIds = [8468597];

    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "skipped-maps.json");
    private readonly HashSet<int> trackIds = [];
    private readonly object gate = new();
    private readonly RandomizerAnywhere.Config.AppConfig config;

    // One reader/writer of the file at a time: /blockmap writing while the reload loop reads would
    // otherwise write back a list that lacks the hand edit the loop was about to pick up.
    private readonly SemaphoreSlim fileLock = new(1, 1);

    // The file's write time as this process last read or wrote it. The reload loop only reads the
    // file when it differs, so the server's own writes don't count as a hand edit.
    private DateTime? knownWriteTimeUtc;

    // The ids a hand edit of the file added, so the game can move off the map it is playing right
    // now if that is one of them - the usual reason to edit the file is a map that crashes everyone.
    public event Action<IReadOnlyCollection<int>>? MapsAdded;

    public SkippedMaps(RandomizerAnywhere.Config.AppConfig config)
    {
        this.config = config;
    }

    public bool Contains(int trackId)
    {
        lock (gate)
        {
            return trackIds.Contains(trackId);
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return trackIds.Count;
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var fresh = !File.Exists(filePath);

        try
        {
            if (!fresh)
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var ids = JsonSerializer.Deserialize<int[]>(json) ?? [];

                lock (gate)
                {
                    foreach (var id in ids)
                    {
                        trackIds.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Kept, not overwritten: nothing below writes unless the file was missing, so the
            // admins' list ("drop every player" maps among it) is not replaced by an empty one.
            var quarantined = AtomicFile.QuarantineCorrupt(filePath);
            Console.WriteLine($"ERROR: failed to read skipped-maps.json - {ex.Message}. "
                + (quarantined is null ? "Starting with an empty list." : $"The file was kept as {Path.GetFileName(quarantined)}; fix it and put it back."));
        }

        // A server without a list yet starts with the known-broken maps. Track ids belong to ONE
        // exchange - 8468597 is a TMNF map and means something else on tmuf.exchange - so only a
        // server drawing from tmnf.exchange gets them.
        var changed = false;
        int[] snapshot;

        lock (gate)
        {
            if (fresh && config.EffectiveTmxGame == GameTitle.TMNF)
            {
                foreach (var id in InitialSkippedMapIds)
                {
                    changed |= trackIds.Add(id);
                }
            }

            snapshot = [.. trackIds.Order()];
        }

        if (changed)
        {
            try
            {
                await AtomicFile.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: couldn't write skipped-maps.json - {ex.Message}");
            }
        }

        knownWriteTimeUtc = CurrentWriteTimeUtc();
        Console.WriteLine($"Skipped maps: {snapshot.Length} map(s) on this server's skip list.");
    }

    public async Task RunReloadLoopAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await Task.Delay(interval, cancellationToken);

            try
            {
                await ReloadIfChangedAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: skip list reload failed, trying again next time - {ex.Message}");
            }
        }
    }

    // Returns false if the map was already on the list.
    public async Task<bool> AddAsync(int trackId, CancellationToken cancellationToken = default)
    {
        int[] added;
        bool isNew;

        await fileLock.WaitAsync(cancellationToken);

        try
        {
            // an edit nobody has picked up yet would be lost under the write below
            added = await ReadIfChangedAsync(cancellationToken);

            int[] snapshot;

            lock (gate)
            {
                isNew = trackIds.Add(trackId);
                snapshot = [.. trackIds.Order()];
            }

            if (isNew)
            {
                await AtomicFile.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot));
                knownWriteTimeUtc = CurrentWriteTimeUtc();
            }
        }
        finally
        {
            fileLock.Release();
        }

        RaiseMapsAdded(added);
        return isNew;
    }

    // /unblockmap. Returns false if the map was not on the list.
    public async Task<bool> RemoveAsync(int trackId, CancellationToken cancellationToken = default)
    {
        int[] added;
        bool removed;

        await fileLock.WaitAsync(cancellationToken);

        try
        {
            // an edit nobody has picked up yet would be lost under the write below
            added = await ReadIfChangedAsync(cancellationToken);

            int[] snapshot;

            lock (gate)
            {
                removed = trackIds.Remove(trackId);
                snapshot = [.. trackIds.Order()];
            }

            if (removed)
            {
                await AtomicFile.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot));
                knownWriteTimeUtc = CurrentWriteTimeUtc();
            }
        }
        finally
        {
            fileLock.Release();
        }

        RaiseMapsAdded(added);
        return removed;
    }

    private async Task ReloadIfChangedAsync(CancellationToken cancellationToken)
    {
        int[] added;

        await fileLock.WaitAsync(cancellationToken);

        try
        {
            added = await ReadIfChangedAsync(cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }

        RaiseMapsAdded(added);
    }

    // Replaces the in-memory list with the file's when the file changed since it was last read or
    // written here, and returns the ids that are new. Called under fileLock.
    private async Task<int[]> ReadIfChangedAsync(CancellationToken cancellationToken)
    {
        var writeTime = CurrentWriteTimeUtc();

        // a deleted file is not an empty list - keep what is in memory, the next write recreates it
        if (writeTime is null || writeTime == knownWriteTimeUtc)
        {
            return [];
        }

        int[] ids;

        try
        {
            ids = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(filePath, cancellationToken)) ?? [];
        }
        catch (JsonException ex)
        {
            // a typo or a half-saved edit: keep the current list and look again on the next change.
            // Not quarantined like at startup - someone is editing the file right now.
            knownWriteTimeUtc = writeTime;
            Console.WriteLine($"Warning: skipped-maps.json doesn't parse ({ex.Message}) - keeping the current skip list until the file is fixed.");
            return [];
        }

        int[] added;
        int removed;
        int count;

        lock (gate)
        {
            var fresh = new HashSet<int>(ids);

            added = [.. fresh.Except(trackIds).Order()];
            removed = trackIds.Count(id => !fresh.Contains(id));
            trackIds.Clear();
            trackIds.UnionWith(fresh);
            count = trackIds.Count;
        }

        knownWriteTimeUtc = writeTime;

        if (added.Length > 0 || removed > 0)
        {
            Console.WriteLine($"Skip list reloaded from skipped-maps.json: {count} map(s), {added.Length} added{(added.Length > 0 ? $" ({string.Join(", ", added)})" : string.Empty)}, {removed} removed.");
        }

        return added;
    }

    private void RaiseMapsAdded(int[] added)
    {
        if (added.Length == 0)
        {
            return;
        }

        try
        {
            MapsAdded?.Invoke(added);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: handling the skip list's new maps failed - {ex.Message}");
        }
    }

    private DateTime? CurrentWriteTimeUtc() => File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : null;
}
