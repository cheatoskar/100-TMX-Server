using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RandomizerAnywhere;

// A copy of the preset's map pool on disk: the list of track ids and the map files themselves.
//
// When tm-exchange.com is down the controller can't fetch maps at all - not even with a list of
// ids, because the files come from TMX too. So once a week (on Sunday, or right away when there is
// no copy for the current preset yet) the whole pool is listed and every map file that is missing
// locally is downloaded; maps that dropped out of the pool (got a record, were deleted) are
// removed. While TMX can't be reached, maps are drawn from this copy instead.
//
// Only pools small enough to list completely are copied (the default preset: a few hundred maps,
// a few MB). Presets with tens of thousands of maps have no copy and still need TMX.
internal sealed class PoolCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan KeepUnusedListsFor = TimeSpan.FromDays(30);

    private readonly string directory;
    private readonly string mapsDirectory;
    private readonly object gate = new();
    private readonly Dictionary<string, CachedPool> pools = [];

    public sealed record CachedPool(string Query, DateTimeOffset UpdatedAt, bool Complete, int[] TrackIds);

    public PoolCache()
        : this(Path.Combine(AppContext.BaseDirectory, "MapCache"))
    {
    }

    public PoolCache(string directory)
    {
        this.directory = directory;
        mapsDirectory = Path.Combine(directory, "maps");
        Load();
    }

    public CachedPool? GetPool(string query)
    {
        lock (gate)
        {
            return pools.GetValueOrDefault(query);
        }
    }

    // Due on Sunday (once that day), when the copy is older than a week, or when there is none.
    public bool NeedsRefresh(string query, DateTimeOffset now)
    {
        var pool = GetPool(query);

        if (pool is null || now - pool.UpdatedAt >= MaxAge)
        {
            return true;
        }

        return now.UtcDateTime.DayOfWeek == DayOfWeek.Sunday && pool.UpdatedAt.UtcDateTime.Date != now.UtcDateTime.Date;
    }

    public bool HasMap(int trackId) => File.Exists(MapPath(trackId));

    public InMemoryFile? TryLoadMap(int trackId)
    {
        try
        {
            var path = MapPath(trackId);
            if (!File.Exists(path))
            {
                return null;
            }

            var data = File.ReadAllBytes(path);
            if (data.Length < 3 || data[0] != (byte)'G' || data[1] != (byte)'B' || data[2] != (byte)'X')
            {
                return null;
            }

            return new InMemoryFile($"tmx{trackId}.Challenge.Gbx", data, trackId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Warning: couldn't read map {trackId} from the local pool copy - {ex.Message}");
            return null;
        }
    }

    // The file a warmup can start from (the "_tmx<id>_" marker identifies it), or null.
    public string? MapFilePath(int trackId) => HasMap(trackId) ? MapPath(trackId) : null;

    public async Task StoreMapAsync(int trackId, byte[] data)
    {
        Directory.CreateDirectory(mapsDirectory);
        await AtomicFile.WriteAllBytesAsync(MapPath(trackId), data);
    }

    public async Task SavePoolAsync(CachedPool pool)
    {
        lock (gate)
        {
            pools[pool.Query] = pool;
        }

        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAllTextAsync(PoolFilePath(pool.Query), JsonSerializer.Serialize(pool));
    }

    // Removes map files no list needs any more, and lists nobody refreshed for a month (a preset
    // that was tried once).
    public void Prune(DateTimeOffset now)
    {
        try
        {
            HashSet<int> needed;
            lock (gate)
            {
                foreach (var stale in pools.Values.Where(p => now - p.UpdatedAt > KeepUnusedListsFor).ToList())
                {
                    pools.Remove(stale.Query);
                    File.Delete(PoolFilePath(stale.Query));
                }

                needed = [.. pools.Values.SelectMany(p => p.TrackIds)];
            }

            if (!Directory.Exists(mapsDirectory))
            {
                return;
            }

            var removed = 0;
            foreach (var file in new DirectoryInfo(mapsDirectory).EnumerateFiles("_tmx*_.Challenge.Gbx"))
            {
                if (TryParseTrackId(file.Name) is { } id && !needed.Contains(id))
                {
                    file.Delete();
                    removed++;
                }
            }

            if (removed > 0)
            {
                Console.WriteLine($"Map pool copy: removed {removed} map(s) that left the pool.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Warning: couldn't tidy up the local pool copy - {ex.Message}");
        }
    }

    private string MapPath(int trackId) => Path.Combine(mapsDirectory, $"_tmx{trackId}_.Challenge.Gbx");

    private string PoolFilePath(string query)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..16].ToLowerInvariant();
        return Path.Combine(directory, $"pool-{hash}.json");
    }

    private static int? TryParseTrackId(string fileName)
    {
        // "_tmx<id>_.Challenge.Gbx"
        var end = fileName.IndexOf('_', 4);
        return end > 4 && int.TryParse(fileName.AsSpan(4, end - 4), out var id) ? id : null;
    }

    private void Load()
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "pool-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CachedPool>(File.ReadAllText(file)) is { Query: not null, TrackIds: not null } pool)
                {
                    pools[pool.Query] = pool;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // only a copy - the next refresh writes it again
                AtomicFile.QuarantineCorrupt(file);
                Console.WriteLine($"Warning: couldn't read {Path.GetFileName(file)} - {ex.Message}");
            }
        }
    }
}
