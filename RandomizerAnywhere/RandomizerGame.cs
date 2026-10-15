using ManiaAPI.XmlRpc;
using RandomizerAnywhere.Config;
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
    private string? randomEnqueuedMapFileName;

    // set by /loadmap or a passed /votemap vote, consumed (and cleared) by the next
    // NextRandomMapAsync map fetch - lets that single method serve both "next random map" and
    // "this specific TMX id" without duplicating the enqueue/insert/gamemode plumbing
    private int? forcedNextMapTrackId;

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
        var mapGap = currentMapStartedAt is { } startedAt ? (lastPolledMapChangedAt - startedAt).TotalSeconds : (double?)null;

        return $"callbacks: received={client.CallbacksReceived} lastCallbackAge={lastCallbackAge:0.0}s "
            + $"racingProgressAhead={(racingGap is { } r ? $"{r:0.0}s (limit {CallbackRacingGracePeriod.TotalSeconds:0}s)" : "n/a")} "
            + $"mapChangeWithoutBeginRace={(mapGap is { } m ? $"{m:0.0}s (limit {CallbackGracePeriod.TotalSeconds:0}s)" : "n/a")} "
            + $"rankedCount={lastPolledRankedCount} players={lastOnlinePlayerCount} advancing={isAdvancingToNextMap} reconnecting={reconnectInProgress}";
    }

    // An advance still "in flight" after this long is treated as dead rather than blocking
    // every future one. Without it a single hung RPC latched the guard permanently and
    // /skip, /votemap and the finish-advance all became silent no-ops until a restart.
    private static readonly TimeSpan AdvanceStallTimeout = TimeSpan.FromSeconds(30);

    private bool isAdvancingToNextMap =>
        advanceStartedAt is { } startedAt && DateTimeOffset.UtcNow - startedAt < AdvanceStallTimeout;

    private int? currentMapCheckpointTotal;
    private readonly Dictionary<string, int> playerCheckpointProgress = [];
    // tie-breaks the live "who's leading" ranking on the status page: same checkpoint count ->
    // whoever reached it first is ahead
    private readonly Dictionary<string, DateTimeOffset> playerCheckpointTimestamp = [];

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

            if (currentMapStartedAt is not { } startedAt)
            {
                // No BeginRace seen yet - nothing to compare against. The first map after a
                // restart is a known blind spot, so never call it dead on that alone.
                return true;
            }

            // If BeginRace stops arriving, startedAt stays pinned to the previous map while
            // the poll keeps moving forward, so this gap grows without bound. In normal
            // operation it is a few seconds - the poll interval - and never grows.
            return lastPolledMapChangedAt - startedAt < CallbackGracePeriod;
        }
    }

    private bool SessionActive => sessionStopwatch is not null;

    public RandomizerGame(RemoteClient client, TmxRules tmxRules, AppConfig config, DiscordNotifier discordNotifier, Leaderboard leaderboard, SkippedMaps skippedMaps)
    {
        this.skippedMaps = skippedMaps;
        this.client = client;
        this.tmxRules = tmxRules;
        this.config = config;
        this.discordNotifier = discordNotifier;
        this.leaderboard = leaderboard;

        commandHandlers = new()
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

    private void RegisterCallbacks()
    {
        client.On("TrackMania.BeginRace", async (methodParams, cancellationToken) =>
        {
            try
            {
                var mapInfo = (Dictionary<string, object>)methodParams[0];

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

                LogMapTransition(info);

                // Drop the map we just came off the server's playlist. Until this existed the
                // selection only ever grew, so any advance that did not land on the map we queued
                // served an already-played one - which is what players see as "it keeps giving us
                // maps we already finished". Done here rather than in EndRace because the outgoing
                // map is only safely removable once a different one is confirmed running.
                if (previousMapFileName is not null
                    && !ChallengeFileKey(previousMapFileName).Equals(ChallengeFileKey(info.FileName ?? string.Empty), StringComparison.OrdinalIgnoreCase))
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
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1500);
                            await NextRandomMapAsync(goalReached: false, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: couldn't skip past skip-listed map {skippedId} - {ex.Message}");
                        }
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
                    try
                    {
                        var meta = await tmxRules.GetTrackMetaAsync(trackIdForMeta, cancellationToken);
                        await client.SendManialinkPageAsync(BuildMapInfoManialink(meta.DifficultyLabel, meta.Awards, info.AuthorTime), cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: failed to fetch TMX map metadata - {ex.Message}");
                    }
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

        client.On("TrackMania.EndRace", async (methodParams, cancellationToken) =>
        {
            try
            {
                // capture the outgoing map's id for "/map -1"/"-2"/... etc. BEFORE it gets cleared
                // below - BeginRace can't do this itself since EndRace has already nulled
                // currentMapTrackId by the time the next BeginRace fires
                if (currentMapTrackId is { } endedTrackId)
                {
                    mapHistory.Insert(0, endedTrackId);
                    if (mapHistory.Count > MaxMapHistory)
                    {
                        mapHistory.RemoveAt(mapHistory.Count - 1);
                    }
                }

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

        client.On("TrackMania.PlayerCheckpoint", async (methodParams, cancellationToken) =>
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

        client.On("TrackMania.PlayerConnect", async (methodParams, cancellationToken) =>
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

        client.On("TrackMania.PlayerChat", async (methodParams, cancellationToken) =>
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
                        await SendMessageAsync(login, $"$F00That command failed: {ex.Message}", cancellationToken);
                    }
                    catch (Exception replyEx)
                    {
                        Console.WriteLine($"Warning: could not tell {login} the command failed - {replyEx.Message}");
                    }
                }
            }
        });

        client.On("TrackMania.PlayerFinish", async (methodParams, cancellationToken) =>
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

        client.On("TrackMania.StatusChanged", async (methodParams, cancellationToken) =>
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

        client.On("TrackMania.EndRound", async (methodParams, cancellationToken) =>
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

        client.On("TrackMania.PlayerManialinkPageAnswer", async (methodParams, cancellationToken) =>
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

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await StartSessionAsync(cancellationToken);
                    break;
                }
                catch (Exception ex) when (attempt < 5)
                {
                    Console.WriteLine($"Warning: StartSessionAsync attempt {attempt} failed - {ex.Message}");
                    await Task.Delay(2000, cancellationToken);
                }
            }
        }

        // WaitForCloseAsync is what keeps the process alive. A reconnect closes the old
        // connection on purpose, so returning from it is not automatically a shutdown - check
        // whether a reconnect caused it and re-attach to the new connection if so.
        while (!cancellationToken.IsCancellationRequested)
        {
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

            if (Volatile.Read(ref reconnectGeneration) == generation)
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
    private async Task TryRecoverCallbacksAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - lastCallbackRecoveryAt < CallbackRecoveryCooldown)
        {
            return;
        }

        lastCallbackRecoveryAt = DateTimeOffset.UtcNow;
        Console.WriteLine("Rebuilding the game server connection to recover the callback stream...");

        reconnectInProgress = true;
        Interlocked.Increment(ref reconnectGeneration);

        try
        {
            await client.ReconnectAsync(cancellationToken);

            // Give the fresh connection a clean slate rather than judging it on the old one's
            // evidence; if it is still deaf, the next map change re-detects it.
            var now = DateTimeOffset.UtcNow;
            currentMapStartedAt = now;
            lastPolledMapChangedAt = now;
            lastPolledRankedCount = 0;
            lastRacingProgressAt = null;
            callbackLossReported = false;
            failedRecoveries = 0;

            Console.WriteLine("Reconnected - callback stream restored without a restart.");
        }
        catch (Exception ex)
        {
            failedRecoveries++;
            Console.WriteLine($"ERROR: reconnect failed ({failedRecoveries}) - {ex.Message}");

            // Last resort only: if redialling keeps failing and nobody is racing, hand the
            // problem to systemd. Never while players are on - a dead controller is bad, but
            // kicking people mid-race to fix it is not our call.
            if (failedRecoveries >= 3 && lastOnlinePlayerCount == 0)
            {
                Console.WriteLine("Reconnect keeps failing and nobody is online - restarting the controller.");
                Environment.Exit(70);
            }
        }
        finally
        {
            reconnectInProgress = false;
        }
    }

    // Step 2: on a busy server a dead stream shows up at the next map change. On an idle empty
    // one nothing changes at all, so the stall hides until somebody joins and hits it. Provoke a
    // callback instead - ChallengeRestart always yields BeginRace, and with nobody online
    // restarting the current map costs nothing.
    private async Task ProbeCallbacksAsync(CancellationToken cancellationToken)
    {
        if (!SessionActive || lastOnlinePlayerCount > 0 || reconnectInProgress || isAdvancingToNextMap)
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
        var before = client.CallbacksReceived;

        Console.WriteLine("No callbacks for a while on an empty server - probing the stream.");
        await CallWithTransitionRetryAsync("ChallengeRestart", [], cancellationToken);
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
                await CheckStalledMapAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: stalled-map watchdog failed - {ex.Message}");
            }

            try
            {
                await ProbeCallbacksAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: callback probe failed - {ex.Message}");
            }
        }
    }

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
                Console.WriteLine($"Warning: status.json write timed out (stuck on '{statusStage}'), retrying next tick.");
                Diagnostics.Dump($"status.json write timed out on '{statusStage}'", recentLines: 25);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to write status.json - {ex.Message}");
            }

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

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private async Task WriteStatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PlayerSummary> onlinePlayers = [];
        SetStatusStage("GetPlayerList");
        try
        {
            onlinePlayers = await client.GetPlayersAsync(cancellationToken);
        }
        catch (Exception)
        {
            // controller not fully connected yet, report nobody online for now
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
        catch (Exception)
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
        catch (Exception)
        {
            // ranking not available right now - just skip this witness for this tick
        }

        // ranked by live race progress: most checkpoints first, ties broken by who reached
        // their current checkpoint first (see playerCheckpointTimestamp's declaration)
        var racers = onlinePlayers
            .Select(p => new
            {
                Nickname = TmFormatCodeRegex().Replace(p.NickName, string.Empty),
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
        var json = JsonSerializer.Serialize(status);
        await File.WriteAllTextAsync(statusFilePath, json, cancellationToken);
        SetStatusStage("idle");
    }

    private async Task StartAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can start a session.", cancellationToken);
            return;
        }

        await StartSessionAsync(cancellationToken);
    }

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            sessionStopwatch = new();
            await SetTimeLimitAsync(cancellationToken);
            await SendMessageAsync([string.Empty, "$0F0Let's begin!"], cancellationToken);

            if (config.TimeLimit.TotalMilliseconds > 0)
            {
                await SendMessageAsync($"Time limit set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }

            await NextRandomMapAsync(goalReached: false, cancellationToken);
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
        if (!SessionActive || isAdvancingToNextMap)
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
        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"Player {GetNicknameOrLogin(login)} wants to skip the current challenge - this starts a vote.", cancellationToken);
        }
        else
        {
            await SendMessageAsync("Skipping the current challenge...", cancellationToken);
        }

        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    // args[0] == "-N" (N >= 1) refers to the Nth map before the current one (e.g. "/imp -1" for
    // the map that just ended, "/imp -3" for three maps ago - see /history for what's available).
    // IsCurrent is false for any "-N" - callers use it to skip current-map-only side effects like
    // advancing to a new map or reading live checkpoint info for a map that isn't loaded anymore
    private (int? TrackId, bool IsCurrent) ResolveMapReference(string[] args)
    {
        if (args.Length > 0 && int.TryParse(args[0], out var offset) && offset < 0)
        {
            var historyIndex = -offset - 1;
            return (historyIndex < mapHistory.Count ? mapHistory[historyIndex] : null, false);
        }

        return (DisplayedMapTrackId, true);
    }

    // The path a fetched TMX map gets inserted under. The TMX id goes into the name on purpose:
    // the dedicated server rotates its own selection whenever a map ends without us advancing it
    // (a time limit running out with nobody finishing, say), so "the map we last picked" and "the
    // map actually running" drift apart, and the file name is the only thing that ties a running
    // challenge back to the id it came from. Observed live on 2026-09-11: the controller had
    // /map, the TMX link and the status page preview all pointing at a map from 15 minutes back.
    private static string InsertedChallengePath(InMemoryFile map) =>
        Path.Combine("_RandomizerAny", $"{DateTimeOffset.UtcNow.Ticks}_tmx{map.TrackId}_{map.FileName}");

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
        var (trackId, isCurrent) = ResolveMapReference(args);

        if (trackId is not { } id)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}";

        tmxRules.ExcludeForSession(id);

        var skipSuffix = isCurrent ? ", skipping" : string.Empty;
        await SendMessageAsync($"$F00Map {id} reported impossible by {GetNicknameOrLogin(login)} - won't be shown again until reviewed{skipSuffix}.", cancellationToken);
        await discordNotifier.PostAsync($"{DiscordServerTag}**{GetPlainNickname(login)}** reported map **{id}** as impossible: {tmxUrl}", cancellationToken);

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

        var (trackId, isCurrent) = ResolveMapReference(args);

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
        var (trackId, _) = ResolveMapReference(args);

        if (trackId is not { } id)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}";

        await SendMessageAsync($"$FF0Map {id} flagged as hard by {GetNicknameOrLogin(login)} for review.", cancellationToken);
        await discordNotifier.PostHardAsync($"{DiscordServerTag}**{GetPlainNickname(login)}** flagged map **{id}** as hard: {tmxUrl}", cancellationToken);
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
        var (trackId, isCurrent) = ResolveMapReference(args);

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

        for (var i = 0; i < Math.Min(mapHistory.Count, HistoryDisplayCount); i++)
        {
            var trackId = mapHistory[i];
            string name;
            try
            {
                name = await tmxRules.GetTrackNameAsync(trackId, cancellationToken);
            }
            catch (Exception)
            {
                name = $"Track {trackId}";
            }

            lines.Add($"$FF0-{i + 1}$FFF: {name}");
            shownTrackIds.Add(trackId);
            shownNames.Add(name);
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
        await client.CallAsync("ChallengeRestart", [], cancellationToken);

        var lapsSuffix = info.NbLaps > 0 ? $" ({info.NbLaps} laps)" : string.Empty;
        await SendMessageAsync($"$0F0{GetNicknameOrLogin(login)} switched this multilap map to Rounds mode{lapsSuffix} for a valid replay. Back to TimeAttack once this map ends.", cancellationToken);
    }

    private async Task InfoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, [
            "$0BF--- 100% TMX Project ---",
            "$FF0/skip$FFF - skip the current map (votes if multiple players are online)",
            "$FF0/map$FFF (or $FF0/map -1$FFF, $FF0-2$FFF, ... for a past map) - show the map's TMX link",
            "$FF0/history$FFF - list recent maps and what $FF0-1$FFF, $FF0-2$FFF, ... refer to",
            "$FF0/rounds$FFF - switch a multilap map to Rounds mode for a valid replay",
            "$FF0/imp$FFF (or $FF0/imp -1$FFF, $FF0-2$FFF, ... for a past map) - report a map as impossible",
            "$FF0/hard$FFF (or $FF0/hard -1$FFF, $FF0-2$FFF, ... for a past map) - flag a map as hard for review",
            "$FF0/top$FFF - show the top finishers on this server",
            "$FF0/rank$FFF - show your own rank and finish count",
            "$FF0/votepreset <name>$FFF - propose switching preset, others confirm with $0F0/yes$FFF ($FF0/presets$FFF for names)",
            "$FF0/votemap <TMX id>$FFF - propose loading a specific TMX map by id, shows name/author/difficulty/AT/tags, others confirm with $0F0/yes$FFF",
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
        var commands = await client.GetChatCommandListAsync(cancellationToken);
        var formattedCommands = commands
            .Select(cmd => $"$FF0{cmd}$FFF")
            .Order();

        await SendMessageAsync(login, $"Commands: {string.Join(", ", formattedCommands)}", cancellationToken);
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

        var wasActive = SessionActive;
        if (wasActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var (success, displayName, error) = TryApplyPreset(args[0]);

        if (!success)
        {
            await SendMessageAsync(login, $"$F00{error}", cancellationToken);
            return;
        }

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
        var preset = TomlLoader.LoadPresetConfig(Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml"));
        return preset is null || preset.SupportsGame(config.Game);
    }

    private (bool Success, string? DisplayName, string? Error) TryApplyPreset(string presetName)
    {
        var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml");

        if (!File.Exists(presetPath))
        {
            return (false, null, $"Preset '{presetName}' not found.");
        }

        var presetConfig = TomlLoader.LoadPresetConfig(presetPath);

        if (presetConfig is null)
        {
            return (false, null, $"Failed to load preset '{presetName}'.");
        }

        if (!presetConfig.SupportsGame(config.Game))
        {
            return (false, null, $"Preset '{presetName}' is not for this game.");
        }

        presetConfig.Apply(config);
        config.LastPreset = presetConfig;

        var displayName = string.IsNullOrWhiteSpace(presetConfig.DisplayName) ? presetName : presetConfig.DisplayName;
        return (true, displayName, null);
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
        var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml");

        if (!File.Exists(presetPath) || !IsPresetForThisGame(presetName))
        {
            await SendMessageAsync(login, $"$F00Preset '{presetName}' not found.", cancellationToken);
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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PresetVoteWindowMs, cancellationToken);

                if (votePresetName != presetName)
                {
                    return; // already finalized or superseded
                }

                if (await HasVoteMajorityAsync(cancellationToken))
                {
                    await FinalizeVoteAsync(cancellationToken);
                }
                else
                {
                    await SendMessageAsync($"$F00Vote for preset '{presetName}' failed - not enough support.", cancellationToken);
                    votePresetName = null;
                    voteYesLogins = null;
                    await HideManialinkAsync(VotePopupManialinkId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                // server shutting down mid-vote, nothing to do
            }
        }, cancellationToken);
    }

    private async Task YesAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (voteYesLogins is null || (votePresetName is null && voteMapTrackId is null))
        {
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
        votePresetName = null;
        voteYesLogins = null;
        await HideManialinkAsync(VotePopupManialinkId, cancellationToken);

        // see ResetSessionStateForPresetSwitch for why this isn't StopSessionAsync
        if (SessionActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var (success, displayName, error) = TryApplyPreset(presetName);

        if (!success)
        {
            await SendMessageAsync($"$F00Vote passed but preset failed to apply: {error}", cancellationToken);
            return;
        }

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
                await NextRandomMapAsync(goalReached: false, cancellationToken);
            }
            else
            {
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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PresetVoteWindowMs, cancellationToken);

                if (voteMapTrackId != trackId)
                {
                    return; // already finalized or superseded
                }

                if (await HasVoteMajorityAsync(cancellationToken))
                {
                    await FinalizeMapVoteAsync(cancellationToken);
                }
                else
                {
                    await SendMessageAsync($"$F00Vote to load map {trackId} failed - not enough support.", cancellationToken);
                    voteMapTrackId = null;
                    voteYesLogins = null;
                    await HideManialinkAsync(VotePopupManialinkId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                // server shutting down mid-vote, nothing to do
            }
        }, cancellationToken);
    }

    private async Task FinalizeMapVoteAsync(CancellationToken cancellationToken)
    {
        if (voteMapTrackId is not { } trackId)
        {
            return;
        }

        voteMapTrackId = null;
        voteYesLogins = null;
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

            sessionStopwatch?.Stop();

            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has reached the $FF0{goalName}$0F0!", cancellationToken);
            }
            else
            {
                await SendMessageAsync($"$0F0You have reached the $FF0{goalName}$0F0!", cancellationToken);
            }
            await SendFrozenTimeMessageAsync(cancellationToken);

            await leaderboard.RecordFinishAsync(login, GetPlainNickname(login), GetRawNickname(login), cancellationToken);
            await SendTop10PanelAsync(cancellationToken);
            await SendReplayLinkAsync(login, cancellationToken);
            await Task.Delay(ReplaySaveGraceMs, cancellationToken);

            await NextRandomMapAsync(goalReached: true, cancellationToken);
        }
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
        catch (Exception ex)
        {
            await SendMessageAsync(login, "$F00Could not fetch your replay from the server, try saving manually with the in-game key.", cancellationToken);
            Console.WriteLine($"Warning: failed to send replay link to {login} - {ex.Message}");
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
    // "Change in progress". That is transient - the transition settles in well under a
    // second - so a /skip that lands at the wrong moment should wait and retry instead of
    // failing outright, which is what made skipping look unreliable.
    private async Task CallWithTransitionRetryAsync(string method, object[] args, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.CallAsync(method, args, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 4
                && ex.Message.Contains("Change in progress", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(700, cancellationToken);
            }
        }
    }

    public async Task NextRandomMapAsync(bool goalReached, CancellationToken cancellationToken)
    {
        // with multiple players, every finisher fires its own PlayerFinish -> NextRandomMapAsync
        // call; if one is already advancing the map, a second concurrent call must be a no-op,
        // not a second NextChallenge - see isAdvancingToNextMap's declaration for why
        if (isAdvancingToNextMap)
        {
            return;
        }

        advanceStartedAt = DateTimeOffset.UtcNow;
        SetAdvanceStage($"start (goalReached={goalReached}, forced={forcedNextMapTrackId}, alreadyQueued={randomEnqueuedMapFileName is not null})");
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
                var nextMap = forcedNextMapTrackId is { } forcedTrackId
                    ? await tmxRules.GetMapGbxByIdAsync(forcedTrackId, cancellationToken)
                    : await tmxRules.NextMapGbxAsync(cancellationToken);
                forcedNextMapTrackId = null;
                SetAdvanceStage($"downloaded map {nextMap.TrackId} '{nextMap.FileName}' ({nextMap.Data.Length} bytes)");

                // Last line of defence. The random pick and /loadmap + /votemap already refuse
                // skip-listed maps, so this should never fire - but if any route ever hands one
                // over, it must die HERE, before the file is written or inserted. Once the game
                // server has the map it can be loaded, and loading a map the game can't open
                // drops every player.
                if (skippedMaps.Contains(nextMap.TrackId))
                {
                    forcedNextMapTrackId = null;
                    throw new InvalidOperationException($"map {nextMap.TrackId} is on this server's skip list");
                }

                // InsertChallenge de-dupes by the challenge's UID against the server's WHOLE selection
                // (playlist), not by the file path we write it under - and that selection only ever
                // grows, so once this exact TMX map was inserted before (an earlier random rotation, or
                // a previous /loadmap or /votemap for the same id), it's stuck there for the rest of the
                // server's uptime and every future InsertChallenge for it fails with "already added",
                // no matter what's currently loaded. Reuse the existing selection entry instead of
                // fighting that permanent case.
                SetAdvanceStage("asking the server for its map list (GetChallengeList)");
                var mapPath = await client.FindSelectedChallengeFileNameAsync(nextMap.FileName, cancellationToken);
                SetAdvanceStage(mapPath is null
                    ? "map not in the server's list yet - writing file and inserting"
                    : $"map already in the server's list as '{mapPath}' - reusing it");

                if (mapPath is null)
                {
                    mapPath = InsertedChallengePath(nextMap);
                    await client.WriteFileAsync(mapPath, nextMap.Data, cancellationToken);

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
                            await client.CallAsync("InsertChallenge", [mapPath], cancellationToken);
                            break;
                        }
                        catch (Exception ex) when (ex.Message.Contains("already added", StringComparison.OrdinalIgnoreCase) && attempt < 5)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                SetAdvanceStage("SetGameMode");
                await client.CallAsync("SetGameMode", [1], cancellationToken);

                // Both paths above end up here, so a reused selection entry inserted under the
                // older naming scheme still gets tied to its TMX id for ResolveRunningTrackId.
                RememberChallengeTrackId(mapPath, nextMap.TrackId);

                randomEnqueuedMapFileName = mapPath;
                pendingMapTrackId = nextMap.TrackId;
            }

            if (await client.IsMultiplePlayersAsync(cancellationToken) && (!goalReached || config.CallVoteOnFinish))
            {
                await CallWithTransitionRetryAsync("CallVote", [XmlRpcClient.GenerateXmlPayload("NextChallenge", [])], cancellationToken);

                // Say it out loud. With two or more players /skip does not skip - it opens a
                // vote - and players who did not know that read the silence as a broken command.
                await SendMessageAsync("$FF0A vote to load the next map has started - press $0F0F5$FF0 or type $0F0/yes$FF0 to agree.", cancellationToken);
            }
            else
            {
                if (sessionStopwatch?.IsRunning == true)
                {
                    sessionStopwatchMillisecondOffset += (int)setupWatch.ElapsedMilliseconds;
                    sessionStopwatch.Stop();
                    await SendFrozenTimeMessageAsync(cancellationToken);
                }

                // best-effort preview message only - if the challenge got swallowed as an "already
                // added" duplicate above, the server may not recognize THIS generated path even
                // though the underlying map is still fine to switch to, so this must never block
                // the actual NextChallenge call below
                try
                {
                    var info = await client.GetChallengeInfoAsync(randomEnqueuedMapFileName, cancellationToken);
                    var cpSuffix = info.NbCheckpoints is { } cpCount ? $" ({cpCount} CPs)" : string.Empty;
                    await SendMessageAsync($"Next map is ready: {info.Name}{cpSuffix}", cancellationToken);
                }
                catch (Exception)
                {
                    await SendMessageAsync("Next map is ready.", cancellationToken);
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
                SetAdvanceStage("NextChallenge");
                await CallWithTransitionRetryAsync("NextChallenge", [], cancellationToken);
                SetAdvanceStage("NextChallenge sent");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Trace($"advance FAILED at '{advanceStage}': {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            advanceStartedAt = null;
            advanceStage = "idle";
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
            // so the next BeginRace still knows what to prune, even on the path where BeginRace
            // never fired for this map in the first place
            currentMapFileName ??= info.FileName;
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
            try
            {
                var meta = await tmxRules.GetTrackMetaAsync(trackId, cancellationToken);
                await client.SendManialinkPageToLoginAsync(login, BuildMapInfoManialink(meta.DifficultyLabel, meta.Awards, info.AuthorTime), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to fetch TMX map metadata for {login} - {ex.Message}");
            }
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
        return TmFormatCodeRegex().Replace(nickname, string.Empty);
    }

    // nickname with its original $-formatting codes intact, for HUD/manialink text (which renders
    // them the same way in-game chat does) - not safe for Discord/the status page, see GetPlainNickname
    private string GetRawNickname(string login)
    {
        return nicknameCache.TryGetValue(login, out var nick) ? nick : login;
    }

    [GeneratedRegex(@"[^\s""]+|""[^""]*""")]
    private static partial Regex CommandArgsRegex();

    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[<>oiswnmgzt$])")]
    private static partial Regex TmFormatCodeRegex();

    // the "_tmx<id>_" marker InsertedChallengePath puts into every map path we insert
    [GeneratedRegex(@"_tmx(\d+)_")]
    private static partial Regex ChallengeTrackIdRegex();
}
