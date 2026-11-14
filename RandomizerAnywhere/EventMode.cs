using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RandomizerAnywhere.Config;

namespace RandomizerAnywhere;

internal sealed record EventMap(int TrackId, bool Unlimiter);

internal sealed record EventFinish(string Login, string Nickname, int Time, DateTimeOffset At, string Server);

internal sealed record EventServerInfo(string Label, string Login, int? TrackId, string? MapName, int Players, int MaxPlayers, DateTimeOffset Updated);

internal sealed record EventStanding(string Login, string Nickname, int Maps, DateTimeOffset LastAt);

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

    // with EventMapsSource = "tmx": the list as TMX last answered it (kept in the shared state file,
    // so every server works from the same list), and when it was refreshed
    private List<int> tmxPool = [];
    private HashSet<int> tmxUnlimiter = [];
    private DateTimeOffset? tmxPoolUpdated;

    // the state's revision as last read or written (see StateFileModel.Revision)
    private long revision;

    // set (SyncHubUrl): the state lives on the hub, not in StateFile
    private readonly SyncClient sync;

    // set once, by whichever server saw the last open map finished; the end of the event for all
    private DateTimeOffset? endedAt;

    private DateTime? mapsWriteTimeUtc;
    private DateTime? stateWriteTimeUtc;

    // Raised (from the reload loop or after this server's own write) whenever the list or the
    // progress changed, so the widgets can be refreshed.
    public event Action? Changed;

    public EventMode(AppConfig config, SyncClient sync)
    {
        this.config = config;
        this.sync = sync;
    }

    private bool Remote => sync.Enabled;

    private DateTime lastHubWarning;

    public bool Everyday => config.EventEveryday;

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
                return !string.IsNullOrWhiteSpace(title) ? title
                    : !string.IsNullOrWhiteSpace(config.EventTitle) ? config.EventTitle
                    : "Event";
            }
        }
    }

    public bool UsesTmxSource => config.EventMapsFromTmx;

    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (gate)
            {
                return endedAt;
            }
        }
    }

    // who finished the most event maps first, then who got there earlier
    public IReadOnlyList<EventStanding> Standings()
    {
        lock (gate)
        {
            var listed = maps.Select(m => m.TrackId).ToHashSet();
            return [.. finished.Where(kv => listed.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value.Login))
                .GroupBy(kv => kv.Value.Login, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EventStanding(g.Key, g.OrderByDescending(kv => kv.Value.At).First().Value.Nickname, g.Count(), g.Max(kv => kv.Value.At)))
                .OrderByDescending(s => s.Maps)
                .ThenBy(s => s.LastAt)];
        }
    }

    // first and last finish on the list, for "30 maps in 2:14 h"
    public (DateTimeOffset First, DateTimeOffset Last)? FinishSpan()
    {
        lock (gate)
        {
            var listed = maps.Select(m => m.TrackId).ToHashSet();
            var times = finished.Where(kv => listed.Contains(kv.Key)).Select(kv => kv.Value.At).ToList();
            return times.Count == 0 ? null : (times.Min(), times.Max());
        }
    }

    public bool TmxRefreshDue
    {
        get
        {
            lock (gate)
            {
                return UsesTmxSource && (tmxPoolUpdated is null || DateTimeOffset.UtcNow - tmxPoolUpdated > TimeSpan.FromMinutes(config.EventTmxRefreshMinutes));
            }
        }
    }

    // Takes TMX's current answer for the event query (already without impossible/skip-listed maps).
    // Maps stay in the list once in it - finished ones as done; one TMX no longer lists and nobody
    // here finished (a record came from elsewhere) leaves it; new uploads are added at the end.
    // A list cut short (complete = false) only adds, it never removes.
    public async Task MergeTmxPoolAsync(IReadOnlyList<int> fetched, bool complete, IReadOnlyCollection<int>? unlimiterIds = null)
    {
        if (!Enabled || !UsesTmxSource)
        {
            return;
        }

        var changed = false;

        await UpdateStateAsync(() =>
        {
            changed = false;

            // another server refreshed it while this one asked TMX
            if (tmxPoolUpdated is { } updated && DateTimeOffset.UtcNow - updated < TimeSpan.FromMinutes(config.EventTmxRefreshMinutes) / 2)
            {
                return;
            }

            var fresh = fetched.ToHashSet();
            var kept = tmxPool.Where(id => (finished.ContainsKey(id) && !Everyday) || fresh.Contains(id) || !complete).ToList();
            var added = fetched.Where(id => !kept.Contains(id)).ToList();
            var merged = kept.Concat(added).ToList();

            changed = !merged.SequenceEqual(tmxPool);
            if (changed)
            {
                Console.WriteLine($"Event: TMX list refreshed - {merged.Count} map(s), {added.Count} new, {tmxPool.Count - kept.Count} gone (record from elsewhere).");
            }

            if (unlimiterIds is not null)
            {
                var flagged = merged.Where(unlimiterIds.Contains).ToHashSet();
                changed |= !flagged.SetEquals(tmxUnlimiter);
                tmxUnlimiter = flagged;
            }

            tmxPool = merged;
            tmxPoolUpdated = DateTimeOffset.UtcNow;

            if (Everyday)
            {
                // a finish on a map that left the list (its record is on TMX now) is history
                var listed = merged.ToHashSet();
                foreach (var id in finished.Keys.Where(id => !listed.Contains(id)).ToList())
                {
                    finished.Remove(id);
                }
            }
            maps = [.. tmxPool.Select(id => new EventMap(id, tmxUnlimiter.Contains(id)))];
        });

        if (changed)
        {
            RaiseChanged();
        }
    }

    // Ends the event when no open map is left. True for the one call that ended it.
    public async Task<bool> TryEndAsync()
    {
        if (!Enabled || Everyday)
        {
            return false;
        }

        var ended = false;

        await UpdateStateAsync(() =>
        {
            ended = false;
            if (endedAt is null && maps.Count > 0 && maps.All(m => finished.ContainsKey(m.TrackId)))
            {
                endedAt = DateTimeOffset.UtcNow;
                ended = true;
            }
        });

        if (ended)
        {
            Console.WriteLine($"Event: '{Title}' finished - every map on the list is done.");
            RaiseChanged();
        }

        return ended;
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

    // The open maps this server is meant to play (EventUnlimiterMaps): a vanilla server skips the
    // Unlimiter maps (a client without the mod can't load them), an Unlimiter server plays only
    // those or all of them.
    public IReadOnlyList<EventMap> OpenMapsForThisServer
    {
        get
        {
            lock (gate)
            {
                return [.. maps.Where(m => !finished.ContainsKey(m.TrackId) && PlaysHere(m))];
            }
        }
    }

    // What this server should draw its next map from: its open maps minus the ones another server
    // is playing right now, so two servers never run the same map at once. When only maps that
    // run elsewhere are left, those come back rather than nothing.
    public bool PlaysHere(EventMap map) => config.EventUnlimiterMaps switch
    {
        "only" => map.Unlimiter,
        "include" => true,
        _ => !map.Unlimiter,
    };

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
        await ReloadStateAsync(force: true);

        lock (gate)
        {
            Console.WriteLine($"Event: '{Title}' - {maps.Count} map(s) {(UsesTmxSource ? "from TMX (refreshed every " + config.EventTmxRefreshMinutes + " min)" : "in " + MapsFile)}, {maps.Count(m => finished.ContainsKey(m.TrackId))} finished (progress in {StateFile}), this server is '{ServerLabel}' (Unlimiter maps: {config.EventUnlimiterMaps}){(Everyday ? ", everyday list" : string.Empty)}{(Remote ? ", shared through the hub " + config.SyncHubUrl : string.Empty)}{(endedAt is not null ? " - already finished" : string.Empty)}.");
        }

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
                changed |= await ReloadStateAsync(force: false);
                changed |= await ForgetStaleFinishesAsync();

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
            isFirst = false;
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

    // Everyday list: a map finished here but still without a TMX record a day later (nobody
    // uploaded the replay) is open again. Only maps TMX still lists count - a map that got its
    // record leaves the list at the next TMX refresh anyway.
    public static readonly TimeSpan EverydayFinishKeptFor = TimeSpan.FromDays(1);
    private DateTimeOffset lastStaleCheck;

    private async Task<bool> ForgetStaleFinishesAsync()
    {
        if (!Everyday || DateTimeOffset.UtcNow - lastStaleCheck < TimeSpan.FromMinutes(10))
        {
            return false;
        }

        lastStaleCheck = DateTimeOffset.UtcNow;

        bool any;
        lock (gate)
        {
            any = finished.Any(kv => DateTimeOffset.UtcNow - kv.Value.At > EverydayFinishKeptFor);
        }

        if (!any)
        {
            return false;
        }

        var removed = 0;
        await UpdateStateAsync(() =>
        {
            var stale = finished.Where(kv => DateTimeOffset.UtcNow - kv.Value.At > EverydayFinishKeptFor).Select(kv => kv.Key).ToList();
            removed = stale.Count;
            foreach (var id in stale)
            {
                finished.Remove(id);
            }
        });

        if (removed > 0)
        {
            Console.WriteLine($"Open maps: {removed} map(s) finished more than a day ago still have no TMX record - open again.");
        }

        return removed > 0;
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
        if (UsesTmxSource)
        {
            return false;
        }

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
        // +1 on every write; a write through the hub only goes through on the revision it read
        public long Revision { get; set; }
        public Dictionary<string, EventFinish> Finished { get; set; } = [];
        public Dictionary<string, EventServerInfo> Servers { get; set; } = [];
        public List<int> TmxPool { get; set; } = [];
        public List<int> TmxUnlimiter { get; set; } = [];
        public DateTimeOffset? TmxPoolUpdated { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    // memory <- model; call under gate
    private void ApplyModel(StateFileModel model)
    {
        revision = model.Revision;
        finished = model.Finished
            .Where(kv => int.TryParse(kv.Key, out _))
            .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        servers = new Dictionary<string, EventServerInfo>(model.Servers, StringComparer.OrdinalIgnoreCase);
        tmxPool = model.TmxPool;
        tmxUnlimiter = [.. model.TmxUnlimiter];
        tmxPoolUpdated = model.TmxPoolUpdated;
        endedAt = model.EndedAt;

        if (UsesTmxSource)
        {
            maps = [.. tmxPool.Select(id => new EventMap(id, tmxUnlimiter.Contains(id)))];
        }
    }

    // model <- memory; call under gate
    private StateFileModel BuildModel() => new()
    {
        Revision = revision,
        Finished = finished.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
        Servers = new Dictionary<string, EventServerInfo>(servers),
        TmxPool = [.. tmxPool],
        TmxUnlimiter = [.. tmxUnlimiter],
        TmxPoolUpdated = tmxPoolUpdated,
        EndedAt = endedAt,
    };

    private Task<bool> ReloadStateAsync(bool force) => Remote ? ReloadRemoteStateAsync(force) : Task.FromResult(ReloadStateIfChanged(force));

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
                ApplyModel(model);
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

    // the hub's copy, when it moved on since the last look
    private async Task<bool> ReloadRemoteStateAsync(bool force)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var model = JsonSerializer.Deserialize<StateFileModel>(await sync.GetEventStateAsync(budget.Token)) ?? new StateFileModel();

            lock (gate)
            {
                if (!force && model.Revision == revision)
                {
                    return false;
                }

                ApplyModel(model);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Diagnostics.Trace($"event state from the hub: {ex.Message}");
            return false;
        }
    }

    // Read-modify-write of the shared state: under the cross-process lock on the local file, or
    // through the hub, where a write only lands on the revision it was based on - when another
    // server wrote in between, read again and redo the change. modify() may therefore run more
    // than once and must set every outcome it reports (not just flip it on).
    private async Task UpdateStateAsync(Action modify)
    {
        if (Remote)
        {
            await UpdateRemoteStateAsync(modify);
            return;
        }

        using var fileLock = await AcquireFileLockAsync();

        ReloadStateIfChanged(force: false);

        StateFileModel model;
        lock (gate)
        {
            modify();
            revision++;
            model = BuildModel();
        }

        await WriteStateFileAsync(model);
    }

    private async Task UpdateRemoteStateAsync(Action modify)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var current = JsonSerializer.Deserialize<StateFileModel>(await sync.GetEventStateAsync(budget.Token)) ?? new StateFileModel();

                string json;
                long baseRevision;
                lock (gate)
                {
                    ApplyModel(current);
                    baseRevision = revision;
                    modify();
                    json = JsonSerializer.Serialize(BuildModel(), StateJson);
                }

                if (await sync.PutEventStateAsync(baseRevision, json, budget.Token) is { } written)
                {
                    lock (gate)
                    {
                        revision = written;
                    }

                    return;
                }

                // another server wrote first - take its state and do the change again
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                // the hub is away: the change stays in this server's memory only and the next
                // read from the hub replaces it
                Diagnostics.Trace($"event hub update failed: {ex.Message}");
                if (DateTime.UtcNow - lastHubWarning > TimeSpan.FromMinutes(10))
                {
                    lastHubWarning = DateTime.UtcNow;
                    Console.WriteLine($"Warning: the event hub can't be reached ({ex.Message}) - this server shows what it last had and keeps trying.");
                }

                lock (gate)
                {
                    modify();
                }

                return;
            }
        }

        Console.WriteLine("Warning: the event hub kept changing under this server's update - gave up after 5 tries.");
    }

    private async Task WriteStateFileAsync(StateFileModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        await AtomicFile.WriteAllTextAsync(StateFile, JsonSerializer.Serialize(model, StateJson));

        try
        {
            stateWriteTimeUtc = File.GetLastWriteTimeUtc(StateFile);
        }
        catch (IOException)
        {
            // the next reload just reads it again
        }
    }

    // ---- the hub's side (SyncHub) ----

    public string ExportStateJson()
    {
        ReloadStateIfChanged(force: false);
        lock (gate)
        {
            return JsonSerializer.Serialize(BuildModel(), StateJson);
        }
    }

    // Another server's write: taken only when it was based on the current revision. Returns the
    // new revision, or null when someone else wrote first (the caller reads again and retries).
    public async Task<long?> ImportStateAsync(string json, long baseRevision)
    {
        var incoming = JsonSerializer.Deserialize<StateFileModel>(json) ?? throw new JsonException("empty state");

        StateFileModel model;
        using (await AcquireFileLockAsync())
        {
            ReloadStateIfChanged(force: false);

            lock (gate)
            {
                if (revision != baseRevision)
                {
                    return null;
                }

                incoming.Revision = baseRevision + 1;
                ApplyModel(incoming);
                model = BuildModel();
            }

            await WriteStateFileAsync(model);
        }

        RaiseChanged();
        return model.Revision;
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
