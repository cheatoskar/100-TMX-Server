using System.Text.Json;

namespace RandomizerAnywhere;

// This server's own "never play these" list, for maps that can't be played here - a map built with
// blocks the game doesn't have, say, which fails to load for every player. Deliberately separate
// from ImpossibleMaps: that one is the shared, community-reviewed list pulled from the sheet and
// reported to Discord. This one is local to the server, is never sent anywhere, and is not
// overwritten by the sheet refresh. Persisted to skipped-maps.json so it survives restarts, and
// plain JSON so it can be edited by hand (a flat array of TMX track ids).
internal sealed class SkippedMaps
{
    // Maps known to be unplayable here, shipped with the build so a fresh deploy already has them -
    // the live server's list file is created from this on first start. Add an id here when a map
    // is confirmed broken; the runtime file (and /blockmap) can add more without a rebuild.
    //   8468597  "Lunatic Sextreme 002" - built with blocks the game does not have, fails to load
    private static readonly int[] BuiltInSkippedMapIds = [8468597];

    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "skipped-maps.json");
    private readonly HashSet<int> trackIds = [];
    private readonly object gate = new();
    private readonly RandomizerAnywhere.Config.AppConfig config;

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
        try
        {
            if (File.Exists(filePath))
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
            // Kept, not overwritten: the write below would otherwise replace the admins' list
            // with just the built-in ids, and the maps it blocked ("drop every player") return.
            var quarantined = AtomicFile.QuarantineCorrupt(filePath);
            Console.WriteLine($"ERROR: failed to read skipped-maps.json - {ex.Message}. "
                + (quarantined is null ? "Starting with the built-in list only." : $"The file was kept as {Path.GetFileName(quarantined)}; fix it and put it back."));
        }

        // merge in the built-in ids; if any were missing, write the file so it shows the full list
        var changed = false;
        int[] snapshot;

        lock (gate)
        {
            // track ids belong to ONE exchange - 8468597 is a TMNF map and means something else on
            // tmuf.exchange - so the built-in ids only apply to a server drawing from tmnf.exchange
            if (config.EffectiveTmxGame == GameTitle.TMNF)
            {
                foreach (var id in BuiltInSkippedMapIds)
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

        Console.WriteLine($"Skipped maps: {snapshot.Length} map(s) on this server's skip list.");
    }

    // Returns false if the map was already on the list.
    public async Task<bool> AddAsync(int trackId, CancellationToken cancellationToken = default)
    {
        int[] snapshot;

        lock (gate)
        {
            if (!trackIds.Add(trackId))
            {
                return false;
            }

            snapshot = [.. trackIds.Order()];
        }

        await AtomicFile.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot));
        return true;
    }
}
