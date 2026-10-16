using RandomizerAnywhere.Config;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http.Json;
using System.Text;

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

    public TmxRules(HttpClient http, AppConfig config, ImpossibleMaps impossibleMaps, SkippedMaps skippedMaps)
    {
        this.http = http;
        this.config = config;
        this.impossibleMaps = impossibleMaps;
        this.skippedMaps = skippedMaps;

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
        using var response = await http.GetAsync(GetTrackGbxUrl(trackId.ToString(CultureInfo.InvariantCulture)), cancellationToken);

        var gbxBytes = await ReadMapBytesAsync(response, trackId, cancellationToken);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName ?? (response.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

        MarkServed(trackId);
        return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
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

                    return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
                }
                catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"Warning: TMX track {trackId} couldn't be downloaded ({ex.Message}) - picking another.");
                }
            }
        }
    }

    private async Task<(HttpResponseMessage Response, int TrackId)> NextMapGbxResponseAsync(CancellationToken cancellationToken)
    {
        var searchWatch = Stopwatch.StartNew();

        for (var attempt = 1; attempt <= MaxTotalAttempts; attempt++)
        {
            if (searchWatch.Elapsed > MaxSearchDuration)
            {
                throw new InvalidOperationException(
                    $"Gave up looking for a servable map after {searchWatch.Elapsed.TotalSeconds:0}s ({attempt - 1} attempts) - TMX is too slow or unreachable right now.");
            }

            var tmxRandomUrl = $"{GetRandomTrackUrl()}?{config.TmxQueryOverride ?? BuildQuery()}";

            using var request = new HttpRequestMessage(HttpMethod.Head, tmxRandomUrl);
            using var response = await http.SendAsync(request, cancellationToken);

            // TMX answers a random pick with a redirect to the track; anything else (an error
            // page, a 5xx during an outage) says nothing about which map, so name the status
            // instead of a bare "failed to get track relative path"
            var trackRelativePath = response.Headers.Location?.OriginalString
                ?? throw new InvalidOperationException($"TMX's random pick answered HTTP {(int)response.StatusCode} {response.ReasonPhrase} without a track.");
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
            var gbxResponse = await http.GetAsync(GetTrackGbxUrl(trackIdString), cancellationToken);
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
