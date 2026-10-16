using ManiaAPI.XmlRpc;
using RandomizerAnywhere.Config;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TmEssentials;

namespace RandomizerAnywhere;

internal sealed partial class RandomizerGame
{
    // give players time to manually save their replay (in-game "save replay" key) before
    // the map gets forcefully switched away from under them
    private const int ReplaySaveGraceMs = 8000;

    private const int PresetVoteWindowMs = 30000;

    // AGPLv3 §13: this is a modified version, so users interacting with it over the network
    private const string SourceRepoUrl = "http://github.com/cheatoskar/randomizer-anywhere-100--tmx";

    private readonly RemoteClient client;
    private readonly TmxRules tmxRules;
    private readonly AppConfig config;
    private readonly DiscordNotifier discordNotifier;
    private readonly Leaderboard leaderboard;

    private readonly Dictionary<string, Func<int, string, string[], CancellationToken, Task>> commandHandlers;
    private readonly Dictionary<string, string> nicknameCache = [];

    private Stopwatch? sessionStopwatch;
    private int sessionStopwatchMillisecondOffset;
    private MapInfo? currentMap;
    private int? currentMapTrackId;
    private int? pendingMapTrackId;

    // True once BeginRace has looked at the running challenge, whether or not it could identify
    // it. Before that, pendingMapTrackId is the best guess available; after it, currentMapTrackId
    // is the answer even when that answer is "no idea" - falling back to the last map we picked
    // is exactly how /map ended up reporting a map from a quarter of an hour earlier.
    private bool currentMapIdentified;

    // The running challenge's path as the server spells it, kept so the next BeginRace can hand
    // that exact string back to RemoveChallenge.
    private string? currentMapFileName;

    // The one answer to "which TMX map is on right now" - used by /map, /imp, /hard, the status
    // page and the stalled-map watchdog alike. Each of those used to spell out
    // "currentMapTrackId ?? pendingMapTrackId" itself, which is why a stale id showed up in the
    // chat link, the status page's preview image and the watchdog log all at once.
    private int? DisplayedMapTrackId => currentMapIdentified ? currentMapTrackId : currentMapTrackId ?? pendingMapTrackId;

    // Challenge file name (basename, no directory) -> the TMX id it was fetched by, for every map
    // this process has inserted. The id is in the file name too and that is the primary source,
    // but entries reused via FindSelectedChallengeFileNameAsync may predate that naming, so keep
    // a direct record as well.
    private readonly Dictionary<string, int> trackIdByChallengeFile = new(StringComparer.OrdinalIgnoreCase);

    // most recent first: mapHistory[0] is "-1", mapHistory[1] is "-2", etc. Capped at
    // MaxMapHistory so this can't grow unbounded over a long-running session.
    private readonly List<int> mapHistory = [];
    private const int MaxMapHistory = 10;

    // The TMX id of the map that last STARTED (BeginRace), kept across EndRace so the next
    // BeginRace can put it into the history. null when that map couldn't be identified.
    private int? lastRunningTrackId;

    private void PushMapHistory(int trackId)
    {
        mapHistory.Insert(0, trackId);
        if (mapHistory.Count > MaxMapHistory)
        {
            mapHistory.RemoveAt(mapHistory.Count - 1);
        }
    }

    // true while the current map runs in Rounds mode because of /rounds; the next advance has to
    // switch back to TimeAttack even when it reuses an already queued map
    private bool roundsModeActive;

    // Fire-and-forget with a log line instead of a silently lost exception.
    private void RunInBackground(string what, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            // started from inside a locked job, the flag would be inherited although this
            // task holds no lock - and WithoutGameLockIfBackgroundAsync would release someone else's
            holdsGameLockInBackground.Value = false;

            try
            {
                await work();
            }
            catch (OperationCanceledException) when (appToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: {what} failed - {ex.Message}");
            }
        });
    }

    // TMX metadata does not change while a map is played; caching it means the widget for a late
    // joiner costs nothing, and BeginRace never waits for tm-exchange.com.
    private readonly ConcurrentDictionary<int, (string DifficultyLabel, int Awards)> trackMetaCache = new();
    private readonly ConcurrentDictionary<int, string> trackNameCache = new();
    private static readonly TimeSpan TrackMetaBudget = TimeSpan.FromSeconds(15);

    // login null = everyone
    private void ShowMapInfoWidgetInBackground(int trackId, int authorTime, string? login)
    {
        RunInBackground($"showing the map info widget for {trackId}", async () =>
        {
            if (!trackMetaCache.TryGetValue(trackId, out var meta))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                budget.CancelAfter(TrackMetaBudget);
                meta = await tmxRules.GetTrackMetaAsync(trackId, budget.Token);

                if (trackMetaCache.Count > 500)
                {
                    trackMetaCache.Clear();
                }

                trackMetaCache[trackId] = meta;
            }

            // the map may have changed while TMX was answering - don't show stale info
            if (currentMapTrackId != trackId)
            {
                return;
            }

            var manialink = BuildMapInfoManialink(meta.DifficultyLabel, meta.Awards, authorTime);

            if (login is null)
            {
                await client.SendManialinkPageAsync(manialink, cancellationToken: appToken);
            }
            else
            {
                await client.SendManialinkPageToLoginAsync(login, manialink, cancellationToken: appToken);
            }
        });
    }
    private string? randomEnqueuedMapFileName;

    // set by /loadmap or a passed /votemap vote, consumed (and cleared) by the next
    // NextRandomMapAsync map fetch - lets that single method serve both "next random map" and
    // "this specific TMX id" without duplicating the enqueue/insert/gamemode plumbing
    private int? forcedNextMapTrackId;

    // A map loaded by /loadmap or /votemap rather than drawn at random. Finishes on it don't count
    // for the leaderboard: with one player online /votemap passes on its own vote, so anyone could
    // load the same five-second map over and over and collect a finish each time.
    private int? forcedMapQueuedTrackId;
    private bool currentMapIsForced;

    // A forced map that had no record on TMX when it was loaded is one of the project's open maps,
    // so finishing it counts - but only once per player and map, or /votemap could farm it.
    private bool forcedMapQueuedHasNoRecord;
    private bool currentForcedMapHasNoRecord;
    private readonly HashSet<(string Login, int TrackId)> forcedMapCredits = [];

    private readonly SkippedMaps skippedMaps;

    private string? votePresetName;
    private int? voteMapTrackId;
    private HashSet<string>? voteYesLogins;

    // guards against concurrent map advances - with multiple players and AutoSkipMode=Finished,
    // every player finishing the same map fires its own PlayerFinish -> NextRandomMapAsync call;
    // without this, a second call arriving while the first is still in flight would skip the
    // "fetch a new map" step (one is already queued) but still send a second NextChallenge,
    // advancing the dedicated server one extra map past what pendingMapTrackId/currentMapTrackId
    // account for - which is exactly what made /map, /imp and /hard report the previous map
    private DateTimeOffset? advanceStartedAt;

    // Diagnostics only: which step of NextRandomMapAsync is running right now, and which step of
    // the status write is running, so a stall or a dropped connection can be pinned to a step.
    private volatile string advanceStage = "idle";
    private volatile string statusStage = "idle";

    private void SetAdvanceStage(string stage)
    {
        advanceStage = stage;
        Diagnostics.Trace($"advance: {stage}");
    }

    private void SetStatusStage(string stage)
    {
        statusStage = stage;
        Diagnostics.Trace($"status: {stage}");
    }

    // The evidence behind CallbacksHealthy, printed whenever the stream is judged dead so it is
    // possible to tell afterwards whether the verdict was right.
    private string DescribeCallbackState()
    {
        var now = DateTimeOffset.UtcNow;
        var lastCallbackAge = (now - client.LastCallbackAt).TotalSeconds;
        var racingGap = lastRacingProgressAt is { } racingAt ? (racingAt - client.LastCallbackAt).TotalSeconds : (double?)null;
        var sinceMapChange = (now - lastPolledMapChangedAt).TotalSeconds;

        return $"callbacks: received={client.CallbacksReceived} lastCallbackAge={lastCallbackAge:0.0}s "
            + $"racingProgressAhead={(racingGap is { } r ? $"{r:0.0}s (limit {CallbackRacingGracePeriod.TotalSeconds:0}s)" : "n/a")} "
            + $"polledMap='{lastPolledMapName}' beginRaceMap='{lastBeginRaceMapName}' "
            + $"sinceMapChange={sinceMapChange:0.0}s (grace {CallbackGracePeriod.TotalSeconds:0}s) "
            + $"rankedCount={lastPolledRankedCount} players={lastOnlinePlayerCount} advancing={isAdvancingToNextMap} "
            + $"mapGeneration={mapGeneration} switchClaimed={SwitchAlreadyClaimed} reconnecting={reconnectInProgress}";
    }

    // An advance still "in flight" after this long is treated as dead rather than blocking
    // every future one. Without it a single hung RPC latched the guard permanently and
    // /skip, /votemap and the finish-advance all became silent no-ops until a restart.
    // It has to be longer than the advance can legitimately take: the TMX search alone may use
    // 30s plus a 40s request, then a 40s download - 30s here let a second advance start while the
    // first was still running. AdvanceBudget below bounds the advance itself, so this is only
    // the backstop.
    private static readonly TimeSpan AdvanceStallTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan AdvanceBudget = TimeSpan.FromSeconds(120);

    // which advance owns advanceStartedAt, so a stalled one finishing late cannot clear the guard
    // of the advance that replaced it
    private long advanceCounter;
    private long advanceOwner;

    private bool isAdvancingToNextMap =>
        advanceStartedAt is { } startedAt && DateTimeOffset.UtcNow - startedAt < AdvanceStallTimeout;

    // Counts BeginRace callbacks, i.e. maps that actually started. A map switch that has been
    // asked for is "claimed" for the generation it switches AWAY from, so everything still queued
    // for the old map - the second finisher's PlayerFinish, a /skip typed during the podium, the
    // stalled-map watchdog - sees the claim and does not ask for another switch. That second
    // NextChallenge is what skipped the freshly loaded map or failed with "Change in progress"
    // (81 times in the old host's log), because the guard above only stops two advances running
    // at the same moment, not one right after the other.
    private int mapGeneration;
    private int? advanceClaimGeneration;
    private DateTimeOffset advanceClaimedAt;

    // If the new map's BeginRace never arrives the claim must not block switching for good.
    private static readonly TimeSpan AdvanceClaimTimeout = TimeSpan.FromSeconds(90);

    private bool SwitchAlreadyClaimed =>
        advanceClaimGeneration == mapGeneration && DateTimeOffset.UtcNow - advanceClaimedAt < AdvanceClaimTimeout;

    // Which claim is the current one. A finish schedules its switch for later; if something else
    // (a session start, a /preset) switched the map in between, the claim has a new id and the
    // finish's switch must not run a second time.
    private int claimCounter;
    private int advanceClaimId;

    private int ClaimSwitch()
    {
        advanceClaimGeneration = mapGeneration;
        advanceClaimedAt = DateTimeOffset.UtcNow;
        return advanceClaimId = ++claimCounter;
    }

    private void ReleaseSwitchClaim() => advanceClaimGeneration = null;

    // Everything that decides about the game - callback handlers, vote timers, the watchdog,
    // background advances - runs under this one lock. Callbacks were already handled one at a
    // time, but the vote timers, the stalled-map watchdog and the delayed finish-advance ran on
    // the thread pool next to them and raced on the same unlocked fields. The status writer only
    // reads and never takes it, so a long advance can never make status.json go stale.
    private readonly SemaphoreSlim gameLock = new(1, 1);

    // How long a background job waits for the lock before giving up on this round.
    private static readonly TimeSpan GameLockWait = TimeSpan.FromSeconds(150);

    // The process-wide token from RunAsync. Background jobs use this one: a callback handler's
    // token belongs to that single callback and is cancelled/disposed once the handler returns.
    private CancellationToken appToken;

    private async Task<bool> RunUnderGameLockAsync(string what, Func<CancellationToken, Task> action)
    {
        if (!await gameLock.WaitAsync(GameLockWait, appToken))
        {
            Console.WriteLine($"Warning: {what} skipped - the game was busy for {GameLockWait.TotalSeconds:0}s.");
            return false;
        }

        try
        {
            holdsGameLockInBackground.Value = true;
            await action(appToken);
            return true;
        }
        finally
        {
            holdsGameLockInBackground.Value = false;
            gameLock.Release();
        }
    }

    // True inside RunUnderGameLockAsync, i.e. a background job (finish switch, watchdog, session
    // keeper, AutoStart) that holds the lock - as opposed to a callback handler.
    private static readonly AsyncLocal<bool> holdsGameLockInBackground = new();

    // The TMX search and download can take a minute or more. A background job that held the lock
    // all that time blocked every callback behind it, and the callback client cuts a handler off
    // after 60s - finishes, joins and BeginRace were lost. Background jobs let go of the lock for
    // that part (their advance stays guarded by isAdvancingToNextMap). A callback handler keeps
    // it: the stream waits for the handler anyway, so letting go would gain nothing.
    private async Task<T> WithoutGameLockIfBackgroundAsync<T>(Func<Task<T>> work)
    {
        if (!holdsGameLockInBackground.Value)
        {
            return await work();
        }

        gameLock.Release();
        try
        {
            return await work();
        }
        finally
        {
            // no token: the caller's finally releases the lock, so it must be held again here
            await gameLock.WaitAsync(CancellationToken.None);
        }
    }

    private int? currentMapCheckpointTotal;
    // Concurrent: written by checkpoint callbacks, enumerated by the status writer (which does
    // not take the game lock) - a plain Dictionary can throw "collection was modified" there.
    private readonly ConcurrentDictionary<string, int> playerCheckpointProgress = [];
    // tie-breaks the live "who's leading" ranking on the status page: same checkpoint count ->
    // whoever reached it first is ahead
    private readonly ConcurrentDictionary<string, DateTimeOffset> playerCheckpointTimestamp = [];

    // Set every time a map actually begins; the stalled-map watchdog measures from here.
    private DateTimeOffset? currentMapStartedAt;
    // Last player count observed by the status writer, reused by the watchdog so it
    // does not make its own server call every 10 seconds.
    private int lastOnlinePlayerCount;

    // How long BeginRace is allowed to lag behind a map change the status poll already saw.
    // A healthy callback arrives within a second or two; this is deliberately generous.
    private static readonly TimeSpan CallbackGracePeriod = TimeSpan.FromSeconds(120);

    // How far the poll's evidence of racing may run ahead of the last callback before the stream
    // counts as dead. A finish or an improved time produces a callback within a second or two.
    private static readonly TimeSpan CallbackRacingGracePeriod = TimeSpan.FromSeconds(90);

    private string? lastPolledMapName;
    private DateTimeOffset lastPolledMapChangedAt = DateTimeOffset.UtcNow;
    private bool callbackLossReported;

    // Racing evidence straight from the dedicated server: how many players hold a time on the
    // current map, and when the poll last saw that number grow. Reset by the poll (never by a
    // callback) whenever the map changes, so it can never be stale in a way that accuses a
    // healthy stream.
    private int lastPolledRankedCount;
    private DateTimeOffset? lastRacingProgressAt;

    // Reconnecting deliberately closes the old connection, which WaitForCloseAsync cannot tell
    // apart from the game server going away. The generation counter lets the keep-alive loop
    // distinguish the two, so healing the stream never exits the controller by accident.
    private volatile bool reconnectInProgress;
    private int reconnectGeneration;
    private DateTimeOffset lastCallbackRecoveryAt = DateTimeOffset.MinValue;
    private int failedRecoveries;
    private DateTimeOffset lastCallbackProbeAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan CallbackRecoveryCooldown = TimeSpan.FromMinutes(2);
    // 20 minutes, not 5. The probe only matters on an empty server, where a stalled stream
    // harms nobody until someone joins - and the first map change after that detects it
    // within CallbackGracePeriod anyway. Probing often bought nothing and cost real churn.
    private static readonly TimeSpan CallbackProbeIdle = TimeSpan.FromMinutes(20);

    // A ChallengeRestart landing while the dedicated server is still switching maps cancels
    // that switch and reloads the old map. Never probe near a map change.
    private static readonly TimeSpan CallbackProbeMapSettleTime = TimeSpan.FromMinutes(2);

    // The status loop polls the dedicated server directly; currentMapStartedAt is only ever set
    // by the BeginRace callback. So if the poll sees a new map and BeginRace never follows, the
    // callback stream is dead - and that failure is silent and nasty: the controller keeps
    // answering its own polls (status.json stays fresh, the website looks healthy) while it has
    // stopped driving the game entirely. No chat commands, no widgets, no welcome messages and
    // no map picks, so the dedicated server just rotates the playlist it has already
    // accumulated, which is what players see as "the track pool repeats and never adds maps".
    // Observed on 2026-09-02 13:00-19:00 and again 2026-09-03 from 12:05.
    //
    // That map-change witness is blind in exactly the case it is meant to catch, though: a dead
    // stream also kills auto-skip, so nothing changes the map, so the poll sees nothing move and
    // the gap never grows. On 2026-09-11 that let a 19-minute outage go undetected until the
    // dedicated server happened to rotate on its own.
    //
    // The second witness below covers that. It replaces a "players online and no callback for
    // five minutes" check that was plain wrong: online is not the same as racing, and on an
    // unlimited time limit a lobby of idle players generates no callbacks at all, so a perfectly
    // healthy stream got declared dead and reconnected every five minutes - visible on 2026-09-13
    // as reconnect pairs 5:01 apart. What matters is not silence, it is silence while something
    // is demonstrably happening, and the current ranking is the server's own record of that.
    private bool CallbacksHealthy
    {
        get
        {
            // Someone set or improved a time - the server says so - but no callback has arrived
            // since. Nothing legitimate does that.
            if (lastRacingProgressAt is { } racingAt && racingAt - client.LastCallbackAt > CallbackRacingGracePeriod)
            {
                return false;
            }

            if (lastBeginRaceMapName is null || lastPolledMapName is null)
            {
                // No BeginRace seen yet - nothing to compare against. The first map after a
                // restart is a known blind spot, so never call it dead on that alone.
                return true;
            }

            // The poll sees which map is running; the last BeginRace says which map the callback
            // stream last reported. If they differ for longer than the grace period, the BeginRace
            // for the running map never came: the stream is dead.
            //
            // This used to compare TIMES: "poll saw the change" minus "last BeginRace". But the poll
            // often sees the new map before its BeginRace has been handled, and at that moment the
            // last BeginRace is the PREVIOUS map's, so the "gap" was really how long the previous
            // map had been played. Any map longer than two minutes could trip it: 39 verdicts on
            // the old host, every one with callbacks less than a second old, each followed by a
            // needless reconnect that dropped whatever callbacks arrived meanwhile. Comparing the
            // map names needs no timing guess at all.
            // The grace runs from whichever came last: the poll seeing a new map, or a BeginRace.
            // A BeginRace for a map the poll hasn't seen yet (the poll failed or is behind) is
            // proof the stream works, not that it is dead.
            var lastSign = lastBeginRaceAt > lastPolledMapChangedAt ? lastBeginRaceAt : lastPolledMapChangedAt;
            return lastBeginRaceMapName == lastPolledMapName
                || DateTimeOffset.UtcNow - lastSign < CallbackGracePeriod;
        }
    }

    // The map name (formatting removed, as the poll stores it) from the last BeginRace callback.
    private string? lastBeginRaceMapName;
    private DateTimeOffset lastBeginRaceAt;

    private bool SessionActive => sessionStopwatch is not null;

    // Registered with the game server as chat commands (RandomizerSetup) - must match the keys of
    // commandHandlers, which the constructor checks.
    internal static readonly string[] ChatCommandNames =
    [
        "start", "stop", "end", "skip", "imp", "blockmap", "hard", "top", "rank", "map", "history",
        "rounds", "info", "help", "testhud", "votepreset", "yes", "no", "commands", "source",
        "timelimit", "tl", "preset", "presets", "loadmap", "votemap",
    ];

    public RandomizerGame(RemoteClient client, TmxRules tmxRules, AppConfig config, DiscordNotifier discordNotifier, Leaderboard leaderboard, SkippedMaps skippedMaps)
    {
        this.skippedMaps = skippedMaps;
        this.client = client;
        this.tmxRules = tmxRules;
        this.config = config;
        this.discordNotifier = discordNotifier;
        this.leaderboard = leaderboard;

        // case-insensitive: "/Skip" and "/SKIP" are the same command to a player
        commandHandlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = StartAsync,
            ["stop"] = StopAsync,
            ["end"] = StopAsync,
            ["skip"] = SkipAsync,
            ["imp"] = ImpossibleAsync,
            ["blockmap"] = BlockMapAsync,
            ["hard"] = HardAsync,
            ["top"] = TopAsync,
            ["rank"] = RankAsync,
            ["map"] = MapAsync,
            ["history"] = HistoryAsync,
            ["rounds"] = RoundsAsync,
            ["info"] = InfoAsync,
            ["help"] = InfoAsync,
            ["testhud"] = TestHudAsync,
            ["votepreset"] = VotePresetAsync,
            ["yes"] = YesAsync,
            ["no"] = NoAsync,
            ["commands"] = CommandsAsync,
            ["source"] = SourceAsync,
            ["timelimit"] = TimeLimitAsync,
            ["tl"] = TimeLimitAsync,
            ["preset"] = PresetAsync,
            ["presets"] = PresetsAsync,
            ["loadmap"] = LoadMapAsync,
            ["votemap"] = VoteMapAsync
        };

        if (!commandHandlers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ChatCommandNames))
        {
            Console.WriteLine("Warning: ChatCommandNames and the command handlers disagree - some commands won't work.");
        }

        /*client.Callback += async (methodName, methodParams, cancellationToken) =>
        {
            Console.WriteLine($"{methodName} {string.Join(' ', methodParams.Select(x =>
            {
                return x is Dictionary<string, object> dict
                    ? $"{{{string.Join(", ", dict.Select(kv => $"{kv.Key}: {kv.Value}"))}}}"
                    : x?.ToString() ?? "null";
            }))}");
        };*/
    }

    // Every game callback takes the game lock (see gameLock). Callbacks still arrive one at a time;
    // the lock only keeps the vote timers, the watchdog and background advances out while one runs.
    // A handler that cannot get the lock in time hits RemoteClient's handler cutoff like any other
    // slow handler, so the stream keeps moving.
    private void OnGameCallback(string method, Func<object[], CancellationToken, Task> handler)
    {
        client.On(method, async (methodParams, cancellationToken) =>
        {
            await gameLock.WaitAsync(cancellationToken);
            try
            {
                await handler(methodParams, cancellationToken);
            }
            finally
            {
                gameLock.Release();
            }
        });
    }

    private void RegisterCallbacks()
    {
        OnGameCallback("TrackMania.BeginRace", async (methodParams, cancellationToken) =>
        {
            // a new map has started: whatever switch was claimed for the previous one is done
            mapGeneration++;

            try
            {
                var mapInfo = (Dictionary<string, object>)methodParams[0];

                // straight from the callback, before any call that could fail - the health check
                // compares it with the map the status poll sees
                if (mapInfo.TryGetValue("Name", out var beginRaceName) && beginRaceName is string name)
                {
                    lastBeginRaceMapName = TmFormatCodeRegex().Replace(name, string.Empty);
                    lastBeginRaceAt = DateTimeOffset.UtcNow;
                }

                currentMap = new MapInfo(
                    AuthorTime: (int)mapInfo["AuthorTime"],
                    GoldTime: (int)mapInfo["GoldTime"],
                    SilverTime: (int)mapInfo["SilverTime"],
                    BronzeTime: (int)mapInfo["BronzeTime"]
                );
                playerCheckpointProgress.Clear();
                playerCheckpointTimestamp.Clear();
                currentMapCheckpointTotal = null;
                currentMapStartedAt = DateTimeOffset.UtcNow;

                var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);

                // Ask the running challenge what it is instead of assuming it is whatever we last
                // queued - the dedicated server also advances its own selection, and then those
                // two are different maps. See InsertedChallengePath.
                var previousMapFileName = currentMapFileName;
                currentMapFileName = info.FileName;
                currentMapTrackId = ResolveRunningTrackId(info.FileName);
                currentMapIdentified = true;

                var mapChanged = previousMapFileName is null
                    || !ChallengeFileKey(previousMapFileName).Equals(ChallengeFileKey(info.FileName ?? string.Empty), StringComparison.OrdinalIgnoreCase);

                // History is "the maps before this one", so it is written when a DIFFERENT map
                // starts. It used to be written on every EndRace, and a ChallengeRestart (the idle
                // probe, /rounds, /stop) ends a race without changing the map - the current map then
                // showed up as "-1" and "/imp -1" reported the map still being played.
                if (mapChanged && lastRunningTrackId is { } previousTrackId)
                {
                    PushMapHistory(previousTrackId);
                }

                if (mapChanged)
                {
                    // /rounds only ever applies to the map it was used on
                    roundsModeActive = false;

                    currentMapIsForced = currentMapTrackId is { } startedId && startedId == forcedMapQueuedTrackId;
                    currentForcedMapHasNoRecord = currentMapIsForced && forcedMapQueuedHasNoRecord;
                    if (currentMapIsForced)
                    {
                        forcedMapQueuedTrackId = null;
                        forcedMapQueuedHasNoRecord = false;
                    }
                }

                lastRunningTrackId = currentMapTrackId;

                LogMapTransition(info);

                // Drop the map we just came off the server's playlist. Until this existed the
                // selection only ever grew, so any advance that did not land on the map we queued
                // served an already-played one - which is what players see as "it keeps giving us
                // maps we already finished". Done here rather than in EndRace because the outgoing
                // map is only safely removable once a different one is confirmed running.
                if (previousMapFileName is not null && mapChanged)
                {
                    await RemovePlayedChallengeAsync(previousMapFileName, cancellationToken);
                }

                currentMapCheckpointTotal = info.NbCheckpoints;

                // Fallback only. A skip-listed map is kept out before it ever reaches the game server
                // (see TmxRules pick, /loadmap, /votemap and the guard in NextRandomMapAsync), so this
                // should never fire. If one does get on anyway - say the id was added to the file
                // while that map was already queued - move straight on, from a separate task so this
                // handler, which holds the whole callback stream, isn't stuck waiting on a TMX fetch.
                if (currentMapTrackId is { } skippedId && skippedMaps.Contains(skippedId) && SessionActive)
                {
                    Console.WriteLine($"Skip list: map {skippedId} came up but is on this server's skip list - moving on.");
                    var generation = mapGeneration;
                    RunInBackground($"skipping past skip-listed map {skippedId}", async () =>
                    {
                        await Task.Delay(1500, appToken);
                        await RunUnderGameLockAsync($"skipping past skip-listed map {skippedId}", async token =>
                        {
                            // something else already moved the server on in the meantime
                            if (mapGeneration == generation)
                            {
                                await NextRandomMapAsync(goalReached: false, token);
                            }
                        });
                    });
                }

                // a map with no checkpoints at all has nothing to count, and "0/0" is just noise
                if (info.NbCheckpoints is { } total && total > 0)
                {
                    await client.SendManialinkPageAsync(BuildCheckpointManialink(0, total), cancellationToken: cancellationToken);
                }

                if (info.LapRace)
                {
                    await client.SendManialinkPageAsync(BuildRoundsPromptManialink(info.NbLaps), cancellationToken: cancellationToken);
                }
                else
                {
                    // clear a leftover prompt from a previous multilap map - a new one only gets
                    // sent above when the CURRENT map is itself a multilap map
                    await HideManialinkAsync(RoundsPromptManialinkId, cancellationToken);
                }

                if (currentMapTrackId is { } trackIdForMeta)
                {
                    // TMX is slow at times (up to its 40s budget) and this handler holds the whole
                    // callback stream, so the info widget is filled in from the side
                    ShowMapInfoWidgetInBackground(trackIdForMeta, info.AuthorTime, login: null);
                }
            }
            catch (Exception ex)
            {
                // an unhandled exception here would kill the callback dispatch loop entirely,
                // silently freezing every future event (joins, chat, finishes, the status page) -
                // every handler below is wrapped for the same reason
                Console.WriteLine($"Warning: checkpoint HUD setup failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.EndRace", async (methodParams, cancellationToken) =>
        {
            try
            {
                currentMap = null;
                currentMapTrackId = null;
                currentMapIdentified = false;
                randomEnqueuedMapFileName = null;
                currentMapCheckpointTotal = null;
                playerCheckpointProgress.Clear();
                playerCheckpointTimestamp.Clear();
                // not hiding the manialink here: SendHideManialinkPage has no per-widget id to target
                // without also nuking the always-on top10 panel, and BeginRace overwrites the CP
                // counter text for the next map anyway, so the stale text is only visible for a moment
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: EndRace handling failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.PlayerCheckpoint", async (methodParams, cancellationToken) =>
        {
            try
            {
                var login = (string)methodParams[1];
                var checkpointIndex = (int)methodParams[4];

                if (currentMapCheckpointTotal is not { } total || total <= 0)
                {
                    return;
                }

                // TMF numbers the finish line as a checkpoint too, so without the clamp the last
                // one a player crosses reads as "4/3"
                var current = Math.Min(checkpointIndex + 1, total);
                playerCheckpointProgress[login] = current;
                playerCheckpointTimestamp[login] = DateTimeOffset.UtcNow;

                await client.SendManialinkPageToLoginAsync(login, BuildCheckpointManialink(current, total), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to update checkpoint HUD - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.PlayerConnect", async (methodParams, cancellationToken) =>
        {
            try
            {
                var login = (string)methodParams[0];

                nicknameCache[login] = await client.GetPlayerNicknameAsync(login, cancellationToken);

                await SendWelcomeMessageAsync(login, cancellationToken);
                await SendTop10PanelAsync(cancellationToken);
                await SendMapWidgetsToLoginAsync(login, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: PlayerConnect handling failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.PlayerChat", async (methodParams, cancellationToken) =>
        {
            // Pulled out of the try so the catch below can still answer the player.
            var login = methodParams.Length > 1 ? methodParams[1] as string : null;

            try
            {
                var playerUid = (int)methodParams[0];
                var message = (string)methodParams[2];
                var isRegisteredCmd = (bool)methodParams[3];

                if (isRegisteredCmd)
                {
                    await OnCommand(playerUid, login!, message, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: chat command handling failed - {ex.Message}");

                // This used to be console-only, so the player who typed the command saw
                // nothing at all and reasonably concluded the server was ignoring them.
                if (login is not null)
                {
                    try
                    {
                        // appToken: when the handler was cut off, its own token is already cancelled
                        await SendMessageAsync(login, $"$F00That command failed: {ex.Message}", appToken);
                    }
                    catch (Exception replyEx)
                    {
                        Console.WriteLine($"Warning: could not tell {login} the command failed - {replyEx.Message}");
                    }
                }
            }
        });

        OnGameCallback("TrackMania.PlayerFinish", async (methodParams, cancellationToken) =>
        {
            try
            {
                var playerUid = (int)methodParams[0];
                var login = (string)methodParams[1];
                var score = (int)methodParams[2];

                await OnPlayerFinish(playerUid, login, score, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: PlayerFinish handling failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.StatusChanged", async (methodParams, cancellationToken) =>
        {
            try
            {
                var statusCode = (TrackManiaStatusCode)(int)methodParams[0];

                if (SessionActive)
                {
                    switch (statusCode)
                    {
                        case TrackManiaStatusCode.Play:
                            sessionStopwatch?.Start();
                            break;
                        case TrackManiaStatusCode.Finish:
                            await FinishMapAsync(cancellationToken);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: StatusChanged handling failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.EndRound", async (methodParams, cancellationToken) =>
        {
            try
            {
                await FinishMapAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: EndRound handling failed - {ex.Message}");
            }
        });

        OnGameCallback("TrackMania.PlayerManialinkPageAnswer", async (methodParams, cancellationToken) =>
        {
            try
            {
                var playerUid = (int)methodParams[0];
                var login = (string)methodParams[1];
                var answer = (int)methodParams[2];
                await HandleManialinkAnswerAsync(playerUid, login, answer, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: manialink click handling failed - {ex.Message}");
            }
        });
    }

    // action IDs for clickable manialink widgets - sent back verbatim by the client as the
    // "Answer" param of TrackMania.PlayerManialinkPageAnswer when a player clicks a button
    private const int ActionVoteYes = 1;
    private const int ActionVoteNo = 2;
    private const int ActionRoundsAccept = 3;
    private const int ActionClosePresetList = 4;
    private const int ActionCloseHistory = 5;
    private const int ActionPresetBase = 10;
    private const int ActionHistoryBase = 20;

    private const string RoundsPromptManialinkId = "rounds_prompt";
    private const string VotePopupManialinkId = "vote_popup";
    private const string PresetListManialinkId = "preset_list";
    private const string MapInfoManialinkId = "map_info";
    private const string HistoryManialinkId = "history_list";

    // preset names as last shown to any player via the /presets widget, in click-index order -
    // clicking entry N in that widget re-runs it through the same path as /votepreset <name>
    private IReadOnlyList<string>? lastShownPresetNames;

    // track ids as last shown to any player via the /history widget, in click-index order -
    // clicking entry N in that widget re-runs it through the same path as "/map -<N+1>"
    private IReadOnlyList<int>? lastShownHistoryTrackIds;

    private async Task HandleManialinkAnswerAsync(int playerUid, string login, int answer, CancellationToken cancellationToken)
    {
        switch (answer)
        {
            case ActionVoteYes:
                await YesAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionVoteNo:
                await NoAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionRoundsAccept:
                await RoundsAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionClosePresetList:
                await HideManialinkToLoginAsync(login, PresetListManialinkId, cancellationToken);
                break;
            case ActionCloseHistory:
                await HideManialinkToLoginAsync(login, HistoryManialinkId, cancellationToken);
                break;
            default:
                if (answer >= ActionHistoryBase)
                {
                    var historyIndex = answer - ActionHistoryBase;
                    if (lastShownHistoryTrackIds is { } trackIds && historyIndex >= 0 && historyIndex < trackIds.Count)
                    {
                        await MapAsync(playerUid, login, [$"-{historyIndex + 1}"], cancellationToken);
                        await HideManialinkToLoginAsync(login, HistoryManialinkId, cancellationToken);
                    }
                    break;
                }

                var presetIndex = answer - ActionPresetBase;
                if (presetIndex >= 0 && lastShownPresetNames is { } names && presetIndex < names.Count)
                {
                    await VotePresetAsync(playerUid, login, [names[presetIndex]], cancellationToken);
                    await HideManialinkToLoginAsync(login, PresetListManialinkId, cancellationToken);
                }
                break;
        }
    }

    private async Task FinishMapAsync(CancellationToken cancellationToken)
    {
        // TODO: there should be some second tolerance
        var sessionExpired = config.TimeLimit.TotalMilliseconds > 0
            && sessionStopwatch is not null
            && sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset >= config.TimeLimit.TotalMilliseconds;

        // freeze time if it was still running
        if (sessionStopwatch?.IsRunning == true)
        {
            sessionStopwatch.Stop();

            if (!sessionExpired)
            {
                await SendFrozenTimeMessageAsync(cancellationToken);
            }
        }

        // if session expired, stop the session and reset the time limit
        if (sessionExpired)
        {
            await SendMessageAsync("$FF0Time limit reached! Stopping the session.", cancellationToken);

            // like /stop: the session keeper must not start it again half a minute later
            sessionStoppedByAdmin = true;
            await StopSessionAsync(cancellationToken);
        }
        else
        {
            await SetCalculatedTimeLimitAsync(cancellationToken);
        }
    }

    public async Task OnCommand(int playerUid, string login, string message, CancellationToken cancellationToken)
    {
        var trimmedMessage = message.TrimStart('/');
        var firstSpaceIndex = trimmedMessage.IndexOf(' ');
        var mainCommand = firstSpaceIndex == -1 ? trimmedMessage : trimmedMessage.Substring(0, firstSpaceIndex);

        if (commandHandlers.TryGetValue(mainCommand, out var handler))
        {
            var args = CommandArgsRegex().Matches(trimmedMessage)
                .Cast<Match>()
                .Skip(1)
                .Select(m => m.Value.Trim('"'))
                .ToArray();

            await handler(playerUid, login, args, cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        appToken = cancellationToken;
        RegisterCallbacks();

        // The dedicated server's own anti-cheat bans players for "Time incoherence", and on a
        // randomizer serving RPG, stunt and trial maps straight off TMX that fires on perfectly
        // legitimate finishes - monster11_02 was banned on 2026-09-03 for completing an RPG map.
        // ladder_mode is inactive now, which should stop it at the source; this clears anything
        // that still slips through, so a false ban can never outlive a restart. Nothing in this
        // controller ever bans anyone deliberately, so an empty ban list is the correct state.
        try
        {
            await client.CallAsync("CleanBanList", [], cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to clear the ban list - {ex.Message}");
        }

        try
        {
            await SendWelcomeMessageAsync(login: null, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to send initial welcome message - {ex.Message}");
        }

        try
        {
            await SendTop10PanelAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to update initial top 10 panel - {ex.Message}");
        }

        _ = StatusWriteLoopAsync(cancellationToken);
        _ = StalledMapWatchLoopAsync(cancellationToken);

        if (config.AutoStart)
        {
            // the server may still be finishing its own startup map load (ServerSetup's warmup
            // challenge), so an immediate map change here can race it - and not always with just
            // a catchable "Change in progress" RPC exception. Observed live: the race can instead
            // crash the dedicated server process outright, which tears down the whole XML-RPC
            // socket (unhandled IOException, no chance to retry). A short grace delay before the
            // very first attempt avoids hitting that window in the first place; the retry loop
            // below still exists for the milder, catchable case.
            await Task.Delay(3000, cancellationToken);

            // These retries used to do nothing: StartSessionAsync marked the session active before
            // its first await, so attempts 2-5 returned at once and reported success while the
            // server sat on the warmup map. It now only counts as started once the first map is
            // on its way, and the session keeper in the watchdog loop keeps retrying after these.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await RunUnderGameLockAsync("starting the session", StartSessionAsync);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"Warning: StartSessionAsync attempt {attempt} failed - {ex.Message}");
                    await Task.Delay(2000, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"Warning: StartSessionAsync attempt {attempt} failed - {ex.Message}. The session keeper will keep trying.");
                }
            }
        }

        // WaitForCloseAsync is what keeps the process alive. A reconnect closes the old
        // connection on purpose, so returning from it is not automatically a shutdown - check
        // whether a reconnect caused it and re-attach to the new connection if so.
        while (!cancellationToken.IsCancellationRequested)
        {
            // A reconnect that failed leaves no connection at all. Waiting on it would throw at
            // once and be read as "the game server closed the connection" - exit 75, even with
            // players on. Keep redialling instead; TryRecoverCallbacksAsync decides when giving up
            // and restarting is acceptable.
            if (reconnectInProgress)
            {
                // a reconnect is swapping the connection right now - wait for it to finish
                await Task.Delay(200, cancellationToken);
                continue;
            }

            if (!client.IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await TryRecoverCallbacksAsync(cancellationToken, ignoreCooldown: true);
                continue;
            }

            var generation = Volatile.Read(ref reconnectGeneration);
            Exception? closeError = null;
            var closeWaitStartedAt = Stopwatch.GetTimestamp();

            try
            {
                await client.WaitForCloseAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // the connection was torn out from under us by a reconnect
                closeError = ex;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (Volatile.Read(ref reconnectGeneration) == generation && !reconnectInProgress)
            {
                // The game server closed the connection and no reconnect of ours caused it.
                // Falling out of RunAsync here ends Main and exits with code 0, which
                // Restart=on-failure reads as "finished successfully" and leaves the server
                // dead - that is exactly how it sat down for two days from 2026-09-04 19:18.
                // Exit non-zero so it is recorded as a failure and systemd brings it back.
                Console.WriteLine("ERROR: the game server closed the XML-RPC connection - exiting so systemd restarts us.");

                // The one event nobody has been able to explain: 6 exits in 12 days, all right after
                // the same map was picked, and the dedicated server never crashed on its own. Record
                // everything needed to tell "the socket dropped while the server kept running" (the
                // dedicated process is still alive below) from "the server really went away".
                Diagnostics.Dump(
                    "XML-RPC connection closed with no reconnect of ours",
                    $"WaitForCloseAsync {(closeError is null ? "returned normally" : $"threw {closeError.GetType().FullName}: {closeError.Message}")} after "
                    + $"{Stopwatch.GetElapsedTime(closeWaitStartedAt).TotalMinutes:0.0} min | reconnectInProgress={reconnectInProgress} generation={generation} | "
                    + $"advance: stage='{advanceStage}' {(advanceStartedAt is { } adv ? $"running for {(DateTimeOffset.UtcNow - adv).TotalSeconds:0.0}s" : "not running")} | "
                    + $"{DescribeCallbackState()} | pending/forced map ids: pending={pendingMapTrackId} forced={forcedNextMapTrackId}",
                    recentLines: 150,
                    force: true);
                await Console.Out.FlushAsync(cancellationToken);
                Environment.Exit(75);
            }

            while (reconnectInProgress && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    // Step 1 of never letting a stall matter: rebuild the connection instead of restarting the
    // process. Works with a full server, takes seconds, and nobody is kicked.
    private async Task TryRecoverCallbacksAsync(CancellationToken cancellationToken, bool ignoreCooldown = false)
    {
        if (!ignoreCooldown && DateTimeOffset.UtcNow - lastCallbackRecoveryAt < CallbackRecoveryCooldown)
        {
            return;
        }

        // the status loop, the probe and the keep-alive loop can all ask for this; one at a time
        if (Interlocked.Exchange(ref recoveryRunning, 1) == 1)
        {
            return;
        }

        lastCallbackRecoveryAt = DateTimeOffset.UtcNow;
        Console.WriteLine("Rebuilding the game server connection to recover the callback stream...");

        reconnectInProgress = true;
        Interlocked.Increment(ref reconnectGeneration);

        try
        {
            // A dedicated server that accepts the TCP connection but never answers (a hung main
            // loop) would otherwise keep this waiting forever - and with it reconnectInProgress,
            // the keep-alive loop and every future recovery.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ReconnectBudget);
            await client.ReconnectAsync(budget.Token);

            // Give the fresh connection a clean slate rather than judging it on the old one's
            // evidence; if it is still deaf, the next map change re-detects it.
            var now = DateTimeOffset.UtcNow;
            currentMapStartedAt = now;
            lastPolledMapChangedAt = now;
            lastBeginRaceMapName = lastPolledMapName;
            lastBeginRaceAt = now;
            lastPolledRankedCount = 0;
            lastRacingProgressAt = null;
            callbackLossReported = false;
            failedRecoveries = 0;

            Console.WriteLine("Reconnected - callback stream restored without a restart.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            failedRecoveries++;
            Console.WriteLine($"ERROR: reconnect failed ({failedRecoveries}) - {ex.Message}");

            // Last resort only: if redialling keeps failing and nobody is racing, hand the
            // problem to systemd. Never while players are on - a dead controller is bad, but
            // kicking people mid-race to fix it is not our call. After several minutes of not
            // being able to reach the game server at all, though, nobody is being served by
            // this process anyway, and the player count it remembers is just as old.
            if ((failedRecoveries >= 3 && lastOnlinePlayerCount == 0) || failedRecoveries >= 6)
            {
                Console.WriteLine("Reconnect keeps failing - restarting the controller.");
                await Console.Out.FlushAsync(CancellationToken.None);
                Environment.Exit(70);
            }
        }
        finally
        {
            reconnectInProgress = false;
            Volatile.Write(ref recoveryRunning, 0);
        }
    }

    private int recoveryRunning;
    private static readonly TimeSpan ReconnectBudget = TimeSpan.FromSeconds(30);

    // Step 2: on a busy server a dead stream shows up at the next map change. On an idle empty
    // one nothing changes at all, so the stall hides until somebody joins and hits it. Provoke a
    // callback instead - ChallengeRestart always yields BeginRace, and with nobody online
    // restarting the current map costs nothing.
    private async Task ProbeCallbacksAsync(CancellationToken cancellationToken)
    {
        long before = 0;
        var probed = false;

        // The restart itself runs under the game lock, the 10s wait for its callback must not:
        // that callback needs the lock to be handled.
        await RunUnderGameLockAsync("probing the callback stream", async token =>
        {
            if (!SessionActive || lastOnlinePlayerCount > 0 || reconnectInProgress || isAdvancingToNextMap || SwitchAlreadyClaimed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            if (now - client.LastCallbackAt < CallbackProbeIdle || now - lastCallbackProbeAt < CallbackProbeIdle)
            {
                return;
            }

            // isAdvancingToNextMap only covers our own call; the dedicated server keeps switching
            // for several seconds after it returns, and a ChallengeRestart in that window eats the
            // change. Stay well clear of any map that has just started.
            if (now - lastPolledMapChangedAt < CallbackProbeMapSettleTime)
            {
                return;
            }

            lastCallbackProbeAt = now;
            before = client.CallbacksReceived;

            Console.WriteLine("No callbacks for a while on an empty server - probing the stream.");
            await CallWithTransitionRetryAsync("ChallengeRestart", [], token);
            probed = true;
        });

        if (!probed)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

        if (client.CallbacksReceived == before)
        {
            Console.WriteLine("ERROR: the probe produced no callback - the stream is dead.");
            await TryRecoverCallbacksAsync(cancellationToken);
        }
    }

    private static readonly string statusFilePath = Path.Combine(AppContext.BaseDirectory, "WebStatus", "status.json");

    private async Task StalledMapWatchLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

            try
            {
                await RunUnderGameLockAsync("stalled-map watchdog", CheckStalledMapAsync);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: stalled-map watchdog failed - {ex.Message}");
            }

            try
            {
                await ProbeCallbacksAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: callback probe failed - {ex.Message}");
            }

            try
            {
                await RunUnderGameLockAsync("session keeper", KeepSessionRunningAsync);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: couldn't restart the session - {ex.Message}");
            }
        }
    }

    // Set by /stop, /end and the time limit running out: the session is meant to be off, so the
    // keeper below leaves it off.
    private bool sessionStoppedByAdmin;

    // With AutoStart the session is supposed to run whenever the server does. Anything that ends
    // it by accident - a failed start at boot, a preset switch whose first map could not be
    // fetched - used to leave the server parked on its current map until someone typed /start,
    // with the stalled-map watchdog switched off too (it only runs during a session).
    private async Task KeepSessionRunningAsync(CancellationToken cancellationToken)
    {
        if (!config.AutoStart || SessionActive || sessionStoppedByAdmin || reconnectInProgress || !client.IsConnected)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < keeperRetryAfter)
        {
            return;
        }

        Console.WriteLine("Session keeper: AutoStart is on but no session is running - starting one.");

        try
        {
            await StartSessionAsync(cancellationToken);
            keeperFailures = 0;
        }
        catch
        {
            // TMX down: every attempt holds up the game for its download - back off up to 5 min
            keeperFailures++;
            keeperRetryAfter = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(300, 30 * keeperFailures));
            throw;
        }
    }

    private int keeperFailures;
    private DateTimeOffset keeperRetryAfter;

    private int statusTicks;

    private async Task StatusWriteLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await WriteStatusAsync(writeTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // statusStage names the step that used up the budget: the RPC polls no longer
                // swallow the cancellation, so a slow game server is no longer reported as
                // "stuck on writing status.json to disk" (~370 times on the old host).
                Console.WriteLine($"Warning: status.json write timed out (stuck on '{statusStage}'), retrying next tick.");
                Diagnostics.Dump($"status.json write timed out on '{statusStage}'", recentLines: 25);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to write status.json - {ex.Message}");
            }

            try
            {
                await CheckCallbackHealthAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // this loop is what keeps status.json fresh - it must never die on its own
                Console.WriteLine($"Warning: callback health check failed - {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private async Task CheckCallbackHealthAsync(CancellationToken cancellationToken)
    {
        // A quiet baseline (one line every 5 minutes) so that when something stalls it can be
        // compared with what "normal" looked like: dedicated server CPU, thread pool, load.
        if (++statusTicks % 30 == 0)
        {
            Console.WriteLine($"[diag] health: {Diagnostics.Snapshot()} | {DescribeCallbackState()}");
        }

        if (CallbacksHealthy)
        {
            callbackLossReported = false;
        }
        else
        {
            if (!callbackLossReported)
            {
                callbackLossReported = true;
                Console.WriteLine(
                    "ERROR: the callback stream from the dedicated server is dead. The controller "
                    + "is still polling but no longer driving the game - no chat commands, widgets "
                    + "or map picks, and the playlist will just repeat. Restarting once empty.");

                // 42 of these in 15 days, most of them right after a map change - so log the
                // numbers behind the verdict and the last minutes of activity, to learn whether
                // the stream really died or the check fired while a map swap was in progress.
                Diagnostics.Dump(
                    "callback stream judged dead",
                    $"{DescribeCallbackState()} | advance stage='{advanceStage}' | status stage='{statusStage}'");
            }

            // Rebuild the connection rather than the process: this works with a full
            // server and costs seconds, so a stall no longer waits for the server to empty.
            await TryRecoverCallbacksAsync(cancellationToken);
        }
    }

    private IReadOnlyList<PlayerSummary> lastPolledPlayers = [];

    private async Task WriteStatusAsync(CancellationToken cancellationToken)
    {
        // A failed poll reports the last list that was actually seen, not "nobody". status.json's
        // PlayerCount is what the deploy script and the website treat as "the server is empty",
        // and a server that is merely slow to answer is not an empty one.
        var onlinePlayers = lastPolledPlayers;
        var playersStale = false;
        SetStatusStage("GetPlayerList");
        try
        {
            onlinePlayers = await client.GetPlayersAsync(cancellationToken);
            lastPolledPlayers = onlinePlayers;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // not connected (yet), or the call failed - keep the last known list for this tick
            playersStale = true;
        }

        var displayedTrackId = DisplayedMapTrackId;

        // Name and checkpoint count come straight from the dedicated server and are true whether
        // or not we can tie the map to a TMX id - only the link and the preview image need the id.
        // Gating all of it on the id blanked the entire status page for a map we couldn't identify.
        string? mapName = null;
        int? nbCheckpoints = null;
        SetStatusStage("GetCurrentChallengeInfo");
        try
        {
            var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
            mapName = TmFormatCodeRegex().Replace(info.Name, string.Empty);
            nbCheckpoints = info.NbCheckpoints;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // map info not available yet
        }

        // The poll is the independent witness: it sees the map change whether or not the
        // callback stream is still alive. CallbacksHealthy compares the two.
        if (mapName is not null && mapName != lastPolledMapName)
        {
            lastPolledMapName = mapName;
            lastPolledMapChangedAt = DateTimeOffset.UtcNow;

            // A new map clears the ranking, so start counting from zero again. Doing this from
            // the poll and not from BeginRace is the point - a witness that needs the callback
            // stream to stay correct is no witness at all.
            lastPolledRankedCount = 0;
            lastRacingProgressAt = null;
        }

        // Second witness: the server's own record of who has a time on this map. It only ever
        // grows within a map, and it grows precisely when a callback should have fired.
        SetStatusStage("GetCurrentRanking");
        try
        {
            var rankedCount = await client.GetCurrentRankingCountAsync(cancellationToken);

            if (rankedCount > lastPolledRankedCount)
            {
                lastPolledRankedCount = rankedCount;
                lastRacingProgressAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // ranking not available right now - just skip this witness for this tick
        }

        // ranked by live race progress: most checkpoints first, ties broken by who reached
        // their current checkpoint first (see playerCheckpointTimestamp's declaration)
        var racers = onlinePlayers
            .Select(p => new
            {
                Nickname = StripTmCodes(p.NickName),
                // Kept alongside the stripped name so the website can render the
                // colours people actually play under. Consumers that want plain
                // text keep reading Nickname and are unaffected.
                RawNickname = p.NickName,
                Checkpoint = playerCheckpointProgress.GetValueOrDefault(p.Login, 0),
                Since = playerCheckpointTimestamp.GetValueOrDefault(p.Login, DateTimeOffset.MaxValue),
            })
            .OrderByDescending(p => p.Checkpoint)
            .ThenBy(p => p.Since)
            .Select((p, i) => new { p.Nickname, p.RawNickname, p.Checkpoint, IsLeader = i == 0 && p.Checkpoint > 0 })
            .ToList();

        lastOnlinePlayerCount = onlinePlayers.Count;

        var status = new
        {
            config.ServerName,
            SessionActive,
            PlayerCount = onlinePlayers.Count,
            // true when this tick could not ask the game server and PlayerCount is the last known one
            PlayerCountStale = playersStale,
            PresetDisplayName = config.LastPreset?.DisplayName,
            CurrentMapName = mapName,
            CurrentMapCheckpoints = nbCheckpoints,
            CurrentMapTrackId = displayedTrackId,
            CurrentMapUrl = displayedTrackId is { } id ? $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}" : null,
            CurrentMapImageUrl = displayedTrackId is { } imgId ? $"https://{tmxRules.GetSiteUrl()}/trackshow/{imgId}/image/1" : null,
            Players = racers,
            // 25 rather than 5: the website scrolls this list, and the in-game
            // /top10 command takes its own slice, so nothing in chat gets longer.
            Top = leaderboard.GetTop(25),
            ControllerHealthy = CallbacksHealthy,
            CallbacksReceived = client.CallbacksReceived,
            LastCallbackAt = client.LastCallbackAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        SetStatusStage("writing status.json to disk");
        Directory.CreateDirectory(Path.GetDirectoryName(statusFilePath)!);
        // Atomic: the website and the deploy script poll this file every few seconds and used to
        // catch it empty or half-written now and then.
        var json = JsonSerializer.Serialize(status);
        await AtomicFile.WriteAllTextAsync(statusFilePath, json);
        SetStatusStage("idle");
    }

    private async Task StartAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can start a session.", cancellationToken);
            return;
        }

        sessionStoppedByAdmin = false;
        await StartSessionAsync(cancellationToken);
    }

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        if (SessionActive)
        {
            return;
        }

        sessionStopwatch = new();

        try
        {
            await SetTimeLimitAsync(cancellationToken);

            // ownsClaim: a pending finish switch must not swallow the session's first map (after
            // /preset the old preset's map kept running) - the finish switch sees the newer claim
            // and stands down
            if (!await NextRandomMapAsync(goalReached: false, cancellationToken, ownsClaim: true))
            {
                Console.WriteLine("Session started - a map switch was already on its way and becomes its first map.");
            }

            // only now: when TMX is down the session keeper retries, and players got "Let's begin!"
            // every half minute for a session that never began
            await SendMessageAsync([string.Empty, "$0F0Let's begin!"], cancellationToken);

            if (config.TimeLimit.TotalMilliseconds > 0)
            {
                await SendMessageAsync($"Time limit set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }
        }
        catch
        {
            // Only a session whose first map is on its way counts as started. Leaving it marked
            // active after a failure is what made the AutoStart retries no-ops and stranded the
            // server on the warmup map; this way the next attempt really tries again.
            sessionStopwatch = null;
            throw;
        }
    }

    private async Task StopAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can stop the session.", cancellationToken);
            return;
        }

        if (!SessionActive)
        {
            await SendMessageAsync(login, "$F00No active session to stop.", cancellationToken);
            return;
        }

        // the session keeper must not start it again behind the admin's back
        sessionStoppedByAdmin = true;
        await StopSessionAsync(cancellationToken);

        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"$FF0Player {GetNicknameOrLogin(login)} has stopped the session!", cancellationToken);
        }
        else
        {
            await SendMessageAsync("$F00Session stopped!", cancellationToken);
        }
    }

    private async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        sessionStopwatch?.Stop();
        sessionStopwatch = null;
        sessionStopwatchMillisecondOffset = 0;
        currentMap = null;
        currentMapTrackId = null;
        randomEnqueuedMapFileName = null;

        await client.CallAsync("SetTimeAttackLimit", [0], cancellationToken);
        await client.CallAsync("ChallengeRestart", [], cancellationToken);
    }

    private async Task SetTimeLimitAsync(CancellationToken cancellationToken)
    {
        await client.CallAsync("SetTimeAttackLimit", [config.TimeLimit.TotalMilliseconds], cancellationToken);
    }

    private async Task SetCalculatedTimeLimitAsync(CancellationToken cancellationToken)
    {
        if (config.TimeLimit.TotalMilliseconds <= 0 || sessionStopwatch is null)
        {
            return;
        }

        var elapsedMilliseconds = sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset;

        sessionStopwatchMillisecondOffset += 1500;

        await client.CallAsync("SetTimeAttackLimit", [config.TimeLimit.TotalMilliseconds - (int)elapsedMilliseconds], cancellationToken);
    }

    // A map nobody can load - custom blocks the clients do not have, a corrupt file -
    // looks exactly like this from the server's side: players connect, get dropped
    // straight back out, so nobody is ever counted online and no checkpoint is ever
    // reached. With AutoSkipMode=Finished nothing advances the rotation, so the server
    // sits on that map indefinitely. TMX cannot help us here either: UnlimiterVersion is
    // metadata the uploader declares, so a map built with custom blocks and uploaded
    // without the flag passes inunlimiter=0 cleanly.
    //
    // Both conditions are required. "No checkpoints" alone would fire on a hard map
    // someone is still struggling with; "nobody online" alone would fire on a quiet
    // night and churn through the pool for no reason.
    private static readonly TimeSpan StalledMapTimeout = TimeSpan.FromMinutes(15);

    // The last map the watchdog already skipped, so a map that fails to advance
    // cannot be reported over and over.
    private int? lastStalledMapTrackId;

    private async Task CheckStalledMapAsync(CancellationToken cancellationToken)
    {
        if (!SessionActive || isAdvancingToNextMap || SwitchAlreadyClaimed)
        {
            return;
        }

        if (currentMapStartedAt is not { } startedAt || DateTimeOffset.UtcNow - startedAt < StalledMapTimeout)
        {
            return;
        }

        if (lastOnlinePlayerCount > 0)
        {
            return;
        }

        foreach (var reached in playerCheckpointProgress.Values)
        {
            if (reached > 0)
            {
                return;
            }
        }

        var trackId = DisplayedMapTrackId;

        if (trackId is not null && trackId == lastStalledMapTrackId)
        {
            return;
        }

        // Clear first: the advance below takes a moment, and the watchdog must not fire
        // a second time for the same map while it is in flight.
        currentMapStartedAt = null;
        lastStalledMapTrackId = trackId;

        Console.WriteLine($"Stalled-map watchdog: no players and no checkpoints for {StalledMapTimeout.TotalMinutes:F0} minutes, skipping map {trackId}.");
        await SendMessageAsync($"$F80No players and no checkpoints for {StalledMapTimeout.TotalMinutes:F0} minutes - skipping this map.", cancellationToken);

        // Deliberately no Discord post: this fires on an empty server, potentially
        // several times a night, and nobody needs a notification for a map that had
        // no audience. The console line above is the record. Player-initiated /imp
        // reports still notify, because somebody is actually asking for attention.
        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    private async Task SkipAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            await SendMessageAsync(login, "$F00No session is running - nothing to skip.", cancellationToken);
            return;
        }

        if (isAdvancingToNextMap || SwitchAlreadyClaimed)
        {
            await SendMessageAsync(login, "$FF0The next map is already on its way.", cancellationToken);
            return;
        }

        var multiplePlayers = await client.IsMultiplePlayersAsync(cancellationToken);

        if (!multiplePlayers)
        {
            await SendMessageAsync("Skipping the current challenge...", cancellationToken);
        }

        // With 2+ players this opens a vote, and NextRandomMapAsync announces that itself.
        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    // args[0] == "-N" (N >= 1) refers to the Nth map before the current one (e.g. "/imp -1" for
    // the map that just ended, "/imp -3" for three maps ago - see /history for what's available).
    // IsCurrent is false for any "-N" - callers use it to skip current-map-only side effects like
    // advancing to a new map or reading live checkpoint info for a map that isn't loaded anymore
    // Anything else as the argument is a mistake, not "the current map": "/imp 12345" (a TMX id)
    // used to silently report - and skip - whatever was running.
    private (int? TrackId, bool IsCurrent, bool Invalid) ResolveMapReference(string[] args)
    {
        if (args.Length == 0)
        {
            return (DisplayedMapTrackId, true, false);
        }

        if (int.TryParse(args[0], out var offset) && offset < 0)
        {
            var historyIndex = -offset - 1;
            return (historyIndex < mapHistory.Count ? mapHistory[historyIndex] : null, false, false);
        }

        return (null, false, true);
    }

    private Task SendMapReferenceUsageAsync(string login, string command, CancellationToken cancellationToken) =>
        SendMessageAsync(login, $"$F00Use $FF0/{command}$F00 for the current map, or $FF0/{command} -1$F00 ... $FF0-{MaxMapHistory}$F00 for a past one (see $FF0/history$F00).", cancellationToken);

    // /imp and /hard post to Discord and /votemap asks TMX, so each player gets one per cooldown.
    // Without it a single player could flood the review channel (and hit Discord's rate limit).
    private static readonly TimeSpan ReportCooldown = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, DateTimeOffset> lastReportAt = new(StringComparer.OrdinalIgnoreCase);

    // the same map reported again (by anyone) during this run is not posted again
    private readonly HashSet<(string Kind, int TrackId)> reportedMaps = [];

    private async Task<bool> CheckCooldownAsync(string login, string kind, CancellationToken cancellationToken)
    {
        var key = $"{kind}:{login}";
        var now = DateTimeOffset.UtcNow;

        if (lastReportAt.TryGetValue(key, out var last) && now - last < ReportCooldown)
        {
            var wait = ReportCooldown - (now - last);
            await SendMessageAsync(login, $"$F00Please wait {Math.Ceiling(wait.TotalSeconds):0}s before using /{kind} again.", cancellationToken);
            return false;
        }

        if (lastReportAt.Count > 1000)
        {
            lastReportAt.Clear();
        }

        lastReportAt[key] = now;
        return true;
    }

    // The path a fetched TMX map gets inserted under. The TMX id goes into the name on purpose:
    // the dedicated server rotates its own selection whenever a map ends without us advancing it
    // (a time limit running out with nobody finishing, say), so "the map we last picked" and "the
    // map actually running" drift apart, and the file name is the only thing that ties a running
    // challenge back to the id it came from. Observed live on 2026-09-11: the controller had
    // /map, the TMX link and the status page preview all pointing at a map from 15 minutes back.
    // The map name is capped: the game server refuses to write a path that ends up too long
    // ("couldn't write file"), and the map is found by its _tmx<id>_ marker anyway.
    private static string InsertedChallengePath(InMemoryFile map)
    {
        const string extension = ".Challenge.Gbx";
        const int maxNameLength = 40;
        var name = map.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? map.FileName[..^extension.Length]
            : map.FileName;

        if (name.Length > maxNameLength)
        {
            name = name[..(char.IsHighSurrogate(name[maxNameLength - 1]) ? maxNameLength - 1 : maxNameLength)].TrimEnd();
        }

        return Path.Combine("_RandomizerAny", $"{DateTimeOffset.UtcNow.Ticks}_tmx{map.TrackId}_{name}{extension}");
    }

    // The server reports challenge paths in its own shape (directory separators differ, and it may
    // hand back more or less of the path than we passed in), so match on the bare file name.
    private static string ChallengeFileKey(string fileName)
    {
        var normalised = fileName.Replace('\\', '/');
        var lastSlash = normalised.LastIndexOf('/');
        return lastSlash >= 0 ? normalised[(lastSlash + 1)..] : normalised;
    }

    private void RememberChallengeTrackId(string fileName, int trackId)
    {
        trackIdByChallengeFile[ChallengeFileKey(fileName)] = trackId;
    }

    // One line per map change, because the controller logged which map it PICKED and never which
    // map actually LOADED - and those two turned out to come apart routinely, which is exactly the
    // thing players were reporting and the thing the log could not answer.
    private void LogMapTransition(ChallengeSummary info)
    {
        var loaded = currentMapTrackId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "unidentified";
        var name = TmFormatCodeRegex().Replace(info.Name, string.Empty);

        if (currentMapTrackId is not null && currentMapTrackId == pendingMapTrackId)
        {
            Console.WriteLine($"Map loaded: {loaded} ({name}).");
            return;
        }

        // The interesting case: the server put on something other than what we queued, which means
        // it advanced through its own selection instead of taking our insert.
        var expected = pendingMapTrackId is { } pending ? pending.ToString(CultureInfo.InvariantCulture) : "nothing";
        Console.WriteLine($"Map loaded: {loaded} ({name}) - we had queued {expected}. The server advanced through its own playlist.");
    }

    // Takes the map out of the server's playlist; the .Gbx stays on disk and
    // trackIdByChallengeFile keeps the id, so history and /map are unaffected. Best effort by
    // design: TMF refuses to remove the last remaining challenge, and that refusal is fine - it
    // just means there is nothing to prune yet.
    private async Task RemovePlayedChallengeAsync(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            await client.CallAsync("RemoveChallenge", [fileName], cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note: couldn't drop the previous map from the playlist - {ex.Message}");
        }
    }

    // null means "this is not a map we can identify" - a leftover from an older build still in the
    // selection, or something that never came from TMX. Saying so beats linking the wrong map.
    private int? ResolveRunningTrackId(string? fileName)
    {
        if (fileName is not null)
        {
            var key = ChallengeFileKey(fileName);

            if (trackIdByChallengeFile.TryGetValue(key, out var known))
            {
                return known;
            }

            if (ChallengeTrackIdRegex().Match(key) is { Success: true } match
                && int.TryParse(match.Groups[1].Value, out var trackId))
            {
                return trackId;
            }
        }

        // Says which of the two it was - no file name in the server's response at all, or a name
        // that carries no id - so an unexpected "unknown map" is diagnosable from the log instead
        // of just being a blank in chat and on the status page.
        Console.WriteLine($"Note: couldn't tie the running challenge to a TMX id (FileName: {fileName ?? "<not reported>"}).");
        return null;
    }

    private async Task ImpossibleAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var (trackId, isCurrent, invalid) = ResolveMapReference(args);

        if (invalid)
        {
            await SendMapReferenceUsageAsync(login, "imp", cancellationToken);
            return;
        }

        if (trackId is not { } id)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        if (!await CheckCooldownAsync(login, "imp", cancellationToken))
        {
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}";

        tmxRules.ExcludeForSession(id);

        var skipSuffix = isCurrent ? ", skipping" : string.Empty;
        await SendMessageAsync($"$F00Map {id} reported impossible by {GetNicknameOrLogin(login)} - won't be shown again until reviewed{skipSuffix}.", cancellationToken);

        if (reportedMaps.Add(("imp", id)))
        {
            await discordNotifier.PostAsync($"{DiscordServerTag}**{DiscordNotifier.FormatPlayerName(GetPlainNickname(login))}** reported map **{id}** as impossible: {tmxUrl}", cancellationToken);
        }

        // only advance the session if we're reporting the map that's actually loaded right now -
        // "/imp -1" reports a map that's already gone, the current one has nothing to do with it
        if (isCurrent)
        {
            await NextRandomMapAsync(goalReached: false, cancellationToken);
        }
    }

    // This server's own skip list: for a map that can't be played here at all (built with blocks the
    // game doesn't have, so it fails to load). Unlike /imp it is permanent, stays on this server, and
    // is never sent to Discord or the shared sheet. Admin-only when admins are configured, because
    // it removes a map from the pool for good.
    private async Task BlockMapAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can add a map to the skip list.", cancellationToken);
            return;
        }

        var (trackId, isCurrent, invalid) = ResolveMapReference(args);

        if (invalid)
        {
            await SendMapReferenceUsageAsync(login, "blockmap", cancellationToken);
            return;
        }

        if (trackId is not { } id)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var added = await skippedMaps.AddAsync(id, cancellationToken);
        tmxRules.ExcludeForSession(id);

        Console.WriteLine($"Skip list: {GetPlainNickname(login)} {(added ? "added" : "re-confirmed")} map {id} ({skippedMaps.Count} on the list).");

        var skipSuffix = isCurrent ? ", skipping it now" : string.Empty;
        await SendMessageAsync(
            added
                ? $"$FF0Map {id} added to this server's skip list by {GetNicknameOrLogin(login)} - it won't be picked again{skipSuffix}."
                : $"$FF0Map {id} is already on this server's skip list{skipSuffix}.",
            cancellationToken);

        if (isCurrent)
        {
            await NextRandomMapAsync(goalReached: false, cancellationToken);
        }
    }

    // both servers can share one Discord webhook, so say which one a report came from; the original
    // TMNF server keeps its messages exactly as they were
    private string DiscordServerTag => config.EffectiveTmxGame == GameTitle.TMNF ? string.Empty : $"[{config.EffectiveTmxGame}] ";

    private async Task HardAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var (trackId, _, invalid) = ResolveMapReference(args);

        if (invalid)
        {
            await SendMapReferenceUsageAsync(login, "hard", cancellationToken);
            return;
        }

        if (trackId is not { } id)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        if (!await CheckCooldownAsync(login, "hard", cancellationToken))
        {
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}";

        await SendMessageAsync($"$FF0Map {id} flagged as hard by {GetNicknameOrLogin(login)} for review.", cancellationToken);

        if (reportedMaps.Add(("hard", id)))
        {
            await discordNotifier.PostHardAsync($"{DiscordServerTag}**{DiscordNotifier.FormatPlayerName(GetPlainNickname(login))}** flagged map **{id}** as hard: {tmxUrl}", cancellationToken);
        }
    }

    private async Task TopAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var top = leaderboard.GetTop(5);

        if (top.Count == 0)
        {
            await SendMessageAsync(login, "$F00No finishes recorded yet.", cancellationToken);
            return;
        }

        var lines = top.Select((entry, i) => $"{i + 1}. $FF0{entry.LastNickname}$FFF - {entry.Finishes} finish(es)");
        await SendMessageAsync(login, ["Top finishers on this server:", .. lines], cancellationToken);
    }

    private async Task RankAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var rank = leaderboard.GetRank(login);

        if (rank is null)
        {
            await SendMessageAsync(login, "$F00You haven't finished a map on this server yet.", cancellationToken);
            return;
        }

        await SendMessageAsync(login, $"$0F0You are rank $FF0#{rank.Value.Position}$0F0 with $FF0{rank.Value.Finishes}$0F0 finish(es).", cancellationToken);
    }

    private async Task MapAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var (trackId, isCurrent, invalid) = ResolveMapReference(args);

        if (invalid)
        {
            await SendMapReferenceUsageAsync(login, "map", cancellationToken);
            return;
        }

        if (trackId is not { } id)
        {
            // "couldn't identify" rather than "nothing loaded": with the server free to rotate its
            // own selection, a map can be running that this controller never picked and cannot
            // trace back to a TMX id (see ResolveRunningTrackId)
            var noMapMessage = isCurrent
                ? "$F00Couldn't identify the map that's currently running."
                : "$F00No previous map recorded yet.";
            await SendMessageAsync(login, noMapMessage, cancellationToken);
            return;
        }

        // "http://" not "https://" - TMF's chat "$l[...]" link syntax prepends its own "http://"
        // on top of anything that isn't already that exact scheme, breaking the link (see /source)
        var tmxUrl = $"http://{tmxRules.GetSiteUrl()}/trackshow/{id}";
        var cpSuffix = string.Empty;

        // live checkpoint/lap info is only meaningful for the map that's actually loaded right
        // now - GetCurrentChallengeInfo() always reflects the active challenge, which for "-1"
        // would just be the WRONG map's info
        if (isCurrent)
        {
            try
            {
                var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
                if (info.NbCheckpoints is { } cpCount)
                {
                    cpSuffix = $" $FFF({cpCount} CPs)";
                }
                if (info.LapRace)
                {
                    cpSuffix += $" $F80[Multilap, {info.NbLaps} laps - $FFF/rounds$F80 for a valid replay]";
                }
            }
            catch (Exception)
            {
                // checkpoint count not available, just skip the suffix
            }
        }

        var label = isCurrent ? "Current map" : "Last map";
        await SendMessageAsync(login, $"$FF0{label}:{cpSuffix} $FFF$l[{tmxUrl}]{tmxUrl}$l", cancellationToken);
    }

    // shows the last few maps so players know what "-2", "-3", ... refer to for /map, /imp, /hard
    private const int HistoryDisplayCount = 5;

    private async Task HistoryAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (mapHistory.Count == 0)
        {
            await SendMessageAsync(login, "$F00No map history yet.", cancellationToken);
            return;
        }

        var lines = new List<string> { "$0BFRecent maps ($FF0/map -N$0BF, $FF0/imp -N$0BF, $FF0/hard -N$0BF):" };
        var shownTrackIds = new List<int>();
        var shownNames = new List<string>();

        // All names at once with one short budget: one after the other, each with TMX's full 40s
        // allowance, five lookups could hold the callback stream past its 60s cutoff.
        var trackIds = mapHistory.Take(HistoryDisplayCount).ToList();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TrackMetaBudget);

        var names = await Task.WhenAll(trackIds.Select(async trackId =>
        {
            if (trackNameCache.TryGetValue(trackId, out var cached))
            {
                return cached;
            }

            try
            {
                var name = await tmxRules.GetTrackNameAsync(trackId, budget.Token);

                if (trackNameCache.Count > 500)
                {
                    trackNameCache.Clear();
                }

                trackNameCache[trackId] = name;
                return name;
            }
            catch (Exception)
            {
                return $"Track {trackId}";
            }
        }));

        for (var i = 0; i < trackIds.Count; i++)
        {
            lines.Add($"$FF0-{i + 1}$FFF: {names[i]}");
            shownTrackIds.Add(trackIds[i]);
            shownNames.Add(names[i]);
        }

        await SendMessageAsync(login, lines, cancellationToken);

        lastShownHistoryTrackIds = shownTrackIds;
        await client.SendManialinkPageToLoginAsync(login, BuildHistoryManialink(shownNames), cancellationToken: cancellationToken);
    }

    // switches the CURRENT map to Rounds mode so a multilap track can be finished properly - plain
    // TimeAttack lets a player cross the finish line once and be done, ignoring the map's real lap
    // count, which produces a replay TMX won't accept for a multilap track. Reverts to TimeAttack
    // automatically the moment the RMC moves on to its next map - see NextRandomMapAsync's
    // SetGameMode(1) call, which already runs unconditionally on every map fetch
    private async Task RoundsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            await SendMessageAsync(login, "$F00No session is active.", cancellationToken);
            return;
        }

        // /rounds restarts the map for everyone, so it must not land in the middle of a map switch
        // (a ChallengeRestart there cancels the switch and reloads the old map) and must not be
        // repeatable to reset everybody's runs. Right at the start of a map it is fine - that is
        // when players switch a multilap map to Rounds anyway.
        if (isAdvancingToNextMap || SwitchAlreadyClaimed)
        {
            await SendMessageAsync(login, "$F00The next map is already on its way.", cancellationToken);
            return;
        }

        if (roundsModeActive)
        {
            await SendMessageAsync(login, "$FF0This map is already in Rounds mode.", cancellationToken);
            return;
        }

        ChallengeSummary info;
        try
        {
            info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
        }
        catch (Exception)
        {
            await SendMessageAsync(login, "$F00Could not read the current map's info - try again in a moment.", cancellationToken);
            return;
        }

        if (!info.LapRace)
        {
            await SendMessageAsync(login, "$F00This map isn't a multilap track - no need for Rounds mode.", cancellationToken);
            return;
        }

        await client.CallAsync("SetGameMode", [0], cancellationToken); // 0 = Rounds
        await client.CallAsync("SetRoundForcedLaps", [0], cancellationToken); // 0 = use the map's own lap count
        roundsModeActive = true;
        await CallWithTransitionRetryAsync("ChallengeRestart", [], cancellationToken);

        var lapsSuffix = info.NbLaps > 0 ? $" ({info.NbLaps} laps)" : string.Empty;
        await SendMessageAsync($"$0F0{GetNicknameOrLogin(login)} switched this multilap map to Rounds mode{lapsSuffix} for a valid replay. Back to TimeAttack once this map ends.", cancellationToken);
    }

    private async Task InfoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, [
            "$0BF--- 100% TMX Project ---",
            "$FF0/skip$FFF - skip the current map (with several players online this starts a vote - press $0F0F5$FFF to agree)",
            "$FF0/map$FFF (or $FF0/map -1$FFF, $FF0-2$FFF, ... for a past map) - show the map's TMX link",
            "$FF0/history$FFF - list recent maps and what $FF0-1$FFF, $FF0-2$FFF, ... refer to",
            "$FF0/rounds$FFF - switch a multilap map to Rounds mode for a valid replay (once per map)",
            "$FF0/imp$FFF (or $FF0/imp -1$FFF, $FF0-2$FFF, ... for a past map) - report a map as impossible",
            "$FF0/hard$FFF (or $FF0/hard -1$FFF, $FF0-2$FFF, ... for a past map) - flag a map as hard for review",
            "$FF0/top$FFF - show the top finishers on this server",
            "$FF0/rank$FFF - show your own rank and finish count",
            "$FF0/votepreset <name>$FFF - propose switching preset, others confirm with $0F0/yes$FFF ($FF0/presets$FFF for names)",
            "$FF0/votemap <TMX id>$FFF - propose loading a specific TMX map by id, shows name/author/difficulty/AT/tags, others confirm with $0F0/yes$FFF (doesn't count for the leaderboard)",
            "$FF0/commands$FFF - list every raw command name",
            "$FF0/source$FFF - get the source code link for this modified server (AGPLv3)",
            "Admin-only: $FF0/start$FFF, $FF0/stop$FFF, $FF0/preset$FFF, $FF0/loadmap <TMX id>$FFF, $FF0/blockmap$FFF (server-only skip list), $FF0/timelimit$FFF",
        ], cancellationToken);
    }

    private async Task TestHudAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        const string testXml = """
            <manialink version="1">
                <label posn="0 0 5" halign="center" valign="center" textsize="3" textcolor="FFFF" text="TEST HUD WORKS"/>
            </manialink>
            """;

        try
        {
            await client.SendManialinkPageToLoginAsync(login, testXml, cancellationToken: cancellationToken);
            await SendMessageAsync(login, "$0F0Test HUD sent - do you see big text in the middle of your screen?", cancellationToken);
        }
        catch (Exception ex)
        {
            await SendMessageAsync(login, $"$F00Failed to send: {ex.Message}", cancellationToken);
        }
    }

    private async Task CommandsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        // Built from our own handler table: it used to ask the game server via GetChatCommandList,
        // which this TMF build does not have ("Method not defined"), so /commands always failed.
        var lines = commandHandlers.Keys
            .Order(StringComparer.Ordinal)
            .Select(cmd => $"$FF0/{cmd}$FFF")
            .Chunk(9)
            .Select((chunk, i) => (i == 0 ? "Commands: " : string.Empty) + string.Join(", ", chunk))
            .ToList();

        await SendMessageAsync(login, lines, cancellationToken);
    }

    private async Task SourceAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, $"$FF0This server runs a modified, AGPLv3-licensed version of Randomizer Anywhere. Source: $FFF$l[{SourceRepoUrl}]{SourceRepoUrl}$l", cancellationToken);
    }

    private async Task TimeLimitAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            if (config.TimeLimit.TotalMilliseconds <= 0)
            {
                await SendMessageAsync(login, "Time limit is currently disabled. No time pressure!", cancellationToken);
            }
            else
            {
                await SendMessageAsync(login, $"Time limit is currently set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }

            return;
        }

        var arg = args[0];

        if (arg.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await SendMessageAsync(login, "Usage: $FF0/timelimit <seconds>", cancellationToken);
            return;
        }

        if (SessionActive)
        {
            await SendMessageAsync(login, "$F00Cannot change time limit while a session is active", cancellationToken);
            return;
        }

        if (!int.TryParse(arg, out var seconds) || seconds < 0)
        {
            await SendMessageAsync(login, $"$F00Invalid time limit value: {arg}. Please provide a non-negative integer.", cancellationToken);
            return;
        }

        config.TimeLimit = new TimeInt32(seconds * 1000);

        if (config.TimeLimit.TotalMilliseconds == 0)
        {
            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has disabled the time limit.", cancellationToken);
            }
            else
            {
                await SendMessageAsync("Time limit disabled.", cancellationToken);
            }
        }
        else
        {
            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has set the time limit to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }
            else
            {
                await SendMessageAsync($"Time limit set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }
        }
    }

    private async Task PresetAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can change the preset.", cancellationToken);
            return;
        }

        if (args.Length == 0)
        {
            var currentPresetMessage = string.IsNullOrWhiteSpace(config.LastPreset?.DisplayName)
                ? "No preset was yet applied."
                : $"Last preset: $FF0{config.LastPreset.DisplayName}";

            await SendMessageAsync(login, [currentPresetMessage, "Usage: $FF0/preset <name>"], cancellationToken);
            return;
        }

        // Check the preset BEFORE touching the session. The session used to be reset first, so a
        // typo answered "not found" with the session already gone - no auto-advance, no watchdog.
        var (preset, error) = LoadPresetForThisGame(args[0]);

        if (preset is null)
        {
            await SendMessageAsync(login, $"$F00{error}", cancellationToken);
            return;
        }

        var wasActive = SessionActive;
        if (wasActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var displayName = ApplyPreset(args[0], preset);

        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has applied the $FF0{displayName}$FFF preset.", cancellationToken);
        }
        else
        {
            await SendMessageAsync($"$0F0Preset $FF0{displayName}$0F0 applied.", cancellationToken);
        }

        if (wasActive)
        {
            await StartSessionAsync(cancellationToken);
        }
    }

    // resets the local session bookkeeping a preset switch needs to clear, without the
    // ChallengeRestart RPC call StopSessionAsync makes - see FinalizeVoteAsync for why that
    // call must not race the NextChallenge call the caller is about to trigger via StartSessionAsync
    private void ResetSessionStateForPresetSwitch()
    {
        sessionStopwatch?.Stop();
        sessionStopwatch = null;
        sessionStopwatchMillisecondOffset = 0;
        currentMap = null;
        currentMapTrackId = null;
        randomEnqueuedMapFileName = null;
    }

    // a TMNF preset must not show up (or be applied) on the TMUF server and vice versa
    private bool IsPresetForThisGame(string presetName)
    {
        if (!IsValidPresetName(presetName))
        {
            return false;
        }

        var preset = TomlLoader.LoadPresetConfig(Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml"));
        return preset is null || preset.SupportsGame(config.Game);
    }

    // A preset name is a file name in Presets/, nothing more - no folders, no "..".
    private static bool IsValidPresetName(string presetName) =>
        presetName.Length is > 0 and <= 64 && presetName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private (PresetConfig? Preset, string? Error) LoadPresetForThisGame(string presetName)
    {
        if (!IsValidPresetName(presetName))
        {
            return (null, $"Preset '{presetName}' not found.");
        }

        var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml");

        if (!File.Exists(presetPath))
        {
            return (null, $"Preset '{presetName}' not found.");
        }

        var presetConfig = TomlLoader.LoadPresetConfig(presetPath);

        if (presetConfig is null)
        {
            return (null, $"Failed to load preset '{presetName}'.");
        }

        if (!presetConfig.SupportsGame(config.Game))
        {
            return (null, $"Preset '{presetName}' is not for this game.");
        }

        if (!presetConfig.IsValid(out var invalidReason))
        {
            return (null, $"Preset '{presetName}' is broken: {invalidReason}");
        }

        return (presetConfig, null);
    }

    // returns the name to show players
    private string ApplyPreset(string presetName, PresetConfig presetConfig)
    {
        presetConfig.Apply(config);
        config.LastPreset = presetConfig;

        return string.IsNullOrWhiteSpace(presetConfig.DisplayName) ? presetName : presetConfig.DisplayName;
    }

    private async Task VotePresetAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            await SendMessageAsync(login, "Usage: $FF0/votepreset <name>$FFF, then others type $FF0/yes$FFF to support.", cancellationToken);
            return;
        }

        if (votePresetName is not null || voteMapTrackId is not null)
        {
            await SendMessageAsync(login, $"$F00A vote for {DescribeActiveVote()} is already in progress.", cancellationToken);
            return;
        }

        var presetName = args[0];

        if (LoadPresetForThisGame(presetName) is { Preset: null, Error: var presetError })
        {
            await SendMessageAsync(login, $"$F00{presetError}", cancellationToken);
            return;
        }

        votePresetName = presetName;
        voteYesLogins = [login];

        try
        {
            await SendMessageAsync($"$FF0{GetNicknameOrLogin(login)} started a vote to switch to preset '{presetName}'. Type $0F0/yes$FF0 to support ({PresetVoteWindowMs / 1000}s window).", cancellationToken);
            await client.SendManialinkPageAsync(BuildVotePopupManialink(presetName), cancellationToken: cancellationToken);

            if (await HasVoteMajorityAsync(cancellationToken))
            {
                await FinalizeVoteAsync(cancellationToken);
                return;
            }
        }
        catch
        {
            // see VoteMapAsync - without this a failed announcement left the vote open for good
            if (votePresetName == presetName)
            {
                votePresetName = null;
                voteYesLogins = null;
            }

            throw;
        }

        var voteId = ++voteCounter;
        StartVoteTimer(voteId, $"preset '{presetName}'", FinalizeVoteAsync);
    }

    // Identifies one vote, so a timer can never end a later vote that happens to be for the same
    // preset or map.
    private int voteCounter;
    private int activeVoteId;

    // Ends the vote after the window. Runs on the thread pool, so it takes the game lock, and it
    // ALWAYS clears the vote - it used to catch only cancellation, so a timeout or "Client is not
    // connected" while counting (e.g. during a reconnect) left the vote open for good and every
    // later vote answered "already in progress" until a restart.
    private void StartVoteTimer(int voteId, string description, Func<CancellationToken, Task> finalizeAsync)
    {
        activeVoteId = voteId;

        RunInBackground($"ending the vote for {description}", async () =>
        {
            await Task.Delay(PresetVoteWindowMs, appToken);

            var ran = await RunUnderGameLockAsync($"ending the vote for {description}", async token =>
            {
                if (activeVoteId != voteId || (votePresetName is null && voteMapTrackId is null))
                {
                    return; // already finalized, or a newer vote is running
                }

                var passed = false;
                try
                {
                    passed = await HasVoteMajorityAsync(token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: couldn't count the votes for {description} - {ex.Message}");
                }

                if (passed)
                {
                    await finalizeAsync(token);
                    return;
                }

                ClearVote();

                try
                {
                    await SendMessageAsync($"$F00Vote for {description} failed - not enough support.", token);
                    await HideManialinkAsync(VotePopupManialinkId, token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: couldn't announce the end of the vote for {description} - {ex.Message}");
                }
            });

            // never got the lock: end the vote anyway, or every later vote is "already running"
            if (!ran && activeVoteId == voteId)
            {
                ClearVote();
            }
        });
    }

    private void ClearVote()
    {
        votePresetName = null;
        voteMapTrackId = null;
        voteYesLogins = null;
        activeVoteId = 0;
    }

    private async Task YesAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (voteYesLogins is null || (votePresetName is null && voteMapTrackId is null))
        {
            // The skip vote (/skip with several players) is TrackMania's own vote, which this
            // command cannot cast - say how to vote on it instead of "no vote is active".
            if (await IsGameVoteRunningAsync(cancellationToken))
            {
                await SendMessageAsync(login, "$FF0That vote is run by TrackMania itself - press $0F0F5$FF0 to agree.", cancellationToken);
                return;
            }

            await SendMessageAsync(login, "$F00No vote is currently active. Start one with $FF0/votepreset <name>$F00 or $FF0/votemap <id>$F00.", cancellationToken);
            return;
        }

        voteYesLogins.Add(login);

        if (await HasVoteMajorityAsync(cancellationToken))
        {
            if (votePresetName is not null)
            {
                await FinalizeVoteAsync(cancellationToken);
            }
            else
            {
                await FinalizeMapVoteAsync(cancellationToken);
            }
        }
    }

    private async Task NoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (voteYesLogins is null || (votePresetName is null && voteMapTrackId is null))
        {
            await SendMessageAsync(login, "$F00No vote is currently active.", cancellationToken);
            return;
        }

        voteYesLogins.Remove(login);
        await SendMessageAsync(login, "Your vote against has been noted.", cancellationToken);
    }

    // TrackMania's own vote (CallVote, used for /skip with several players) - GetCurrentCallVote
    // reports an empty CmdName when none is running.
    private async Task<bool> IsGameVoteRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var vote = await client.TracedCallAsync<Dictionary<string, object>>("GetCurrentCallVote", [], cancellationToken);
            return vote.TryGetValue("CmdName", out var cmdName) && cmdName is string { Length: > 0 };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> HasVoteMajorityAsync(CancellationToken cancellationToken)
    {
        var playerCount = await client.GetPlayerCountAsync(cancellationToken);
        return voteYesLogins is not null && voteYesLogins.Count * 2 > playerCount;
    }

    private string DescribeActiveVote() => votePresetName is not null
        ? $"preset '{votePresetName}'"
        : $"map {voteMapTrackId}";

    private async Task FinalizeVoteAsync(CancellationToken cancellationToken)
    {
        if (votePresetName is null)
        {
            return;
        }

        var presetName = votePresetName;
        ClearVote();
        await HideManialinkAsync(VotePopupManialinkId, cancellationToken);

        // checked before the session is touched - see PresetAsync
        var (preset, error) = LoadPresetForThisGame(presetName);

        if (preset is null)
        {
            await SendMessageAsync($"$F00Vote passed but preset failed to apply: {error}", cancellationToken);
            return;
        }

        // see ResetSessionStateForPresetSwitch for why this isn't StopSessionAsync
        if (SessionActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var displayName = ApplyPreset(presetName, preset);

        await SendMessageAsync($"$0F0Vote passed! Preset $FF0{displayName}$0F0 applied.", cancellationToken);
        await StartSessionAsync(cancellationToken);
    }

    // shared by /loadmap and a passed /votemap vote: queues the specific track id for the next
    // map fetch, then either swaps it in right away (session already running, same path /skip
    // uses) or starts a fresh session with it as the first map
    private async Task LoadForcedMapAsync(int trackId, CancellationToken cancellationToken)
    {
        forcedNextMapTrackId = trackId;

        try
        {
            if (SessionActive)
            {
                if (!await NextRandomMapAsync(goalReached: false, cancellationToken))
                {
                    // another switch is running right now; left set, the id would have loaded at
                    // some random later point instead
                    forcedNextMapTrackId = null;
                    await SendMessageAsync("$F00A map change is already in progress - try again in a moment.", cancellationToken);
                }
            }
            else
            {
                sessionStoppedByAdmin = false;
                await StartSessionAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // the outer chat-command dispatch loop also catches exceptions, but only logs to the
            // console - a failed /loadmap or /votemap should tell the player something went wrong
            // instead of silently doing nothing
            forcedNextMapTrackId = null;
            await SendMessageAsync($"$F00Failed to load TMX map {trackId}: {ex.Message}", cancellationToken);
        }
    }

    private async Task LoadMapAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can load a map directly. Try $FF0/votemap <id>$F00 instead.", cancellationToken);
            return;
        }

        if (args.Length == 0 || !int.TryParse(args[0], out var trackId))
        {
            await SendMessageAsync(login, "Usage: $FF0/loadmap <TMX id>$FFF", cancellationToken);
            return;
        }

        if (skippedMaps.Contains(trackId))
        {
            await SendMessageAsync(login, $"$F00Map {trackId} is on this server's skip list and can't be loaded here.", cancellationToken);
            return;
        }

        TmxRules.TrackDetails details;
        try
        {
            details = await tmxRules.GetTrackDetailsAsync(trackId, cancellationToken);
        }
        catch (Exception)
        {
            await SendMessageAsync(login, $"$F00Could not find TMX track {trackId}.", cancellationToken);
            return;
        }

        await SendMessageAsync($"$FF0{GetNicknameOrLogin(login)} is loading $0F0{details.Name}$FF0 (TMX {trackId})...", cancellationToken);
        await LoadForcedMapAsync(trackId, cancellationToken);
    }

    private async Task VoteMapAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var trackId))
        {
            await SendMessageAsync(login, "Usage: $FF0/votemap <TMX id>$FFF, then others type $FF0/yes$FFF to support.", cancellationToken);
            return;
        }

        if (skippedMaps.Contains(trackId))
        {
            await SendMessageAsync(login, $"$F00Map {trackId} is on this server's skip list and can't be loaded here.", cancellationToken);
            return;
        }

        if (votePresetName is not null || voteMapTrackId is not null)
        {
            await SendMessageAsync(login, $"$F00A vote for {DescribeActiveVote()} is already in progress.", cancellationToken);
            return;
        }

        // every attempt is a TMX request made from the callback stream
        if (!await CheckCooldownAsync(login, "votemap", cancellationToken))
        {
            return;
        }

        TmxRules.TrackDetails details;
        try
        {
            details = await tmxRules.GetTrackDetailsAsync(trackId, cancellationToken);
        }
        catch (Exception)
        {
            await SendMessageAsync(login, $"$F00Could not find TMX track {trackId}.", cancellationToken);
            return;
        }

        voteMapTrackId = trackId;
        voteYesLogins = [login];

        try
        {
            var tagsSuffix = details.Tags.Count > 0 ? $" · {string.Join(", ", details.Tags)}" : string.Empty;
            await SendMessageAsync($"$FF0{GetNicknameOrLogin(login)} started a vote to load $0F0{details.Name}$FF0 by {details.Author} ({details.DifficultyLabel}, AT {new TimeInt32(details.AuthorTimeMs)}{tagsSuffix}). Type $0F0/yes$FF0 to support ({PresetVoteWindowMs / 1000}s window).", cancellationToken);
            await client.SendManialinkPageAsync(BuildMapVotePopupManialink(details), cancellationToken: cancellationToken);

            if (await HasVoteMajorityAsync(cancellationToken))
            {
                await FinalizeMapVoteAsync(cancellationToken);
                return;
            }
        }
        catch
        {
            // The vote is already marked active here, but the timer below that ends it hasn't
            // started yet. On 2026-10-10 the announcement threw (a lost reply), the vote was never
            // cleared, and every /votemap for the next hour answered "already in progress" until
            // the service was restarted. Clear it so the player can simply try again.
            if (voteMapTrackId == trackId)
            {
                voteMapTrackId = null;
                voteYesLogins = null;
            }

            throw;
        }

        var voteId = ++voteCounter;
        StartVoteTimer(voteId, $"map {trackId}", FinalizeMapVoteAsync);
    }

    private async Task FinalizeMapVoteAsync(CancellationToken cancellationToken)
    {
        if (voteMapTrackId is not { } trackId)
        {
            return;
        }

        ClearVote();
        await HideManialinkAsync(VotePopupManialinkId, cancellationToken);

        await SendMessageAsync($"$0F0Vote passed! Loading TMX map {trackId}...", cancellationToken);
        await LoadForcedMapAsync(trackId, cancellationToken);
    }

    private async Task PresetsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var presetsDir = Path.Combine(AppContext.BaseDirectory, "Presets");

        if (!Directory.Exists(presetsDir))
        {
            await SendMessageAsync(login, "$F00No presets available.", cancellationToken);
            return;
        }

        var rawPresetNames = Directory.EnumerateFiles(presetsDir, "*.toml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Where(IsPresetForThisGame)
            .Order()
            .ToList();

        if (rawPresetNames.Count == 0)
        {
            await SendMessageAsync(login, "$F00No presets available.", cancellationToken);
            return;
        }

        var coloredNames = rawPresetNames.Select(name => $"$FF0{name}$FFF");
        await SendMessageAsync(login, [$"Presets: {string.Join(", ", coloredNames)}", "Select a preset using $FF0/preset <name>$FFF, or click below to start a vote"], cancellationToken);

        lastShownPresetNames = rawPresetNames;
        await client.SendManialinkPageToLoginAsync(login, BuildPresetListManialink(rawPresetNames), cancellationToken: cancellationToken);
    }

    public async Task OnPlayerFinish(int playerUid, string login, int score, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            return;
        }

        // see EnsureCurrentMapStateAsync's declaration - without this, a finish on a map that
        // loaded before anyone connected silently did nothing at all (no leaderboard entry, no
        // auto-skip, no replay link)
        await EnsureCurrentMapStateAsync(cancellationToken);

        if (currentMap is null)
        {
            return;
        }

        int? goalTime = config.AutoSkipMode switch
        {
            AutoSkipMode.AuthorMedal => currentMap.AuthorTime,
            AutoSkipMode.GoldMedal => currentMap.GoldTime,
            AutoSkipMode.SilverMedal => currentMap.SilverTime,
            AutoSkipMode.BronzeMedal => currentMap.BronzeTime,
            _ => null
        };

        if (score > 0 && (config.AutoSkipMode == AutoSkipMode.Finished || score <= goalTime))
        {
            var goalName = config.AutoSkipMode switch
            {
                AutoSkipMode.AuthorMedal => "Author Medal",
                AutoSkipMode.GoldMedal => "Gold Medal",
                AutoSkipMode.SilverMedal => "Silver Medal",
                AutoSkipMode.BronzeMedal => "Bronze Medal",
                _ => "finish line"
            };

            // The first qualifying finish on a map claims the switch to the next one. Everyone who
            // finishes after it - including finishes still queued behind the first one's handler -
            // gets their message, leaderboard credit and replay, but no second map switch.
            var advancesTheMap = !SwitchAlreadyClaimed && !isAdvancingToNextMap;
            var finishGeneration = mapGeneration;
            var finishClaim = advancesTheMap ? ClaimSwitch() : 0;

            try
            {
                await AnnounceFinishAsync(login, goalName, cancellationToken);
            }
            finally
            {
                // Whatever happened to the messages above - the finisher may have left the server
                // already ("Login unknown", "Client is not connected": 135 times on the old host) -
                // the map must still move on. The switch used to be scheduled after them, so a
                // player quitting right after finishing left everyone on the finished map.
                if (advancesTheMap)
                {
                    ScheduleAdvanceAfterFinish(finishGeneration, finishClaim);
                }
            }
        }
    }

    private async Task AnnounceFinishAsync(string login, string goalName, CancellationToken cancellationToken)
    {
        sessionStopwatch?.Stop();

        // The credit first: it is the one part that must not depend on chat messages reaching a
        // player who may already be gone. A failed save must not cost the rest either.
        var counts = !currentMapIsForced
            || (currentForcedMapHasNoRecord && currentMapTrackId is { } forcedId && forcedMapCredits.Add((login, forcedId)));

        if (counts)
        {
            try
            {
                await leaderboard.RecordFinishAsync(login, GetPlainNickname(login), GetRawNickname(login), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"ERROR: couldn't record {login}'s finish on the leaderboard - {ex.Message}");
            }
        }

        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has reached the $FF0{goalName}$0F0!", cancellationToken);
        }
        else
        {
            await SendMessageAsync($"$0F0You have reached the $FF0{goalName}$0F0!", cancellationToken);
        }
        await SendFrozenTimeMessageAsync(cancellationToken);

        await SendTop10PanelAsync(cancellationToken);
        await SendReplayLinkAsync(login, cancellationToken);

        if (currentMapIsForced)
        {
            await SendMessageAsync(login, counts
                ? "$888This map was loaded with /votemap or /loadmap but has no record on TMX yet - your finish counts."
                : "$888Maps loaded with /votemap or /loadmap only count for the leaderboard if they have no record on TMX yet, once per player.", cancellationToken);
        }
    }

    // Players get ReplaySaveGraceMs to save their replay before the map goes away. That wait used
    // to happen inside the PlayerFinish handler, which holds the whole callback stream: every
    // finish froze chat, checkpoints and widgets for everyone for 8 seconds.
    private void ScheduleAdvanceAfterFinish(int generation, int claim)
    {
        RunInBackground("switching to the next map after a finish", async () =>
        {
            await Task.Delay(ReplaySaveGraceMs, appToken);

            var ran = await RunUnderGameLockAsync("switching to the next map after a finish", async token =>
            {
                // the map changed in the meantime (an admin /loadmap, the server's own rotation),
                // or something else switched it and holds a newer claim (a session start, /preset)
                if (mapGeneration != generation || !SessionActive || advanceClaimId != claim)
                {
                    return;
                }

                try
                {
                    await NextRandomMapAsync(goalReached: true, token, ownsClaim: true);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Free the map again so the next finish or a /skip can retry, and tell the
                    // players - they used to sit on the finished map with no word at all.
                    ReleaseSwitchClaim();
                    Console.WriteLine($"Warning: switching to the next map after a finish failed - {ex.Message}");

                    try
                    {
                        await SendMessageAsync("$F00Couldn't load the next map - TMX may be slow right now. Type $FF0/skip$F00 to try again.", token);
                    }
                    catch (Exception)
                    {
                        // the log line above is the record
                    }
                }
            });

            // never got the lock: free the map, or it stays claimed with no switch coming
            if (!ran && advanceClaimId == claim)
            {
                ReleaseSwitchClaim();
            }
        });
    }

    private async Task SendReplayLinkAsync(string login, CancellationToken cancellationToken)
    {
        try
        {
            var fileName = $"{SanitizeFileNamePart(login)}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.Replay.Gbx";
            var relativeFileName = $"{config.ServerName}/{fileName}";

            await client.CallAsync("SaveBestGhostsReplay", [login, relativeFileName], cancellationToken);

            var host = string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost;
            var url = $"http://{host}:{config.ReplayServerPort}/replays/{Uri.EscapeDataString(fileName)}";

            await SendMessageAsync(login, $"$FF0Your TMX-valid replay: $FFF$l[{url}]{url}$l", cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Warning: failed to send replay link to {login} - {ex.Message}");

            try
            {
                await SendMessageAsync(login, "$F00Could not fetch your replay from the server, try saving manually with the in-game key.", cancellationToken);
            }
            catch (Exception)
            {
                // most often the player has already left - nobody to tell
            }
        }
    }

    private static string SanitizeFileNamePart(string value)
    {
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            buffer[i] = TmxRules.InvalidFileNameCharSearchValues.Contains(value[i]) ? '_' : value[i];
        }
        return new string(buffer);
    }

    // The dedicated server rejects RPCs while a challenge transition is underway with
    // "Change in progress". That is transient, so a call that lands at the wrong moment waits and
    // retries instead of failing outright, which is what made skipping look unreliable. The wait
    // is bounded by time, not attempts: three tries 700ms apart (~2s) was shorter than a map load.
    private static readonly TimeSpan TransitionRetryWindow = TimeSpan.FromSeconds(10);

    private async Task CallWithTransitionRetryAsync(string method, object[] args, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await client.CallAsync(method, args, cancellationToken);
                return;
            }
            catch (Exception ex) when (started.Elapsed < TransitionRetryWindow
                && ex.Message.Contains("Change in progress", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(700, cancellationToken);
            }
        }
    }

    // Returns false when it did nothing because a switch is already running or already asked for.
    public async Task<bool> NextRandomMapAsync(bool goalReached, CancellationToken cancellationToken, bool ownsClaim = false)
    {
        // with multiple players, every finisher fires its own PlayerFinish -> NextRandomMapAsync
        // call; if one is already advancing the map, a second concurrent call must be a no-op,
        // not a second NextChallenge - see isAdvancingToNextMap's declaration for why
        if (isAdvancingToNextMap)
        {
            return false;
        }

        // A switch away from this map was already asked for (see mapGeneration). A forced map
        // (/loadmap, a passed /votemap) still goes ahead: the admin or the vote asked for that
        // exact map, so it is fine for it to replace whatever was about to load.
        if (!ownsClaim && forcedNextMapTrackId is null && SwitchAlreadyClaimed)
        {
            return false;
        }

        var advanceId = Interlocked.Increment(ref advanceCounter);
        advanceOwner = advanceId;
        advanceStartedAt = DateTimeOffset.UtcNow;
        SetAdvanceStage($"start (goalReached={goalReached}, forced={forcedNextMapTrackId}, alreadyQueued={randomEnqueuedMapFileName is not null})");

        // The whole advance has one budget, so it can never outlive AdvanceStallTimeout and
        // have a second one start next to it.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(AdvanceBudget);
        var token = budget.Token;

        try
        {
            // In case there are multiple players, the session stopwatch cannot be stopped immediately
            // so in case there is actually just one player, we need to account for the time it took to setup the next challenge
            var setupWatch = Stopwatch.StartNew();

            // A forced id (/loadmap, or a /votemap that passed) has to win over whatever was
            // already queued. Consuming it only when nothing was enqueued meant the queued
            // random map loaded instead, and the forced id leaked into a later rotation - which
            // is exactly why /votemap "sometimes didn't load the right map".
            if (randomEnqueuedMapFileName is null || forcedNextMapTrackId is not null)
            {
                SetAdvanceStage("picking and downloading the next map from TMX");
                var forcedTrackId = forcedNextMapTrackId;
                forcedNextMapTrackId = null;
                forcedMapQueuedTrackId = forcedTrackId;

                var generationBeforeFetch = mapGeneration;
                var sessionBeforeFetch = sessionStopwatch;
                var nextMap = await WithoutGameLockIfBackgroundAsync(async () =>
                {
                    var map = forcedTrackId is { } forcedId
                        ? await tmxRules.GetMapGbxByIdAsync(forcedId, token)
                        : await tmxRules.NextMapGbxAsync(token);

                    // unknown (TMX didn't answer) counts as "has a record": no credit
                    forcedMapQueuedHasNoRecord = forcedTrackId is { } checkedId
                        && await tmxRules.HasRecordAsync(checkedId, token) == false;

                    // A random pick of the map that is running right now would only restart it (the
                    // recent-maps list is in memory and starts empty after every restart).
                    for (var repick = 0; forcedTrackId is null && map.TrackId == DisplayedMapTrackId && repick < 3; repick++)
                    {
                        map = await tmxRules.NextMapGbxAsync(token);
                    }

                    return map;
                });
                SetAdvanceStage($"downloaded map {nextMap.TrackId} '{nextMap.FileName}' ({nextMap.Data.Length} bytes)");

                // The lock may have been let go during the download: if the map changed by itself
                // or the session was stopped meanwhile, switching now would skip the fresh map or
                // undo the /stop.
                if (mapGeneration != generationBeforeFetch || !ReferenceEquals(sessionStopwatch, sessionBeforeFetch))
                {
                    Console.WriteLine($"Map {nextMap.TrackId} was downloaded, but the map or the session changed meanwhile - not switching.");
                    forcedMapQueuedTrackId = null;
                    return false;
                }

                // Last line of defence. The random pick and /loadmap + /votemap already refuse
                // skip-listed maps, so this should never fire - but if any route ever hands one
                // over, it must die HERE, before the file is written or inserted. Once the game
                // server has the map it can be loaded, and loading a map the game can't open
                // drops every player.
                if (skippedMaps.Contains(nextMap.TrackId))
                {
                    throw new InvalidOperationException($"map {nextMap.TrackId} is on this server's skip list");
                }

                // InsertChallenge de-dupes by the challenge's UID against the server's WHOLE selection
                // (playlist), not by the file path we write it under, so a map that is still in the
                // selection (the warmup map, or one that was queued and never played) cannot be
                // inserted again. Reuse that entry instead - found by its TMX id, never by a bare
                // file-name suffix, which matched "Race.Challenge.Gbx" to "A01-Race.Challenge.Gbx".
                SetAdvanceStage("asking the server for its map list (GetChallengeList)");
                var mapPath = await client.FindSelectedChallengeFileNameAsync(nextMap.TrackId, nextMap.FileName, token);
                SetAdvanceStage(mapPath is null
                    ? "map not in the server's list yet - writing file and inserting"
                    : $"map already in the server's list as '{mapPath}' - reusing it");

                if (mapPath is null)
                {
                    mapPath = InsertedChallengePath(nextMap);
                    await client.WriteFileAsync(mapPath, nextMap.Data, token);

                    // This retry loop is only for the TRANSIENT race: InsertChallenge called again
                    // before the dedicated server has fully consumed the PREVIOUS insert (e.g. calling
                    // /loadmap right after a /skip). Retrying after a short wait lets that prior
                    // transition settle instead of pressing on with NextChallenge anyway, which would
                    // just advance to whatever was already pending - silently loading the WRONG map
                    // instead of the one just requested. The PERMANENT "already added" case (map
                    // already in the selection) is handled above by reusing the existing entry, so it
                    // never reaches this loop.
                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            await CallWithTransitionRetryAsync("InsertChallenge", [mapPath], token);
                            break;
                        }
                        catch (Exception ex) when (ex.Message.Contains("already added", StringComparison.OrdinalIgnoreCase) && attempt < 5)
                        {
                            await Task.Delay(1000, token);
                        }
                    }
                }
                else
                {
                    // The reused entry sits wherever it was in the selection; without this
                    // NextChallenge loaded whatever happened to come next instead.
                    await CallWithTransitionRetryAsync("ChooseNextChallenge", [mapPath], token);
                }

                SetAdvanceStage("SetGameMode");
                await CallWithTransitionRetryAsync("SetGameMode", [1], token);
                roundsModeActive = false;

                // Both paths above end up here, so a reused selection entry inserted under the
                // older naming scheme still gets tied to its TMX id for ResolveRunningTrackId.
                RememberChallengeTrackId(mapPath, nextMap.TrackId);

                randomEnqueuedMapFileName = mapPath;
                pendingMapTrackId = nextMap.TrackId;
            }
            else if (roundsModeActive)
            {
                // /rounds was used and the next map was already queued, so the SetGameMode(1)
                // above never ran - without this the next map would start in Rounds mode too
                await CallWithTransitionRetryAsync("SetGameMode", [1], token);
                roundsModeActive = false;
            }

            if (await client.IsMultiplePlayersAsync(token) && (!goalReached || config.CallVoteOnFinish))
            {
                // TrackMania runs one vote at a time and refuses a second one ("Already waiting
                // for a vote"), which players got back as "That command failed".
                if (await IsGameVoteRunningAsync(token))
                {
                    await SendMessageAsync("$FF0A vote to change the map is already running - press $0F0F5$FF0 to agree.", token);
                }
                else
                {
                    await CallWithTransitionRetryAsync("CallVote", [XmlRpcClient.GenerateXmlPayload("NextChallenge", [])], token);

                    // Say it out loud. With two or more players /skip does not skip - it opens a
                    // vote - and players who did not know that read the silence as a broken command.
                    await SendMessageAsync("$FF0A vote to load the next map has started - press $0F0F5$FF0 to agree.", token);
                }

                // A vote may fail, and then this map stays - so it must not stay claimed.
                if (ownsClaim)
                {
                    ReleaseSwitchClaim();
                }
            }
            else
            {
                if (sessionStopwatch?.IsRunning == true)
                {
                    sessionStopwatchMillisecondOffset += (int)setupWatch.ElapsedMilliseconds;
                    sessionStopwatch.Stop();
                    await SendFrozenTimeMessageAsync(token);
                }

                // best-effort preview message only - if the challenge got swallowed as an "already
                // added" duplicate above, the server may not recognize THIS generated path even
                // though the underlying map is still fine to switch to, so this must never block
                // the actual NextChallenge call below
                try
                {
                    var info = await client.GetChallengeInfoAsync(randomEnqueuedMapFileName!, token);
                    var cpSuffix = info.NbCheckpoints is { } cpCount ? $" ({cpCount} CPs)" : string.Empty;
                    await SendMessageAsync($"Next map is ready: {info.Name}{cpSuffix}", token);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    await SendMessageAsync("Next map is ready.", token);
                }

                // InsertChallenge puts the map "after the current challenge" and NextChallenge
                // switches to it - that pair is the whole operation, nothing else is needed.
                //
                // There used to be a ChallengeRestart here, kept as a "nicety for the case where
                // nothing was pending". It was not harmless: when it arrived before the server had
                // begun the switch it cancelled the switch and reloaded the map already running.
                // Caught red-handed at startup on 2026-09-13, where the transition log showed
                // BeginRace firing twice for the same warmup map:
                //   Next track ID: 8142967
                //   Map loaded: unidentified (RPG impossible) - we had queued 8142967.
                // The server then sat on that map indefinitely, because with AutoSkipMode=Finished
                // nothing advances a map nobody can finish and the stalled-map watchdog only fires
                // on an empty server. Every restart stranded players on the warmup map.
                // A TrackMania vote for "next map" still open (opened by an earlier /skip with two
                // players on) would skip the map we load right now the moment it passes.
                try
                {
                    if (await IsGameVoteRunningAsync(token))
                    {
                        await client.CallAsync("CancelVote", [], token);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"Warning: couldn't cancel the running map vote - {ex.Message}");
                }

                SetAdvanceStage("NextChallenge");
                await CallWithTransitionRetryAsync("NextChallenge", [], token);
                ClaimSwitch();
                SetAdvanceStage("NextChallenge sent");
            }

            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Trace($"advance FAILED at '{advanceStage}': {ex.GetType().Name}: {ex.Message}");

            if (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"switching to the next map took longer than {AdvanceBudget.TotalSeconds:0}s (stuck at '{advanceStage}')", ex);
            }

            throw;
        }
        finally
        {
            // only the advance that owns the guard may clear it
            if (advanceOwner == advanceId)
            {
                advanceStartedAt = null;
                advanceStage = "idle";
            }
        }
    }

    private async Task SendWelcomeMessageAsync(string? login, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, config.WelcomeMessage.Prepend(string.Empty), cancellationToken);
    }

    private string GetServerMessageType(string? login)
    {
        if (false)
        {
            return login is null ? "ChatSend" : "ChatSendToLogin";
        }
        else
        {
            return login is null ? "ChatSendServerMessage" : "ChatSendServerMessageToLogin";
        }
    }

    private async Task SendMessageAsync(string? login, string message, CancellationToken cancellationToken)
    {
        await client.CallAsync(GetServerMessageType(login), login is null ? [message] : [message, login], cancellationToken);
    }

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login: null, message, cancellationToken);
    }

    private async Task SendMessageAsync(string? login, IEnumerable<string> messageLines, CancellationToken cancellationToken)
    {
        var serverMessageType = GetServerMessageType(login);

        await client.SystemMulticallAsync(messageLines
            .Select(msg => new XmlRpcCall(serverMessageType, login is null ? [msg] : [msg, login])), cancellationToken);
    }

    private async Task SendMessageAsync(IEnumerable<string> messageLines, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login: null, messageLines, cancellationToken);
    }

    private async Task SendFrozenTimeMessageAsync(CancellationToken cancellationToken)
    {
        if (config.TimeLimit.TotalMilliseconds <= 0 || sessionStopwatch is null)
        {
            return;
        }

        var millisecondsLeft = config.TimeLimit.TotalMilliseconds - (sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset);

        await SendMessageAsync($"Time limit frozen at $FF0{TimeSpan.FromMilliseconds(millisecondsLeft):g}", cancellationToken);
    }

    private string GetNicknameOrLogin(string login)
    {
        return nicknameCache.TryGetValue(login, out var nickname) ? $"$<{nickname}$>" : login;
    }

    private async Task SendTop10PanelAsync(CancellationToken cancellationToken)
    {
        try
        {
            // button icons are served by the built-in web server (see ReplayServer), same host as the replay links
            var host = string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost;
            var assetBaseUrl = $"http://{host}:{config.ReplayServerPort}";

            await client.SendManialinkPageAsync(BuildTop10Manialink(leaderboard.GetTop(10), assetBaseUrl, FavoriteServerUrl), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to update top 10 panel - {ex.Message}");
        }
    }

    // "SendHideManialinkPageToId" (RemoteClient used to have a wrapper for this) actually expects
    // a numeric UId, not our string XML "id" attribute, and throws "Value of type STRING supplied
    // where type INT was expected" - the real way to clear a specific id'd manialink is to send a
    // new, empty <manialink> with that same id (documented TMF behavior: a previously displayed
    // manialink with a matching id gets deleted when the replacement has no content)
    private async Task HideManialinkAsync(string id, CancellationToken cancellationToken)
    {
        await client.SendManialinkPageAsync($"""<manialink id="{id}" version="1"></manialink>""", cancellationToken: cancellationToken);
    }

    // same empty-body-replace trick as HideManialinkAsync, but for a manialink that was only ever
    // sent to one login (SendManialinkPageToLoginAsync) rather than broadcast to everyone
    private async Task HideManialinkToLoginAsync(string login, string id, CancellationToken cancellationToken)
    {
        await client.SendManialinkPageToLoginAsync(login, $"""<manialink id="{id}" version="1"></manialink>""", cancellationToken: cancellationToken);
    }

    // BeginRace can fail to fire for a map that was already loaded before any player connected
    // (confirmed live: no widgets appeared until the first skip/finish, and a finish on that
    // first map didn't record or auto-skip at all) - this fills in currentMap/
    // currentMapCheckpointTotal/currentMapTrackId on demand from the actually-running challenge,
    // so OnPlayerFinish and the widget senders below never silently no-op because BeginRace never
    // got the chance to populate them
    private async Task<ChallengeSummary> EnsureCurrentMapStateAsync(CancellationToken cancellationToken)
    {
        var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);

        currentMap ??= new MapInfo(
            AuthorTime: info.AuthorTime,
            GoldTime: info.GoldTime,
            SilverTime: info.SilverTime,
            BronzeTime: info.BronzeTime
        );
        currentMapCheckpointTotal ??= info.NbCheckpoints;

        // Same source of truth as BeginRace, and for the same reason: this used to fall back to
        // pendingMapTrackId, which is only the right answer when the server loaded the map we
        // queued rather than rotating its own selection.
        if (!currentMapIdentified)
        {
            currentMapTrackId = ResolveRunningTrackId(info.FileName);
            currentMapIdentified = true;

            // so the next BeginRace still knows what to prune and what goes into the history,
            // even on the path where BeginRace never fired for this map in the first place
            if (currentMapFileName is null)
            {
                currentMapFileName = info.FileName;
                lastRunningTrackId = currentMapTrackId;
            }
        }

        return info;
    }

    // sends the same set of map widgets BeginRace normally broadcasts to everyone, but to a
    // single just-connected player - needed both for a genuinely late joiner and for the
    // BeginRace-never-fired case EnsureCurrentMapStateAsync recovers from
    private async Task SendMapWidgetsToLoginAsync(string login, CancellationToken cancellationToken)
    {
        var info = await EnsureCurrentMapStateAsync(cancellationToken);

        if (currentMapCheckpointTotal is { } total && total > 0)
        {
            var current = Math.Min(playerCheckpointProgress.GetValueOrDefault(login, 0), total);
            await client.SendManialinkPageToLoginAsync(login, BuildCheckpointManialink(current, total), cancellationToken: cancellationToken);
        }

        if (info.LapRace)
        {
            await client.SendManialinkPageToLoginAsync(login, BuildRoundsPromptManialink(info.NbLaps), cancellationToken: cancellationToken);
        }

        if (currentMapTrackId is { } trackId)
        {
            // from the side, see BeginRace - a join must never wait for tm-exchange.com
            ShowMapInfoWidgetInBackground(trackId, info.AuthorTime, login);
        }
    }

    private static string BuildCheckpointManialink(int current, int total) => $"""
        <manialink id="cp_counter" version="1">
            <quad posn="-64 37 4" sizen="13 5" halign="left" valign="center" bgcolor="000A"/>
            <label posn="-63 37 5" halign="left" valign="center" textsize="2.5" textcolor="FFFF" text="CP {current} / {total}"/>
        </manialink>
        """;

    // sits just above the CP counter (which is anchored at y=37) so the two never overlap
    private static string GetDifficultyColor(string difficultyLabel) => difficultyLabel switch
    {
        "Beginner" => "0F0",
        "Intermediate" => "0AF",
        "Expert" => "FA0",
        "Lunatic" => "F0F",
        _ => "FFF",
    };

    private static string BuildMapInfoManialink(string difficultyLabel, int awards, int authorTimeMs)
    {
        var difficultyColor = GetDifficultyColor(difficultyLabel);

        // "$o" for bold, then "$z$FFF" to fully reset before the awards count so the bold/color
        // doesn't bleed into it - same reset pattern as the top-10 panel, see its own note on why
        // "$<...$>" isn't used instead
        var awardsSuffix = System.Security.SecurityElement.Escape($" · {awards} award{(awards == 1 ? "" : "s")}");
        var difficultyText = System.Security.SecurityElement.Escape(difficultyLabel);
        var authorTimeText = System.Security.SecurityElement.Escape($"AT {new TimeInt32(authorTimeMs)}");

        return $"""
            <manialink id="{MapInfoManialinkId}" version="1">
                <quad posn="-64 43 4" sizen="20 7" halign="left" valign="center" bgcolor="000A"/>
                <label posn="-63 45 5" halign="left" valign="center" textsize="1.3" textcolor="{difficultyColor}F" text="$o{difficultyText}$z$FFF{awardsSuffix}"/>
                <label posn="-63 41.5 5" halign="left" valign="center" textsize="1.3" textcolor="0F0F" text="$o{authorTimeText}"/>
            </manialink>
            """;
    }

    // sits just below the CP counter - only sent while the loaded map is a real multilap
    // challenge (see BeginRace), hidden again the moment a non-multilap map loads
    private static string BuildRoundsPromptManialink(int nbLaps) => $"""
        <manialink id="{RoundsPromptManialinkId}" version="1">
            <quad posn="-64 22 4" sizen="20 6.5" halign="left" valign="center" bgcolor="000A"/>
            <label posn="-63 24.1 5" halign="left" valign="center" textsize="1.1" textcolor="FF8" text="Multilap map ({nbLaps} laps)"/>
            <quad posn="-63 21.4 5" sizen="18 2.6" halign="left" valign="center" bgcolor="0B3A" action="{ActionRoundsAccept}"/>
            <label posn="-62 21.5 6" halign="left" valign="center" textsize="1" textcolor="FFF" text="Click for Rounds mode"/>
        </manialink>
        """;

    private static string BuildVotePopupManialink(string presetName)
    {
        var text = System.Security.SecurityElement.Escape($"Switch preset to '{presetName}'?");
        return $"""
            <manialink id="{VotePopupManialinkId}" version="1">
                <quad posn="-25 46 4" sizen="50 10" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 44 5" halign="center" valign="center" textsize="1.5" textcolor="FF0F" text="{text}"/>
                <quad posn="-15 40 5" sizen="12 3.5" halign="center" valign="center" bgcolor="0B3A" action="{ActionVoteYes}"/>
                <label posn="-15 40 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="YES"/>
                <quad posn="15 40 5" sizen="12 3.5" halign="center" valign="center" bgcolor="B00A" action="{ActionVoteNo}"/>
                <label posn="15 40 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="NO"/>
            </manialink>
            """;
    }

    // shares the same manialink id/YES-NO actions as BuildVotePopupManialink - only one vote (preset
    // or map) can ever be active at a time (see the votePresetName/voteMapTrackId mutual-exclusion
    // guards), so there's no risk of the two colliding on screen
    private static string BuildMapVotePopupManialink(TmxRules.TrackDetails details)
    {
        var difficultyColor = GetDifficultyColor(details.DifficultyLabel);
        var nameText = System.Security.SecurityElement.Escape(details.Name);
        var authorText = System.Security.SecurityElement.Escape($"by {details.Author}");
        var difficultyText = System.Security.SecurityElement.Escape($"{details.DifficultyLabel} · {details.Awards} award{(details.Awards == 1 ? "" : "s")}");
        var tagsSummary = details.Tags.Count > 0 ? string.Join(", ", details.Tags) : "no tags";
        var atTagsText = System.Security.SecurityElement.Escape($"AT {new TimeInt32(details.AuthorTimeMs)} · {tagsSummary}");

        return $"""
            <manialink id="{VotePopupManialinkId}" version="1">
                <quad posn="-35 50 4" sizen="70 17" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 47.5 5" halign="center" valign="center" textsize="1.6" textcolor="FF0F" text="$o{nameText}"/>
                <label posn="0 44.8 5" halign="center" valign="center" textsize="1.1" textcolor="FFFF" text="{authorText}"/>
                <label posn="0 42.3 5" halign="center" valign="center" textsize="1.1" textcolor="{difficultyColor}F" text="{difficultyText}"/>
                <label posn="0 39.8 5" halign="center" valign="center" textsize="1.1" textcolor="0F0F" text="{atTagsText}"/>
                <quad posn="-15 36 5" sizen="12 3.5" halign="center" valign="center" bgcolor="0B3A" action="{ActionVoteYes}"/>
                <label posn="-15 36 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="YES"/>
                <quad posn="15 36 5" sizen="12 3.5" halign="center" valign="center" bgcolor="B00A" action="{ActionVoteNo}"/>
                <label posn="15 36 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="NO"/>
            </manialink>
            """;
    }

    private static string BuildPresetListManialink(IReadOnlyList<string> presetNames)
    {
        // manialink coordinates must use "." as the decimal separator regardless of the host
        // machine's locale - plain string interpolation of a double formats it with the CURRENT
        // THREAD CULTURE, which on a German-locale box turns e.g. "18.5" into "18,5" and breaks
        // the XML attribute (extra token where the parser expects exactly one number pair)
        static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var rows = new System.Text.StringBuilder();
        var y = 18.0;

        for (var i = 0; i < presetNames.Count; i++)
        {
            var text = System.Security.SecurityElement.Escape(presetNames[i]);
            rows.AppendLine($"""<quad posn="-19 {Inv(y)} 5" sizen="38 3" halign="left" valign="center" bgcolor="0004" action="{ActionPresetBase + i}"/>""");
            rows.AppendLine($"""<label posn="0 {Inv(y)} 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="{text}"/>""");
            y -= 3.5;
        }

        var boxHeight = 10 + (presetNames.Count * 3.5);

        return $"""
            <manialink id="{PresetListManialinkId}" version="1">
                <quad posn="-20 23 4" sizen="40 {Inv(boxHeight)}" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 21.5 5" halign="center" valign="center" textsize="1.5" textcolor="FF0F" text="Click to vote for a preset"/>
                <quad posn="18.5 21.7 5" sizen="3 3" halign="center" valign="center" bgcolor="B00A" action="{ActionClosePresetList}"/>
                <label posn="18.5 21.7 6" halign="center" valign="center" textsize="1.1" textcolor="FFF" text="X"/>
                {rows}
            </manialink>
            """;
    }

    private const double HistoryNameWidthBudget = 30.0;

    private static string BuildHistoryManialink(IReadOnlyList<string> names)
    {
        static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var rows = new System.Text.StringBuilder();
        var y = 18.0;

        for (var i = 0; i < names.Count; i++)
        {
            var displayName = TruncateForDisplayWidth(names[i], HistoryNameWidthBudget);
            var text = System.Security.SecurityElement.Escape($"-{i + 1}: {displayName}");
            rows.AppendLine($"""<quad posn="-19 {Inv(y)} 5" sizen="38 3" halign="left" valign="center" bgcolor="0004" action="{ActionHistoryBase + i}"/>""");
            rows.AppendLine($"""<label posn="0 {Inv(y)} 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="{text}"/>""");
            y -= 3.5;
        }

        var boxHeight = 10 + (names.Count * 3.5);

        return $"""
            <manialink id="{HistoryManialinkId}" version="1">
                <quad posn="-20 23 4" sizen="40 {Inv(boxHeight)}" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 21.5 5" halign="center" valign="center" textsize="1.5" textcolor="FF0F" text="Click a map for its TMX link"/>
                <quad posn="18.5 21.7 5" sizen="3 3" halign="center" valign="center" bgcolor="B00A" action="{ActionCloseHistory}"/>
                <label posn="18.5 21.7 6" halign="center" valign="center" textsize="1.1" textcolor="FFF" text="X"/>
                {rows}
            </manialink>
            """;
    }

    // a flat character count doesn't track rendered width - a lot of TMF nicknames lean on wide
    // Unicode lookalike glyphs (Cyrillic/Greek/symbols) that render noticeably wider than plain
    // ASCII in-game, so those need to cost more against the budget than a raw count would suggest
    private const double Top10NicknameWidthBudget = 17.5;
    private const double WideCharWidth = 1.8;

    // truncates by visible width only - $-format codes are zero-width and are always consumed
    // whole (never split), so a truncated name keeps working, valid color codes instead of
    // falling back to plain text
    private static string TruncateForDisplayWidth(string nickname, double widthBudget)
    {
        // Two passes: measure the whole name first, so a name that fits is never cut. Only a name
        // that doesn't fit gets truncated, and then the "…" is paid for out of the budget - it used
        // to be appended for free, which pushed an already-full row past the edge of the panel.
        if (MeasureDisplayWidth(nickname, int.MaxValue, out _) <= widthBudget)
        {
            return nickname;
        }

        MeasureDisplayWidth(nickname, widthBudget - WideCharWidth, out var cutIndex);
        return nickname[..cutIndex] + "…";
    }

    // Walks the name tracking the $-code state that changes how wide text renders ($o bold, $w wide,
    // $n narrow, $z reset). Stops at the first character that would push the width past the limit
    // and reports its index; returns the width consumed so far (the full width if nothing overflowed).
    private static double MeasureDisplayWidth(string nickname, double limit, out int cutIndex)
    {
        var width = 0.0;
        var scale = 1.0;
        var bold = false;
        var wide = false;
        var narrow = false;
        var i = 0;
        cutIndex = nickname.Length;

        while (i < nickname.Length)
        {
            if (nickname[i] == '$')
            {
                var codeMatch = TmFormatCodeRegex().Match(nickname, i);
                if (codeMatch.Success && codeMatch.Index == i)
                {
                    var code = codeMatch.Groups[1].Value;
                    if (code == "$")
                    {
                        // "$$" renders one literal dollar sign
                        if (width + scale > limit)
                        {
                            cutIndex = i;
                            return width;
                        }

                        width += scale;
                    }
                    else if (code.Length == 1)
                    {
                        switch (char.ToLowerInvariant(code[0]))
                        {
                            case 'o': bold = true; break;
                            case 'w': wide = true; narrow = false; break;
                            case 'n': narrow = true; wide = false; break;
                            case 'z': bold = false; wide = false; narrow = false; break;
                        }

                        scale = (bold ? 1.15 : 1.0) * (wide ? 1.4 : narrow ? 0.7 : 1.0);
                    }

                    i += codeMatch.Length;
                    continue;
                }
            }

            var charWidth = GetGlyphWidth(nickname, i, out var length) * scale;
            if (width + charWidth > limit)
            {
                cutIndex = i;
                return width;
            }

            width += charWidth;
            i += length;
        }

        return width;
    }

    // Rendered width of the character at index (in units of one plain ASCII character). Flat
    // "non-ASCII = 1.8" undercounted the East Asian symbols and Korean jamo some players put in
    // their names, which render roughly three characters wide, so those rows spilled out of the
    // top 10 panel. Combining marks add almost nothing; a surrogate pair is one glyph.
    private static double GetGlyphWidth(string text, int index, out int length)
    {
        length = 1;
        var c = text[index];

        if (c <= 0x7F)
        {
            return 1.0;
        }

        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return EastAsianWideWidth;
        }

        return c switch
        {
            >= '̀' and <= 'ͯ' => 0.3,                                   // combining diacritics
            >= 'ᄀ' and <= 'ᇿ' => EastAsianWideWidth,                    // Hangul jamo
            >= '⺀' and <= '鿿' => EastAsianWideWidth,                    // CJK symbols, kana, ideographs
            >= '가' and <= '힯' => EastAsianWideWidth,                    // Hangul syllables
            >= '＀' and <= '￯' => EastAsianWideWidth,                    // fullwidth forms
            _ => WideCharWidth,
        };
    }

    private const double EastAsianWideWidth = 3.0;

    // gold/silver/bronze colors for the top 3 ranks - "$o" bolds, reset with "$z" before the name
    // itself so a player's own raw nickname color (or the default white) takes back over
    private static readonly string[] RankColors = ["FD0", "CCC", "C83"];

    private const string DiscordInviteUrl = "https://discord.gg/HRShWnzpK3";
    // TMF prepends "http://" to a quad's url attribute itself, so it must be given without a scheme
    private const string DiscordInviteUrlNoScheme = "discord.gg/HRShWnzpK3";
    private string FavoriteServerUrl => $"tmtp://#addfavourite={config.FavoriteLogin}";

    private static string BuildTop10Manialink(IReadOnlyList<LeaderboardEntry> top, string assetBaseUrl, string favoriteUrl)
    {
        // manialink coordinates must use "." as the decimal separator regardless of the host
        // machine's locale - see the identical note on BuildPresetListManialink
        static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var rows = new System.Text.StringBuilder();
        var y = 16;

        // box is right-anchored at x=64 with this width, so its center (for the title) sits at
        // 64 - width/2 - keep these two in sync if the width ever changes again
        const double boxWidth = 17.0;
        const double boxCenterX = 64.0 - (boxWidth / 2.0);

        rows.AppendLine($"""<label posn="{Inv(boxCenterX)} 18 5" halign="center" valign="center" textsize="1.6" textcolor="FF0F" text="$s$oTop 10 Finishers"/>""");

        // thin yellow rule under the header, same color as the header text
        rows.AppendLine($"""<quad posn="{Inv(64.0 - boxWidth + 0.8)} 16.6 5" sizen="{Inv(boxWidth - 1.6)} 0.2" halign="left" valign="center" bgcolor="FF0F"/>""");

        for (var i = 0; i < top.Count; i++)
        {
            var entry = top[i];

            // truncation is code-aware (see TruncateForDisplayWidth), so even a cut-off name keeps
            // its real in-game color. Reset with "$z" rather than wrapping in "$<...$>" - TMF's
            // manialink parser isn't standards-compliant XML, so a literal "<"/">" here would get
            // entity-escaped below and might not get decoded back by the game
            var source = string.IsNullOrEmpty(entry.RawNickname) ? entry.LastNickname : entry.RawNickname;
            var displayName = TruncateForDisplayWidth(source, Top10NicknameWidthBudget);

            // name and score share ONE right-aligned label - splitting them into two independently
            // anchored labels left a visible gap between a truncated "…" and the score whenever the
            // name didn't reach its own anchor. A single label keeps them contiguous, and the score
            // (as the last characters in the string) still lands exactly on the right anchor.
            var rankPrefix = i < RankColors.Length ? $"$o${RankColors[i]}{i + 1}.$z" : $"{i + 1}.";
            var text = System.Security.SecurityElement.Escape($"{rankPrefix} {displayName} $z$FFF- {entry.Finishes}");
            y -= 4;
            rows.AppendLine($"""<label posn="63.5 {y} 5" halign="right" valign="center" textsize="1.2" textcolor="FFF" text="{text}"/>""");
        }

        var boxHeight = 7 + (top.Count * 4);

        return $"""
            <manialink id="top10_panel" version="1">
                <quad posn="64 20 4" sizen="{Inv(boxWidth)} {boxHeight}" halign="right" valign="top" bgcolor="0008"/>
                {rows}
                <quad posn="55.5 27 5" sizen="3.9 5.2" halign="left" valign="top" image="{assetBaseUrl}/discord.png" url="{DiscordInviteUrlNoScheme}"/>
                <quad posn="60.1 27 5" sizen="3.9 5.2" halign="left" valign="top" image="{assetBaseUrl}/favorite.png" manialink="{favoriteUrl}"/>
            </manialink>
            """;
    }

    // plain-text nickname for destinations that don't understand TM's $-formatting codes (e.g. Discord)
    private string GetPlainNickname(string login)
    {
        var nickname = nicknameCache.TryGetValue(login, out var nick) ? nick : login;
        return StripTmCodes(nickname);
    }

    // Plain text from a TrackMania-formatted string. $l / $h / $p are links ("$l[http://...]text$l")
    // and must not survive into Discord or the status page as text either.
    private static string StripTmCodes(string formatted) =>
        TmFormatCodeRegex().Replace(TmLinkCodeRegex().Replace(formatted, string.Empty), string.Empty);

    [GeneratedRegex(@"\$[lhpLHP](\[[^\]]*\])?")]
    private static partial Regex TmLinkCodeRegex();

    // nickname with its original $-formatting codes intact, for HUD/manialink text (which renders
    // them the same way in-game chat does) - not safe for Discord/the status page, see GetPlainNickname
    private string GetRawNickname(string login)
    {
        return nicknameCache.TryGetValue(login, out var nick) ? nick : login;
    }

    [GeneratedRegex(@"[^\s""]+|""[^""]*""")]
    private static partial Regex CommandArgsRegex();

    // case-insensitive: TrackMania accepts $S as well as $s (map names showed up as "$S[Mini Trial]")
    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[<>oiswnmgzt$])", RegexOptions.IgnoreCase)]
    private static partial Regex TmFormatCodeRegex();

    // the "_tmx<id>_" marker InsertedChallengePath puts into every map path we insert
    [GeneratedRegex(@"_tmx(\d+)_")]
    private static partial Regex ChallengeTrackIdRegex();
}
