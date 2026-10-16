using RandomizerAnywhere.Config;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RandomizerAnywhere;

internal sealed class TmxRules
{
    private const int MaxRecencyCheckedAttempts = 20;
    private const int MaxTotalAttempts = 100;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    // An attempt cap alone is not a bound on how long this takes: each attempt is its own HTTP
    // request with its own retry budget, and the whole search runs on the callback dispatch loop,
    // where every second spent here is a second of frozen widgets and dead chat commands. With a
    // struggling TMX, 100 attempts is long enough to take the server out for hours. A wall-clock
    // ceiling makes a finish either produce a map promptly or fail loudly. 30s is roomy for the
    // dozens of fast HEAD requests a normal search takes (recency and impossible-map exclusions
    // each cost one), and leaves the gbx download enough room to still finish inside
    // RemoteClient's handler cutoff, so that cutoff only ever fires on something pathological.
    private static readonly TimeSpan MaxSearchDuration = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly AppConfig config;
    private readonly ImpossibleMaps impossibleMaps;
    private readonly SkippedMaps skippedMaps;
    private readonly MapPool mapPool;
    private readonly PoolCache poolCache;

    // After TMX failed to answer, maps come straight from the local pool copy for a while instead
    // of every map switch first waiting for TMX to time out again.
    private static readonly TimeSpan TmxOutageHold = TimeSpan.FromMinutes(10);
    private DateTimeOffset tmxOutageUntil = DateTimeOffset.MinValue;

    // The full list of maps the current query matches, when it is small enough to load
    // (see MapPool). Refreshed regularly: new uploads join, and with the default preset's
    // "no record yet" filter, maps that get a record drop out.
    private sealed record LoadedPool(string Query, int[] TrackIds, bool Complete, DateTimeOffset LoadedAt);
    private LoadedPool? loadedPool;
    private DateTimeOffset poolRetryAfter = DateTimeOffset.MinValue;

    private const int PoolPageSize = 1000;
    private const int MaxPoolPages = 3;
    private static readonly TimeSpan PoolRefreshInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PoolLoadBudget = TimeSpan.FromSeconds(15);

    private readonly GameTitle game;

    // One instance for the whole process (see Program), touched from the callback stream, the
    // watchdog and background advances - so every access to these two goes through `gate`.
    private readonly object gate = new();
    private readonly Dictionary<int, DateTimeOffset> recentlyServed = [];

    // maps reported via /imp - excluded for the rest of this process's uptime, not written to
    // disk/the shared sheet. That's the human-reviewed permanent list (impossibleMaps above);
    // this is just "don't show it again until someone actually reviews the report"
    private readonly HashSet<int> sessionExcludedTrackIds = [];

    public void ExcludeForSession(int trackId)
    {
        lock (gate)
        {
            sessionExcludedTrackIds.Add(trackId);
        }
    }

    private bool IsSessionExcluded(int trackId)
    {
        lock (gate)
        {
            return sessionExcludedTrackIds.Contains(trackId);
        }
    }

    private void MarkServed(int trackId)
    {
        lock (gate)
        {
            recentlyServed[trackId] = DateTimeOffset.UtcNow;
        }
    }

    private bool WasServedRecently(int trackId)
    {
        lock (gate)
        {
            var cutoff = DateTimeOffset.UtcNow - RecentWindow;
            foreach (var id in recentlyServed.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            {
                recentlyServed.Remove(id);
            }

            return recentlyServed.ContainsKey(trackId);
        }
    }

    // TMF maps are small (a few KB to ~1 MB); anything far bigger is not a map we want to hand
    // to the game server, and must not be buffered whole on a 1 GB machine.
    private const int MaxMapBytes = 8 * 1024 * 1024;

    // Reads a map download and makes sure it IS a map. The random pick never checked the status
    // code, so after the retries a TMX/Cloudflare error page could be saved as a .Gbx and handed
    // to InsertChallenge.
    private static async Task<byte[]> ReadMapBytesAsync(HttpResponseMessage response, int trackId, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxMapBytes)
        {
            throw new InvalidOperationException($"TMX track {trackId} is {response.Content.Headers.ContentLength} bytes - too big to be a map.");
        }

        // read at most one byte past the limit - a response without Content-Length is never
        // buffered whole
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxMapBytes + 1 - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxMapBytes)
            {
                throw new InvalidOperationException($"TMX track {trackId} is more than {MaxMapBytes} bytes - too big to be a map.");
            }
        }

        var bytes = buffer.ToArray();

        if (bytes.Length < 3 || bytes[0] != (byte)'G' || bytes[1] != (byte)'B' || bytes[2] != (byte)'X')
        {
            throw new InvalidOperationException($"TMX sent something that is not a map for track {trackId} ({bytes.Length} bytes, {response.Content.Headers.ContentType?.MediaType ?? "no content type"}).");
        }

        return bytes;
    }

    public TmxRules(HttpClient http, AppConfig config, ImpossibleMaps impossibleMaps, SkippedMaps skippedMaps, MapPool mapPool, PoolCache poolCache)
    {
        this.http = http;
        this.config = config;
        this.impossibleMaps = impossibleMaps;
        this.skippedMaps = skippedMaps;
        this.mapPool = mapPool;
        this.poolCache = poolCache;

        game = config.TmxGame ?? config.Game;
    }

    public string BuildQuery()
    {
        if (config.SourceFromImpossibleList)
        {
            var ids = impossibleMaps.AllIds();
            return ids.Length == 0
                ? "id=0" // no impossible maps tracked yet - deliberately yields "not found" rather than falling back to the full pool
                : $"id={Uri.EscapeDataString(string.Join(',', ids))}";
        }

        var b = new StringBuilder();

        var first = true;

        foreach (var (key, value) in config.TmxQuery)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!first)
            {
                b.Append('&');
            }

            first = false;

            b.Append(Uri.EscapeDataString(key));
            b.Append('=');
            b.Append(Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return b.ToString();
    }

    public string GetRandomTrackUrl() => $"https://{GetSiteUrl()}/trackrandom";
    public string GetTrackGbxUrl(string trackId) => $"https://{GetSiteUrl()}/trackgbx/{trackId}";

    private static readonly string[] DifficultyLabels = ["Beginner", "Intermediate", "Expert", "Lunatic"];

    // 0=Beginner, 1=Intermediate, 2=Expert, 3=Lunatic - verified live against the real API
    // (tmnf.exchange/trackshow/<id> renders these exact labels for these exact values)
    public async Task<(string DifficultyLabel, int Awards)> GetTrackMetaAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=Difficulty,Awards";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTracksResponse>(cancellationToken);
        var result = payload?.Results?.FirstOrDefault();

        if (result is null)
        {
            throw new InvalidOperationException($"TMX has no metadata for track {trackId}.");
        }

        var difficultyLabel = result.Difficulty >= 0 && result.Difficulty < DifficultyLabels.Length
            ? DifficultyLabels[result.Difficulty]
            : $"Difficulty {result.Difficulty}";

        return (difficultyLabel, result.Awards);
    }

    public async Task<string> GetTrackNameAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=TrackName";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTracksResponse>(cancellationToken);
        var name = payload?.Results?.FirstOrDefault()?.TrackName;

        return string.IsNullOrWhiteSpace(name) ? $"Track {trackId}" : name;
    }

    private sealed record TmxApiTracksResponse(List<TmxApiTrackResult>? Results);
    private sealed record TmxApiTrackResult(int Difficulty, int Awards, string? TrackName = null);

    // tag id -> display name, verified live against tmnf.exchange/api/meta/tags (2026-08-16) -
    // this is TMX's community "style" classification (LOL, Tech, RPG, ...), separate from the
    // "Routes"/multilap axis used by the "routes" TmxQuery filter
    private static readonly Dictionary<int, string> TagNames = new()
    {
        [0] = "Race", [1] = "Stunt", [2] = "Maze", [3] = "Offroad", [4] = "Multilap",
        [5] = "FullSpeed", [6] = "LOL", [7] = "Tech", [8] = "SpeedTech", [9] = "RPG",
        [10] = "PressForward", [11] = "Trial", [12] = "Grass", [13] = "Story",
        [14] = "Nascar", [15] = "Speedfun", [16] = "Endurance", [17] = "Altered Nadeo",
        [18] = "Transitional",
    };

    public sealed record TrackDetails(string Name, string Author, string DifficultyLabel, int Awards, int AuthorTimeMs, IReadOnlyList<string> Tags);

    // richer metadata for a single specific track (name, author, difficulty, AT, style tags) -
    // used to show what a player is voting to load, before it's ever downloaded/enqueued.
    // "Uploader.Name" is TMX's nested-field syntax for the author's display name - "Username"/
    // "Author"/etc all 400 with "field does not exist", verified live against the real API
    public async Task<TrackDetails> GetTrackDetailsAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=TrackName,Uploader.Name,Difficulty,Awards,AuthorTime,Tags";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTrackDetailsResponse>(cancellationToken);
        var result = payload?.Results?.FirstOrDefault();

        if (result is null)
        {
            throw new InvalidOperationException($"TMX has no metadata for track {trackId}.");
        }

        var difficultyLabel = result.Difficulty >= 0 && result.Difficulty < DifficultyLabels.Length
            ? DifficultyLabels[result.Difficulty]
            : $"Difficulty {result.Difficulty}";

        var tagNames = (result.Tags ?? []).Select(id => TagNames.GetValueOrDefault(id, $"Tag {id}")).ToList();
        var authorName = result.Uploader?.Name is { Length: > 0 } uploaderName ? uploaderName : "unknown";
        var trackName = result.TrackName is { Length: > 0 } name ? name : $"Track {trackId}";

        return new TrackDetails(trackName, authorName, difficultyLabel, result.Awards, result.AuthorTime, tagNames);
    }

    private sealed record TmxApiTrackDetailsResponse(List<TmxApiTrackDetailsResult>? Results);
    // on the impossible or skip list - such a map is never started on purpose
    public bool IsExcludedFromRotation(int trackId) => impossibleMaps.Contains(trackId) || skippedMaps.Contains(trackId);

    private sealed record TmxApiTrackDetailsResult(string? TrackName, TmxApiUploader? Uploader, int Difficulty, int Awards, int AuthorTime, List<int>? Tags);
    private sealed record TmxApiUploader(string? Name);

    // downloads a SPECIFIC track by id directly, bypassing /trackrandom entirely - unlike
    // NextMapGbxAsync this does NOT check impossibleMaps/sessionExcludedTrackIds, since loading a
    // known id is an explicit, deliberate request (e.g. "let's all try this hard map together"),
    // not a random-pool pick that those exclusions are meant to filter
    public async Task<InMemoryFile> GetMapGbxByIdAsync(int trackId, CancellationToken cancellationToken = default)
    {
        InMemoryFile map;
        try
        {
            map = await DownloadMapAsync(trackId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTmxOutage(ex) && poolCache.TryLoadMap(trackId) is { } copied)
        {
            Console.WriteLine($"TMX can't be reached ({ex.Message}) - map {trackId} comes from the local pool copy.");
            map = copied;
        }

        MarkServed(trackId);
        mapPool.MarkPlayed(trackId);
        return map;
    }

    // Just the download - no "served"/"played" bookkeeping.
    private async Task<InMemoryFile> DownloadMapAsync(int trackId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(GetTrackGbxUrl(trackId.ToString(CultureInfo.InvariantCulture)), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var gbxBytes = await ReadMapBytesAsync(response, trackId, cancellationToken);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName ?? (response.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

        return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
    }

    // The weekly copy of the pool (see PoolCache). Checks every half hour whether it is due; the
    // first check right after the start fills the copy if there is none for the current preset.
    public async Task RunPoolCopyRefreshAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshPoolCopyIfDueAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: updating the local pool copy failed, trying again later - {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), cancellationToken);
        }
    }

    // The copy belongs to one exchange AND one query: TMNF and TMUF ids are different maps.
    private string PoolCopyKey() => $"{GetSiteUrl()}|{config.TmxQueryOverride ?? BuildQuery()}";

    // A copy is meant for a small pool (the default preset: a few hundred maps, a few MB).
    private const int MaxPoolCopyMaps = 1500;
    private const long MaxPoolCopyBytes = 300L * 1024 * 1024;

    private async Task RefreshPoolCopyIfDueAsync(CancellationToken cancellationToken)
    {
        if (config.SourceFromImpossibleList)
        {
            return;
        }

        var query = config.TmxQueryOverride ?? BuildQuery();
        var key = PoolCopyKey();
        var now = DateTimeOffset.UtcNow;
        if (!poolCache.NeedsRefresh(key, now))
        {
            return;
        }

        using var listBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listBudget.CancelAfter(TimeSpan.FromMinutes(2));
        var (ids, complete) = await ListPoolAsync(query, listBudget.Token);

        if (!complete || ids.Length > MaxPoolCopyMaps)
        {
            // too big to copy - remember that, so it isn't listed again every half hour
            await poolCache.SavePoolAsync(new PoolCache.CachedPool(key, now, false, []));
            Console.WriteLine("Map pool copy: this preset matches too many maps to keep a local copy - TMX stays required.");
            return;
        }

        var missing = ids.Where(id => !poolCache.HasMap(id) && !impossibleMaps.Contains(id) && !skippedMaps.Contains(id)).ToList();
        Console.WriteLine($"Map pool copy: {ids.Length} map(s) in the pool, downloading {missing.Count} missing one(s).");

        var failedIds = new List<int>();
        var outagesInARow = 0;
        long downloadedBytes = 0;
        var aborted = false;

        foreach (var id in missing)
        {
            try
            {
                var map = await DownloadMapAsync(id, cancellationToken);
                await poolCache.StoreMapAsync(id, map.Data);
                downloadedBytes += map.Data.Length;
                outagesInARow = 0;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failedIds.Add(id);
                outagesInARow = IsTmxOutage(ex) ? outagesInARow + 1 : 0;

                if (outagesInARow >= 5)
                {
                    // TMX went away mid-way: keep what is there, try the rest again later
                    Console.WriteLine($"Map pool copy: TMX stopped answering ({ex.Message}) - keeping what was copied so far.");
                    aborted = true;
                    break;
                }
            }

            if (downloadedBytes > MaxPoolCopyBytes)
            {
                Console.WriteLine($"Map pool copy: stopped at {downloadedBytes / (1024 * 1024)} MB - this pool is too big to copy.");
                aborted = true;
                break;
            }

            // gently - this is a background job, nobody waits for it
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // Saved even when incomplete: the maps that are there can already stand in for TMX. A
        // partial copy is tried again in a couple of hours (see PoolCache.NeedsRefresh).
        var stillMissing = aborted ? missing.Count(id => !poolCache.HasMap(id)) : failedIds.Count;
        await poolCache.SavePoolAsync(new PoolCache.CachedPool(key, now, true, ids, stillMissing > 0));
        poolCache.Prune(now);
        Console.WriteLine($"Map pool copy updated: {ids.Count(poolCache.HasMap)} of {ids.Length} map(s) on disk"
            + (stillMissing > 0 ? $", {stillMissing} missing (e.g. {string.Join(", ", (failedIds.Count > 0 ? failedIds : missing).Take(5))}) - trying again later." : "."));
    }

    public bool IsInPoolCopy(int trackId) => poolCache.HasMap(trackId);

    // A pool map from the local copy for the server's first map when TMX is down (see ServerSetup).
    public string? PickPoolCopyFileForWarmup()
    {
        if (poolCache.GetPool(PoolCopyKey()) is not { Complete: true } copy)
        {
            return null;
        }

        var candidates = copy.TrackIds.Where(id => poolCache.HasMap(id) && !impossibleMaps.Contains(id) && !skippedMaps.Contains(id)).ToArray();
        return candidates.Length == 0 ? null : poolCache.MapFilePath(candidates[Random.Shared.Next(candidates.Length)]);
    }

    // Whether TMX has a record (world record replay) for this map: true/false, or null when TMX
    // can't be asked right now.
    public async Task<bool?> HasRecordAsync(int trackId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));

            var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId.ToString(CultureInfo.InvariantCulture)}&fields=TrackId,WRReplay.ReplayId";
            using var response = await http.GetAsync(url, budget.Token);
            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(budget.Token), cancellationToken: budget.Token);
            var results = document.RootElement.GetProperty("Results");
            if (results.GetArrayLength() == 0)
            {
                return null;
            }

            return results[0].TryGetProperty("WRReplay", out var record) && record.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Warning: couldn't ask TMX whether map {trackId} has a record - {ex.Message}");
            return null;
        }
    }

    public string GetSiteUrl() => game switch
    {
        GameTitle.TMNF => "tmnf.exchange",
        GameTitle.TMUF => "tmuf.exchange",
        GameTitle.TMN => "nations.tm-exchange.com",
        GameTitle.TMS => "sunrise.tm-exchange.com",
        GameTitle.TMO => "original.tm-exchange.com",
        _ => throw new Exception("Unknown game title"),
    };

    public static SearchValues<char> InvalidFileNameCharSearchValues { get; } = SearchValues.Create([
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ]);

    public async Task<InMemoryFile> NextMapGbxAsync(CancellationToken cancellationToken = default)
    {
        if (DateTimeOffset.UtcNow < tmxOutageUntil && TryPickFromPoolCopy() is { } held)
        {
            return held;
        }

        try
        {
            var map = await NextMapGbxFromTmxAsync(cancellationToken);
            tmxOutageUntil = DateTimeOffset.MinValue;
            return map;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTmxOutage(ex))
        {
            tmxOutageUntil = DateTimeOffset.UtcNow + TmxOutageHold;

            if (TryPickFromPoolCopy() is { } copied)
            {
                Console.WriteLine($"TMX can't be reached ({ex.Message}) - playing maps from the local pool copy for the next {TmxOutageHold.TotalMinutes:0} minutes.");
                return copied;
            }

            throw;
        }
    }

    // No answer from TMX at all: refused, reset, timed out, or an error page instead of data.
    // (A map TMX answered for but refused - 404, not a map - is a bad pick, not an outage.)
    private static bool IsTmxOutage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => IsOutageStatus(status),
        TmxUnavailableException => true,
        // the HTTP pipeline's per-attempt timeout (Polly) - what a TMX that hangs instead of
        // refusing ends in
        Polly.Timeout.TimeoutRejectedException or TaskCanceledException or TimeoutException => true,
        JsonException or IOException => true,
        _ => false,
    };

    // 5xx, rate limiting, a gateway or Cloudflare refusing (403, 408): TMX itself is not answering.
    // A 404 is an answer - that map is gone.
    private static bool IsOutageStatus(System.Net.HttpStatusCode status) =>
        (int)status >= 500 || status is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.RequestTimeout;

    private sealed class TmxUnavailableException(string message) : Exception(message);

    // A map from the local copy of the pool - same rules as a pick from TMX: eligible maps only,
    // every map once per round, never the one just served.
    private InMemoryFile? TryPickFromPoolCopy()
    {
        if (poolCache.GetPool(PoolCopyKey()) is not { Complete: true } copy)
        {
            return null;
        }

        var available = copy.TrackIds.Where(poolCache.HasMap).ToArray();
        var picked = mapPool.Pick(available, IsEligibleForPool, out var state)
            ?? mapPool.Pick(available, id => !impossibleMaps.Contains(id) && !skippedMaps.Contains(id), out state);

        if (picked is not { } trackId || poolCache.TryLoadMap(trackId) is not { } map)
        {
            return null;
        }

        Console.WriteLine($"Next track ID: {trackId} (from the local pool copy, {state})");
        MarkServed(trackId);
        mapPool.MarkPlayed(trackId);
        return map;
    }

    private async Task<InMemoryFile> NextMapGbxFromTmxAsync(CancellationToken cancellationToken)
    {
        // a random pick whose file can't be downloaded (deleted, an error page) is just a bad
        // pick - take another one rather than failing the whole map switch
        for (var attempt = 1; ; attempt++)
        {
            var (trackResponse, trackId) = await NextMapGbxResponseAsync(cancellationToken);
            using (trackResponse)
            {
                try
                {
                    var gbxBytes = await ReadMapBytesAsync(trackResponse, trackId, cancellationToken);
                    var fileName = trackResponse.Content.Headers.ContentDisposition?.FileNameStar
                        ?? trackResponse.Content.Headers.ContentDisposition?.FileName ?? (trackResponse.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

                    // only now: a pick lost to a TMX outage must not use up a map of the round
                    mapPool.MarkPlayed(trackId);
                    return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
                {
                    // no answer at all: TMX is down - the caller takes the map from the local copy
                    if (IsTmxOutage(ex))
                    {
                        throw;
                    }

                    // TMX answered but the map is gone or isn't a map: done with it for this round.
                    // No answer at all (StatusCode null) is TMX being down - the map stays in the bag.
                    if (ex is InvalidOperationException || ex is HttpRequestException { StatusCode: not null })
                    {
                        mapPool.MarkPlayed(trackId);
                    }

                    if (attempt >= 3)
                    {
                        throw;
                    }

                    Console.WriteLine($"Warning: TMX track {trackId} couldn't be downloaded ({ex.Message}) - picking another.");
                }
            }
        }
    }

    // The whole pool for this query, or null when it is too big to list (or TMX can't be asked
    // right now) - the caller then falls back to /trackrandom.
    private async Task<int[]?> GetCompletePoolAsync(string query, CancellationToken cancellationToken)
    {
        if (config.SourceFromImpossibleList)
        {
            return null;
        }

        var current = loadedPool;
        var now = DateTimeOffset.UtcNow;

        if (current is not null && current.Query == query && now - current.LoadedAt < PoolRefreshInterval)
        {
            return current.Complete ? current.TrackIds : null;
        }

        if (now < poolRetryAfter)
        {
            return current is { Complete: true } && current.Query == query ? current.TrackIds : null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PoolLoadBudget);

        try
        {
            var (ids, complete) = await ListPoolAsync(query, budget.Token);

            loadedPool = new LoadedPool(query, ids, complete, DateTimeOffset.UtcNow);

            if (current?.Query != query || current.Complete != complete)
            {
                Console.WriteLine(complete
                    ? $"Map pool: {loadedPool.TrackIds.Length} map(s) match this preset - playing each once before any repeats."
                    : $"Map pool: more than {MaxPoolPages * PoolPageSize} maps match this preset - picking at random from TMX.");
            }

            return complete ? loadedPool.TrackIds : null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // don't ask again on every single pick while TMX struggles
            poolRetryAfter = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            Console.WriteLine($"Warning: couldn't load the map pool from TMX - {ex.Message}");
            return current is { Complete: true } && current.Query == query ? current.TrackIds : null;
        }
    }

    // Every map the query matches (up to MaxPoolPages pages); Complete=false when there are more.
    private async Task<(int[] Ids, bool Complete)> ListPoolAsync(string query, CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        int? after = null;

        for (var page = 0; page < MaxPoolPages; page++)
        {
            var url = $"https://{GetSiteUrl()}/api/tracks?{query}&fields=TrackId&count={PoolPageSize}"
                + (after is { } a ? $"&after={a.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

            using var response = await http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TmxApiTrackIdPage>(cancellationToken)
                ?? throw new InvalidOperationException("TMX returned an empty page.");

            var results = payload.Results ?? [];
            ids.AddRange(results.Select(r => r.TrackId));

            if (!payload.More)
            {
                return ([.. ids.Distinct()], true);
            }

            if (results.Count == 0)
            {
                // "more" but nothing on the page: don't trust this list to be the whole pool
                break;
            }

            after = results[^1].TrackId;
        }

        return ([.. ids.Distinct()], false);
    }

    private sealed record TmxApiTrackIdPage(bool More, List<TmxApiTrackIdResult>? Results);
    private sealed record TmxApiTrackIdResult(int TrackId);

    private bool IsEligibleForPool(int trackId) =>
        !impossibleMaps.Contains(trackId) && !IsSessionExcluded(trackId) && !skippedMaps.Contains(trackId);

    private async Task<(HttpResponseMessage Response, int TrackId)> NextMapGbxResponseAsync(CancellationToken cancellationToken)
    {
        var searchWatch = Stopwatch.StartNew();
        var query = config.TmxQueryOverride ?? BuildQuery();

        if (await GetCompletePoolAsync(query, cancellationToken) is { } pool)
        {
            var pooledId = mapPool.Pick(pool, IsEligibleForPool, out var poolState);

            if (pooledId is null)
            {
                // Every map of the pool is excluded. Maps reported during this run (/imp, /skip
                // exclusions) are only excluded for the session - better to play one of those
                // again than to stop. /trackrandom draws from the same pool, so asking it would
                // only burn 30 seconds before failing.
                pooledId = mapPool.Pick(pool, id => !impossibleMaps.Contains(id) && !skippedMaps.Contains(id), out poolState);
                poolState += ", session exclusions ignored - nothing else left";
            }

            if (pooledId is not { } pickedId)
            {
                throw new InvalidOperationException($"Every one of the {pool.Length} map(s) of this preset is on the impossible or skip list - nothing left to play.");
            }

            Console.WriteLine("Next track ID: " + pickedId);
            Diagnostics.Trace($"tmx: picked {pickedId} from the pool ({poolState}), {searchWatch.ElapsedMilliseconds}ms");
            MarkServed(pickedId);

            var poolDownloadWatch = Stopwatch.StartNew();
            var pooledResponse = await http.GetAsync(GetTrackGbxUrl(pickedId.ToString(CultureInfo.InvariantCulture)), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Diagnostics.Trace($"tmx: downloaded {pickedId} HTTP {(int)pooledResponse.StatusCode} in {poolDownloadWatch.ElapsedMilliseconds}ms");
            return (pooledResponse, pickedId);
        }

        for (var attempt = 1; attempt <= MaxTotalAttempts; attempt++)
        {
            if (searchWatch.Elapsed > MaxSearchDuration)
            {
                throw new TmxUnavailableException(
                    $"Gave up looking for a servable map after {searchWatch.Elapsed.TotalSeconds:0}s ({attempt - 1} attempts) - TMX is too slow or unreachable right now.");
            }

            var tmxRandomUrl = $"{GetRandomTrackUrl()}?{query}";

            using var request = new HttpRequestMessage(HttpMethod.Head, tmxRandomUrl);
            using var response = await http.SendAsync(request, cancellationToken);

            // TMX answers a random pick with a redirect to the track; anything else (an error
            // page, a 5xx during an outage) says nothing about which map, so name the status
            // instead of a bare "failed to get track relative path"
            var trackRelativePath = response.Headers.Location?.OriginalString
                ?? throw (IsOutageStatus(response.StatusCode)
                    ? new TmxUnavailableException($"TMX's random pick answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.")
                    : new InvalidOperationException($"TMX's random pick answered HTTP {(int)response.StatusCode} {response.ReasonPhrase} without a track."));
            var trackIdString = trackRelativePath.Substring(trackRelativePath.LastIndexOf('/') + 1);

            if (!int.TryParse(trackIdString, out var trackId))
            {
                continue;
            }

            if (!config.SourceFromImpossibleList && (impossibleMaps.Contains(trackId) || IsSessionExcluded(trackId)))
            {
                continue;
            }

            // this server's own skip list (maps that can't be played here) - never picked, in any mode
            if (skippedMaps.Contains(trackId))
            {
                // console only - players never see a skipped pick
                Console.WriteLine($"Skip list: random pick {trackId} is on this server's skip list - picking another (never downloaded or sent to the game server).");
                continue;
            }

            if (WasServedRecently(trackId) && attempt <= MaxRecencyCheckedAttempts)
            {
                continue;
            }

            Console.WriteLine("Next track ID: " + trackId);
            Diagnostics.Trace($"tmx: picked {trackId} after {attempt} attempt(s), {searchWatch.ElapsedMilliseconds}ms");
            MarkServed(trackId);

            var downloadWatch = Stopwatch.StartNew();
            var gbxResponse = await http.GetAsync(GetTrackGbxUrl(trackIdString), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Diagnostics.Trace($"tmx: downloaded {trackId} HTTP {(int)gbxResponse.StatusCode} in {downloadWatch.ElapsedMilliseconds}ms");
            return (gbxResponse, trackId);
        }

        throw new InvalidOperationException($"Could not find a servable map after {MaxTotalAttempts} attempts (all candidates were excluded).");
    }

    private static string GetValidFileName(string fileName)
    {
        var buffer = ArrayPool<char>.Shared.Rent(fileName.Length);
        var bufferIndex = 0;

        foreach (var c in fileName)
        {
            // non-ASCII stays out too, not just the Windows-invalid characters: HttpClient parses
            // the Content-Disposition "filename" header as Latin-1 per the historic HTTP header
            // spec, so a map name with stylized Unicode (common on TMX) comes through as mojibake.
            // That mojibake string then gets written to disk AND re-encoded as UTF-8 into the match
            // settings XML separately, and the two don't round-trip to the same bytes - the
            // dedicated server ends up looking for a filename slightly different from the one
            // actually on disk and fails to boot with "Track unknown"
            buffer[bufferIndex++] = InvalidFileNameCharSearchValues.Contains(c) || c > 127 ? '_' : c;
        }

        var result = new string(buffer, 0, bufferIndex);
        ArrayPool<char>.Shared.Return(buffer);

        return result;
    }
}
