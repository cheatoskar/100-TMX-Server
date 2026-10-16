using System.Text.Json;

namespace RandomizerAnywhere;

internal sealed class ImpossibleMaps
{
    private const string SheetCsvUrl = "https://docs.google.com/spreadsheets/d/1fqmzFGPIFBlJuxlwnPJSh1nCTTxqWXtHtvP5OUxE4Ow/export?format=csv&gid=605781157";
    private const int SheetTrackIdColumn = 1;
    private const int SheetFirstDataRow = 4;

    private readonly HttpClient http;
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "impossible-maps.json");

    private readonly HashSet<int> excludedTrackIds = [];
    private readonly object gate = new();

    private readonly RandomizerAnywhere.Config.AppConfig config;

    public ImpossibleMaps(HttpClient http, RandomizerAnywhere.Config.AppConfig config)
    {
        this.http = http;
        this.config = config;
    }

    public bool Contains(int trackId)
    {
        lock (gate)
        {
            return excludedTrackIds.Contains(trackId);
        }
    }

    public int[] AllIds()
    {
        lock (gate)
        {
            return [.. excludedTrackIds];
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var ids = JsonSerializer.Deserialize<int[]>(json) ?? [];

                lock (gate)
                {
                    foreach (var id in ids)
                    {
                        excludedTrackIds.Add(id);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // the sheet refresh below fills the list again; the unreadable file is kept for a look
                var quarantined = AtomicFile.QuarantineCorrupt(filePath);
                Console.WriteLine($"Warning: failed to read impossible-maps.json - {ex.Message}{(quarantined is null ? string.Empty : $" (kept as {Path.GetFileName(quarantined)})")}");
            }
        }

        await RefreshFromSheetAsync(cancellationToken);
    }

    // re-pulls the community-curated sheet so maps added there (after Discord review) start
    // getting excluded without needing a restart
    public async Task RefreshFromSheetAsync(CancellationToken cancellationToken = default)
    {
        // the shared sheet lists TMNF track ids; on another exchange the same numbers are different
        // maps, so only a server drawing from tmnf.exchange may use it
        if (config.EffectiveTmxGame != GameTitle.TMNF)
        {
            Console.WriteLine($"Impossible maps: shared sheet is TMNF-only, skipped for {config.EffectiveTmxGame} (local list only).");
            await SaveAsync(cancellationToken);
            return;
        }

        try
        {
            var csv = await FetchFollowingRedirectsAsync(SheetCsvUrl, cancellationToken);
            var addedFromSheet = 0;

            using var reader = new StringReader(csv);
            for (var rowIndex = 1; reader.ReadLine() is { } line; rowIndex++)
            {
                if (rowIndex < SheetFirstDataRow)
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                if (fields.Length <= SheetTrackIdColumn || !int.TryParse(fields[SheetTrackIdColumn], out var trackId))
                {
                    continue;
                }

                lock (gate)
                {
                    if (excludedTrackIds.Add(trackId))
                    {
                        addedFromSheet++;
                    }
                }
            }

            Console.WriteLine($"Impossible maps: loaded {addedFromSheet} new track id(s) from the sheet.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to fetch impossible-maps sheet, continuing with local list only - {ex.Message}");
        }

        await SaveAsync(cancellationToken);
    }

    public async Task RunPeriodicRefreshAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await Task.Delay(interval, cancellationToken);

            // one failed save used to end this loop for good, and nobody noticed
            try
            {
                await RefreshFromSheetAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: impossible-maps refresh failed, trying again next time - {ex.Message}");
            }
        }
    }

    private async Task<string> FetchFollowingRedirectsAsync(string url, CancellationToken cancellationToken, int maxRedirects = 5)
    {
        for (var i = 0; i < maxRedirects; i++)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
            {
                url = response.Headers.Location?.OriginalString ?? throw new Exception("Redirect response had no Location header.");
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new Exception($"Too many redirects fetching {url}.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        int[] ids;
        lock (gate)
        {
            ids = [.. excludedTrackIds];
        }

        var json = JsonSerializer.Serialize(ids);

        // only a cache of the sheet plus local entries - a failed write must not stop the start
        try
        {
            await AtomicFile.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Warning: couldn't save impossible-maps.json - {ex.Message}");
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
