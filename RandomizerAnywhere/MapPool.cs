using System.Text.Json;

namespace RandomizerAnywhere;

// Picks maps without repeats when the whole pool is small enough to know.
//
// The default preset only takes maps nobody has a record on yet - a few hundred maps, minus the
// ones on the impossible list. TMX's /trackrandom draws from that with replacement and not even
// evenly, and the recent-maps list only covered one hour and was lost on every restart: 213
// picks in two days gave only 75 different maps, some of them 10 times.
//
// So for such a pool every map is played once before any map comes again (a shuffle bag), and
// what was played survives restarts in played-maps.json. Pools too big to list (most presets have
// tens of thousands of maps) keep using /trackrandom, where repeats are rare anyway.
internal sealed class MapPool
{
    private const int RecentCount = 20;

    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "played-maps.json");
    private readonly object gate = new();

    // maps played since the bag was last emptied
    private readonly HashSet<int> playedThisCycle = [];

    // the last few maps in order, so a new cycle doesn't open with a map that just ran
    private readonly List<int> recent = [];

    public MapPool()
    {
        Load();
    }

    // Returns null when nothing in the pool is eligible.
    public int? Pick(IReadOnlyCollection<int> pool, Func<int, bool> isEligible, out string description)
    {
        lock (gate)
        {
            var eligible = pool.Where(isEligible).ToList();

            if (eligible.Count == 0)
            {
                description = "no eligible map in the pool";
                return null;
            }

            var candidates = eligible.Where(id => !playedThisCycle.Contains(id)).ToList();

            if (candidates.Count == 0)
            {
                // everything has been played once - start the next round
                playedThisCycle.Clear();
                candidates = eligible.Where(id => !recent.Contains(id)).ToList();

                if (candidates.Count == 0)
                {
                    candidates = eligible;
                }
            }

            var pick = candidates[Random.Shared.Next(candidates.Count)];
            description = $"{candidates.Count - 1} of {eligible.Count} eligible map(s) still unplayed this round";
            return pick;
        }
    }

    public void MarkPlayed(int trackId)
    {
        string json;

        lock (gate)
        {
            // with a pool too big to list, Pick never runs and never starts a new round - keep
            // the file from growing forever
            if (playedThisCycle.Count >= 5000)
            {
                playedThisCycle.Clear();
            }

            playedThisCycle.Add(trackId);
            recent.Remove(trackId);
            recent.Insert(0, trackId);

            if (recent.Count > RecentCount)
            {
                recent.RemoveRange(RecentCount, recent.Count - RecentCount);
            }

            json = JsonSerializer.Serialize(new PlayedMapsFile([.. playedThisCycle], [.. recent]));
        }

        try
        {
            // small file, written on every map - no reason to make the caller wait for the disk
            _ = AtomicFile.WriteAllTextAsync(filePath, json).ContinueWith(
                t => Console.WriteLine($"Warning: couldn't save played-maps.json - {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: couldn't save played-maps.json - {ex.Message}");
        }
    }

    private void Load()
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<PlayedMapsFile>(File.ReadAllText(filePath));

            lock (gate)
            {
                playedThisCycle.UnionWith(file?.PlayedThisCycle ?? []);
                recent.AddRange((file?.Recent ?? []).Take(RecentCount));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // only a nicety - losing it means some repeats, so start over instead of failing
            AtomicFile.QuarantineCorrupt(filePath);
            Console.WriteLine($"Warning: failed to read played-maps.json, starting a fresh round - {ex.Message}");
        }
    }

    private sealed record PlayedMapsFile(int[] PlayedThisCycle, int[] Recent);
}
