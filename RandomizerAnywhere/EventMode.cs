using System.Text.Json;
using System.Text.Json.Nodes;
using RandomizerAnywhere.Config;

namespace RandomizerAnywhere;

internal sealed record EventMap(int TrackId, bool Unlimiter);

internal sealed record EventFinish(string Login, string Nickname, int Time, DateTimeOffset At, string Server);

internal sealed record EventServerInfo(string Label, string Login, int? TrackId, string? MapName, int Players, int MaxPlayers, DateTimeOffset Updated);

// The event module: a hand-made list of maps to get through (event-maps.json), and the progress on
// it (event-state.json) - which map was finished by whom, with what time, when and on which server,
// plus which map every server is playing right now. Off unless EventEnabled is set; with it off,
// nothing here reads or writes a file and the server behaves exactly as before.
//
// Several servers on one machine can run one event together: point their EventMapsFile and
// EventStateFile at the same files. The state file is then shared like a shared leaderboard -
// every write happens under a cross-process lock on a freshly read copy, so one server's write never
// drops another's finish. The maps file is only ever edited by hand and re-read within seconds.
internal sealed class EventMode
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    // a server that hasn't written its heartbeat for this long is treated as gone
    public static readonly TimeSpan ServerStaleAfter = TimeSpan.FromSeconds(60);

    private readonly AppConfig config;
    private readonly object gate = new();

    private string title = string.Empty;
    private List<EventMap> maps = [];
    private Dictionary<int, EventFinish> finished = [];
    private Dictionary<string, EventServerInfo> servers = new(StringComparer.OrdinalIgnoreCase);

    private DateTime? mapsWriteTimeUtc;
    private DateTime? stateWriteTimeUtc;

    // Raised (from the reload loop or after this server's own write) whenever the list or the
    // progress changed, so the widgets can be refreshed.
    public event Action? Changed;

    public EventMode(AppConfig config)
    {
        this.config = config;
    }

    public bool Enabled => config.EventEnabled;

    public string MapsFile => string.IsNullOrWhiteSpace(config.EventMapsFile)
        ? Path.Combine(AppContext.BaseDirectory, "event-maps.json")
        : Path.GetFullPath(config.EventMapsFile);

    public string StateFile => string.IsNullOrWhiteSpace(config.EventStateFile)
        ? Path.Combine(Path.GetDirectoryName(MapsFile) ?? AppContext.BaseDirectory, "event-state.json")
        : Path.GetFullPath(config.EventStateFile);

    private string LockPath => StateFile + ".lock";

    public string ServerLabel => string.IsNullOrWhiteSpace(config.EventServerLabel) ? config.ServerName : config.EventServerLabel;

    public string Title
    {
        get
        {
            lock (gate)
            {
                return string.IsNullOrWhiteSpace(title) ? "Event" : title;
            }
        }
    }

    public IReadOnlyList<EventMap> AllMaps
    {
        get
        {
            lock (gate)
            {
                return [.. maps];
            }
        }
    }

    // The maps nobody has finished yet, in list order - all of them, Unlimiter or not.
    public IReadOnlyList<EventMap> OpenMaps
    {
        get
        {
            lock (gate)
            {
                return [.. maps.Where(m => !finished.ContainsKey(m.TrackId))];
            }
        }
    }

    // The open maps this server is meant to play: an Unlimiter server only the Unlimiter maps,
    // every other server only the normal ones (a client without the mod can't load the others).
    public IReadOnlyList<EventMap> OpenMapsForThisServer
    {
        get
        {
            lock (gate)
            {
                return [.. maps.Where(m => !finished.ContainsKey(m.TrackId) && m.Unlimiter == config.EventUnlimiter)];
            }
        }
    }

    // What this server should draw its next map from: its open maps minus the ones another server
    // is playing right now, so two servers never run the same map at once. When only maps that
    // run elsewhere are left, those come back rather than nothing.
    public IReadOnlyList<int> PickableIds()
    {
        var open = OpenMapsForThisServer.Select(m => m.TrackId).ToList();
        var elsewhere = MapsRunningElsewhere();
        var free = open.Where(id => !elsewhere.Contains(id)).ToList();
        return free.Count > 0 ? free : open;
    }

    public bool IsListed(int trackId)
    {
        lock (gate)
        {
            return maps.Any(m => m.TrackId == trackId);
        }
    }

    public EventFinish? GetFinish(int trackId)
    {
        lock (gate)
        {
            return finished.GetValueOrDefault(trackId);
        }
    }

    public int FinishedCount
    {
        get
        {
            lock (gate)
            {
                return maps.Count(m => finished.ContainsKey(m.TrackId));
            }
        }
    }

    // the most recent finishes on listed maps, newest first
    public IReadOnlyList<(int TrackId, EventFinish Finish)> RecentFinishes(int count)
    {
        lock (gate)
        {
            var listed = maps.Select(m => m.TrackId).ToHashSet();
            return [.. finished.Where(kv => listed.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value.At)
                .Take(count)
                .Select(kv => (kv.Key, kv.Value))];
        }
    }

    // the other servers of this event that are alive, by label
    public IReadOnlyList<EventServerInfo> OtherServers()
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            return [.. servers.Values
                .Where(s => !string.Equals(s.Label, ServerLabel, StringComparison.OrdinalIgnoreCase) && now - s.Updated < ServerStaleAfter)
                .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public HashSet<int> MapsRunningElsewhere() =>
        [.. OtherServers().Where(s => s.TrackId is not null).Select(s => s.TrackId!.Value)];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        ReloadMapsIfChanged(force: true);
        ReloadStateIfChanged(force: true);

        lock (gate)
        {
            Console.WriteLine($"Event: '{Title}' - {maps.Count} map(s) in {MapsFile}, {maps.Count(m => finished.ContainsKey(m.TrackId))} finished (progress in {StateFile}), this server is '{ServerLabel}'{(config.EventUnlimiter ? " (Unlimiter maps)" : string.Empty)}.");
        }

        await Task.CompletedTask;
    }

    public async Task RunReloadLoopAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        while (true)
        {
            await Task.Delay(interval, cancellationToken);

            try
            {
                var changed = ReloadMapsIfChanged(force: false);
                changed |= ReloadStateIfChanged(force: false);

                if (changed)
                {
                    RaiseChanged();
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: event file reload failed, trying again next time - {ex.Message}");
            }
        }
    }

    // Returns true when this was the map's first finish (and it was recorded).
    public async Task<bool> RecordFinishAsync(int trackId, string login, string nickname, int time)
    {
        if (!Enabled || !IsListed(trackId))
        {
            return false;
        }

        var isFirst = false;

        await UpdateStateAsync(() =>
        {
            if (!finished.ContainsKey(trackId))
            {
                finished[trackId] = new EventFinish(login, nickname, time, DateTimeOffset.UtcNow, ServerLabel);
                isFirst = true;
            }
        });

        if (isFirst)
        {
            RaiseChanged();
        }

        return isFirst;
    }

    // this server's heartbeat: what it plays and how full it is
    public async Task PublishServerAsync(string login, int? trackId, string? mapName, int players, int maxPlayers)
    {
        if (!Enabled)
        {
            return;
        }

        var info = new EventServerInfo(ServerLabel, login, trackId, mapName, players, maxPlayers, DateTimeOffset.UtcNow);
        await UpdateStateAsync(() => servers[ServerLabel] = info);
    }

    // ---- files ----

    private bool ReloadMapsIfChanged(bool force)
    {
        var path = MapsFile;
        DateTime? writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        if (!force && writeTime == mapsWriteTimeUtc)
        {
            return false;
        }

        mapsWriteTimeUtc = writeTime;

        if (writeTime is null)
        {
            if (force)
            {
                Console.WriteLine($"Event: no map list yet - put the TMX ids into {path} (a JSON array, or {{\"title\": \"...\", \"maps\": [...]}}). It is picked up while the server runs.");
            }

            return false;
        }

        try
        {
            var (newTitle, newMaps) = ParseMapsFile(File.ReadAllText(path));

            lock (gate)
            {
                var before = maps.Count;
                title = newTitle;
                maps = newMaps;
                if (!force)
                {
                    Console.WriteLine($"Event: map list reloaded - {maps.Count} map(s) (was {before}).");
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            // someone is editing it right now - keep the current list until it parses again
            Console.WriteLine($"Warning: {Path.GetFileName(path)} doesn't parse ({ex.Message}) - keeping the current event list until the file is fixed.");
            return false;
        }
    }

    // Accepts a plain array of ids, or {"title": "...", "maps": [...]}; each map is an id or
    // {"id": 123, "unlimiter": true}. Duplicates are dropped, the order is kept.
    internal static (string Title, List<EventMap> Maps) ParseMapsFile(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var parsedTitle = string.Empty;
        JsonArray? list;

        if (root is JsonArray array)
        {
            list = array;
        }
        else if (root is JsonObject obj)
        {
            parsedTitle = obj["title"]?.GetValue<string>() ?? string.Empty;
            list = obj["maps"] as JsonArray;
        }
        else
        {
            throw new FormatException("expected an array of TMX ids or an object with \"maps\"");
        }

        var result = new List<EventMap>();
        var seen = new HashSet<int>();

        foreach (var item in list ?? [])
        {
            EventMap? map = item switch
            {
                JsonValue value when value.TryGetValue<int>(out var id) => new EventMap(id, false),
                JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text, out var id) => new EventMap(id, false),
                JsonObject entry when entry["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id)
                    => new EventMap(id, entry["unlimiter"] is JsonValue u && u.TryGetValue<bool>(out var unlimiter) && unlimiter),
                _ => null,
            };

            if (map is not null && map.TrackId > 0 && seen.Add(map.TrackId))
            {
                result.Add(map);
            }
        }

        return (parsedTitle, result);
    }

    private sealed class StateFileModel
    {
        public Dictionary<string, EventFinish> Finished { get; set; } = [];
        public Dictionary<string, EventServerInfo> Servers { get; set; } = [];
    }

    private bool ReloadStateIfChanged(bool force)
    {
        var path = StateFile;
        DateTime? writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        if (writeTime is null || (!force && writeTime == stateWriteTimeUtc))
        {
            return false;
        }

        try
        {
            var model = JsonSerializer.Deserialize<StateFileModel>(File.ReadAllText(path)) ?? new StateFileModel();

            lock (gate)
            {
                finished = model.Finished
                    .Where(kv => int.TryParse(kv.Key, out _))
                    .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
                servers = new Dictionary<string, EventServerInfo>(model.Servers, StringComparer.OrdinalIgnoreCase);
            }

            stateWriteTimeUtc = writeTime;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // another server is mid-write (writes are atomic renames, so this is rare) - next time
            Console.WriteLine($"Warning: couldn't read {Path.GetFileName(path)} - {ex.Message}");
            return false;
        }
    }

    // read-modify-write of the shared state file under the cross-process lock
    private async Task UpdateStateAsync(Action modify)
    {
        using var fileLock = await AcquireFileLockAsync();

        ReloadStateIfChanged(force: false);

        StateFileModel model;
        lock (gate)
        {
            modify();
            model = new StateFileModel
            {
                Finished = finished.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                Servers = new Dictionary<string, EventServerInfo>(servers),
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        await AtomicFile.WriteAllTextAsync(StateFile, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            stateWriteTimeUtc = File.GetLastWriteTimeUtc(StateFile);
        }
        catch (IOException)
        {
            // the next reload just reads it again
        }
    }

    private async Task<IDisposable?> AcquireFileLockAsync()
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);

        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Warning: couldn't lock {Path.GetFileName(LockPath)} - {ex.Message}. Writing the event state without the lock.");
                return null;
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: handling an event list change failed - {ex.Message}");
        }
    }
}
