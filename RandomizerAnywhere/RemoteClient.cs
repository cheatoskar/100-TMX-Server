using ManiaAPI.XmlRpc;
using Polly;
using Polly.Retry;
using RandomizerAnywhere.Config;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Linq;
using System.Text.Json;

namespace RandomizerAnywhere;

internal sealed class RemoteClient : IAsyncDisposable, IDisposable
{
    private static readonly ResiliencePipeline connectionPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 5,
            BackoffType = DelayBackoffType.Exponential
        })
        .Build();

    private static readonly string[] buildFormats = [
        "yyyy-MM-dd_HH_mm",
        "yyyy-MM-dd"
    ];

    private readonly AppConfig config;

    private XmlRpcClient? raw;

    public XmlRpcClient Raw => raw ?? throw new InvalidOperationException("Client is not connected.");

    public bool IsConnected => raw is not null;

    // A call slower than this is worth a line in the journal on its own; healthy calls answer in
    // milliseconds, so anything near this means the dedicated server (or the socket) is stalling.
    private static readonly TimeSpan SlowRpcThreshold = TimeSpan.FromSeconds(2);

    // Hard limit for a single call. A healthy call answers in milliseconds, but on 2026-10-01 one
    // SendDisplayManialinkPage never got its reply while every other call kept answering in
    // 1-45 ms - and because it was made from inside the BeginRace handler, which holds the whole
    // callback stream, that one lost reply froze every callback for the full 60 s handler cutoff.
    // Bounding each call keeps a single lost reply down to this long, and the caller gets an
    // ordinary exception it already knows how to handle.
    // The lost replies themselves were ManiaAPI.XmlRpc <= 2.9.2 registering a call's reply slot only
    // after sending it, so a fast reply could arrive first and be thrown away (the dedicated server's
    // ConsoleLog shows those calls as "succeeded"); 2.9.5 registers first. The bound stays as a guard.
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    // Every call to the dedicated server goes through here so each one is timed and recorded in the
    // flight recorder (see Diagnostics). Behaviour is unchanged: same call, same exceptions.
    public async Task<T> TracedCallAsync<T>(string method, object[] parameters, CancellationToken cancellationToken = default)
    {
        if (Translate(ref method, ref parameters, out var localAnswer))
        {
            return (T)localAnswer!;
        }

        var startedAt = Stopwatch.GetTimestamp();
        Diagnostics.Trace($"rpc> {method}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RpcTimeout);

            try
            {
                var result = await Raw.CallAsync<T>(method, parameters, timeout.Token);
                LogRpcFinished(method, startedAt, error: null);
                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{method} got no reply within {RpcTimeout.TotalSeconds:0}s");
            }
        }
        catch (Exception ex)
        {
            LogRpcFinished(method, startedAt, ex);
            throw;
        }
    }

    // For a call made every second (the old TM server's finish poll): same timeout, but a successful
    // call is not recorded. Otherwise the 400-line flight recorder would hold nothing but this poll
    // and lose the history a stall report needs. Slow or failed calls are still recorded.
    public async Task<T> PollCallAsync<T>(string method, object[] parameters, CancellationToken cancellationToken = default)
    {
        if (Translate(ref method, ref parameters, out var localAnswer))
        {
            return (T)localAnswer!;
        }

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RpcTimeout);

            try
            {
                var result = await Raw.CallAsync<T>(method, parameters, timeout.Token);

                if (Stopwatch.GetElapsedTime(startedAt) >= SlowRpcThreshold)
                {
                    LogRpcFinished(method, startedAt, error: null);
                }

                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{method} got no reply within {RpcTimeout.TotalSeconds:0}s");
            }
        }
        catch (Exception ex)
        {
            LogRpcFinished(method, startedAt, ex);
            throw;
        }
    }

    public async Task<object?> TracedCallAsync(string method, object[] parameters, CancellationToken cancellationToken = default)
    {
        if (Translate(ref method, ref parameters, out var localAnswer))
        {
            return localAnswer;
        }

        var startedAt = Stopwatch.GetTimestamp();
        Diagnostics.Trace($"rpc> {method}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RpcTimeout);

            try
            {
                var result = await Raw.CallAsync(method, parameters, timeout.Token);
                LogRpcFinished(method, startedAt, error: null);
                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{method} got no reply within {RpcTimeout.TotalSeconds:0}s");
            }
        }
        catch (Exception ex)
        {
            LogRpcFinished(method, startedAt, ex);
            throw;
        }
    }

    private static void LogRpcFinished(string method, long startedAt, Exception? error)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        if (error is null)
        {
            Diagnostics.Trace($"rpc< {method} {elapsed.TotalMilliseconds:0}ms");
        }
        else
        {
            Diagnostics.Trace($"rpc! {method} {elapsed.TotalMilliseconds:0}ms {error.GetType().Name}: {error.Message}");
        }

        if (elapsed >= SlowRpcThreshold)
        {
            Console.WriteLine($"[diag] slow call: {method} took {elapsed.TotalSeconds:0.0}s{(error is null ? string.Empty : $" and failed ({error.GetType().Name}: {error.Message})")}");
        }

        if (error is TimeoutException)
        {
            // a lost reply: keep the surrounding history, it is what shows what led up to it
            Diagnostics.Dump($"call timed out: {method}", recentLines: 150);
        }
    }

    // TM2 (ManiaPlanet) and TM2020 servers: the same protocol as TMF, but "Map" where TMF says
    // "Challenge", and the game mode is a script (TimeAttack) steered through script settings.
    // The game logic keeps speaking TMF; this is the one place that knows the difference.
    // Checked on local servers of both on 2026-11-14 (system.listMethods, script settings).
    public bool IsManiaPlanet => config.IsManiaPlanet;

    private static readonly Dictionary<string, string> ManiaPlanetMethodNames = new()
    {
        ["ChallengeRestart"] = "RestartMap",
        ["NextChallenge"] = "NextMap",
        ["InsertChallenge"] = "InsertMap",
        ["RemoveChallenge"] = "RemoveMap",
        ["ChooseNextChallenge"] = "ChooseNextMap",
        ["AddChallenge"] = "AddMap",
        ["AddChallengeList"] = "AddMapList",
        ["GetTracksDirectory"] = "GetMapsDirectory",
        ["GetCurrentChallengeInfo"] = "GetCurrentMapInfo",
        ["GetNextChallengeInfo"] = "GetNextMapInfo",
        ["GetChallengeInfo"] = "GetMapInfo",
        ["GetChallengeList"] = "GetMapList",
        ["GetCurrentChallengeIndex"] = "GetCurrentMapIndex",
    };

    // the server's own name for a TMF method (also for CallVote payloads)
    public string MethodName(string tmfName) =>
        IsManiaPlanet && ManiaPlanetMethodNames.TryGetValue(tmfName, out var name) ? name : tmfName;

    // true = answered here, without asking the server
    private bool Translate(ref string method, ref object[] parameters, out object? localAnswer)
    {
        localAnswer = null;

        if (!IsManiaPlanet)
        {
            return false;
        }

        switch (method)
        {
            // the mode is the TimeAttack script for good; TimeAttack there drives every lap of a
            // multilap map, so the TMF Rounds switch (/rounds) has nothing to do
            case "SetGameMode":
            case "SetRoundForcedLaps":
                localAnswer = true;
                return true;
            case "GetGameMode":
                localAnswer = 1;
                return true;
            // script mode keeps no ranking the old way; the finishes seen on WayPoint stand in
            case "GetCurrentRanking":
                localAnswer = finishes.Ranking();
                return true;
            // 0 = no limit in TimeAttack on both (checked: no map end within 45 s; 5 s ends it)
            case "SetTimeAttackLimit":
                method = "SetModeScriptSettings";
                parameters = [new Dictionary<string, object> { ["S_TimeLimit"] = MillisecondsToSeconds(parameters[0]) }];
                return false;
            case "SetChatTime":
                method = "SetModeScriptSettings";
                parameters = [new Dictionary<string, object> { ["S_ChatTime"] = MillisecondsToSeconds(parameters[0]) }];
                return false;
            default:
                method = MethodName(method);
                return false;
        }
    }

    private static int MillisecondsToSeconds(object value) =>
        Convert.ToInt32(value, CultureInfo.InvariantCulture) is var ms and > 0 ? Math.Max((ms + 999) / 1000, 1) : 0;

    // Best time per login on the running map, from the script's WayPoint events (TM2/TM2020).
    private readonly FinishTracker finishes = new();

    private sealed class FinishTracker
    {
        private readonly Dictionary<string, int> best = [];
        private readonly object gate = new();

        public void Clear()
        {
            lock (gate)
            {
                best.Clear();
            }
        }

        public void Add(string login, int time)
        {
            lock (gate)
            {
                if (time > 0 && (!best.TryGetValue(login, out var old) || time < old))
                {
                    best[login] = time;
                }
            }
        }

        public List<object> Ranking()
        {
            lock (gate)
            {
                return best.OrderBy(kv => kv.Value)
                    .Select((kv, i) => (object)new Dictionary<string, object> { ["Login"] = kv.Key, ["BestTime"] = kv.Value, ["Rank"] = i + 1 })
                    .ToList();
            }
        }
    }

    private HashSet<string>? supportedMethods;
    private HashSet<string> SupportedMethods => supportedMethods ?? throw new InvalidOperationException("Client is not connected.");

    private RemoteVersion? versionInfo;
    public RemoteVersion VersionInfo => versionInfo ?? throw new InvalidOperationException("Version info is not available. Call GetVersionAsync first.");

    public RemoteClient(AppConfig config)
    {
        this.config = config;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        raw = await connectionPipeline.ExecuteAsync(async token =>
            await XmlRpcClient.ConnectAsync(IPAddress.Loopback, config.XmlRpcPort, cancellationToken: token), cancellationToken);

        supportedMethods = new(await raw.SystemListMethodsAsync(cancellationToken));
        serverCallbacks = [];
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var result = await TracedCallAsync<bool>("Authenticate", ["SuperAdmin", "SuperAdmin"], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to authenticate as SuperAdmin.");
        }
    }

    public async Task SetServerNameAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var result = await TracedCallAsync<bool>("SetServerName", [serverName], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to set server name.");
        }
    }

    public async ValueTask<RemoteVersion> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (versionInfo is not null)
        {
            return versionInfo;
        }

        var versionDict = await TracedCallAsync<Dictionary<string, object>>("GetVersion", [], cancellationToken);

        var buildString = versionDict.TryGetValue("Build", out var build) ? build as string : null;
        var buildDate = buildString is null ? default : DateTime.TryParseExact(buildString, buildFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedBuild) ? parsedBuild : default(DateTime?);

        return versionInfo = new RemoteVersion(
            versionDict["Name"] as string ?? throw new InvalidOperationException("Missing Name in GetVersion response"),
            versionDict["Version"] as string ?? throw new InvalidOperationException("Missing Version in GetVersion response"),
            buildDate,
            versionDict.TryGetValue("TitleId", out var titleId) ? titleId as string : null);
    }

    public async Task<bool> EnableCallbacksAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportedMethods.Contains("EnableCallbacks"))
        {
            return false;
        }

        // the callback shapes the adapters below expect (ManiaPlanet.PlayerChat etc.)
        if (IsManiaPlanet)
        {
            await TracedCallAsync("SetApiVersion", [ManiaPlanetApiVersion], cancellationToken);
        }

        var result = await TracedCallAsync<bool>("EnableCallbacks", [true], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to enable callbacks.");
        }

        // finishes, checkpoints and map starts only come as script callbacks there
        if (IsManiaPlanet)
        {
            await TracedCallAsync("TriggerModeScriptEventArray", ["XmlRpc.EnableCallbacks", new[] { "true" }], cancellationToken);
        }

        return result;
    }

    private const string ManiaPlanetApiVersion = "2013-04-16";

    public bool SupportsWriteFile() => SupportedMethods.Contains("WriteFile");

    // TMF refuses any XML-RPC request over 512 KB (the limit XAseco's GbxRemote enforces too), and
    // it does so by dropping the connection - on 2026-10-10 a 934 KB map sent via /votemap took the
    // whole server down twice in a row. WriteFile sends the map base64-encoded (4/3 the size) inside
    // an XML envelope, so anything that would not fit goes straight to disk instead; the dedicated
    // server always runs on this machine (we connect over loopback), so its Tracks folder is ours too.
    private const int MaxRequestBytes = 512 * 1024 - 8;
    private const int XmlEnvelopeAllowance = 4 * 1024;

    private static bool FitsInOneRequest(byte[] fileData) =>
        (fileData.Length + 2) / 3 * 4 + XmlEnvelopeAllowance <= MaxRequestBytes;

    public async Task WriteFileAsync(string filePath, byte[] fileData, CancellationToken cancellationToken = default)
    {
        if (SupportedMethods.Contains("WriteFile") && FitsInOneRequest(fileData))
        {
            await CallAsync("WriteFile", [filePath, fileData], cancellationToken);
            return;
        }

        var tracksDirectory = await TracedCallAsync<string>("GetTracksDirectory", [], cancellationToken);

        if (Path.GetDirectoryName(filePath) is string directoryRelativePath)
        {
            Directory.CreateDirectory(Path.Combine(tracksDirectory, directoryRelativePath));
        }

        await File.WriteAllBytesAsync(Path.Combine(tracksDirectory, filePath), fileData, cancellationToken);
    }

    public async Task CallAsync(string methodName, object[] parameters, CancellationToken cancellationToken = default)
    {
        var result = await TracedCallAsync(methodName, parameters, cancellationToken);

        if (result is false)
        {
            throw new InvalidOperationException($"Failed to call method {methodName}.");
        }
    }

    public async Task<IEnumerable<XmlRpcCallResult>> SystemMulticallAsync(IEnumerable<XmlRpcCall> calls, CancellationToken cancellationToken = default)
    {
        var callList = calls.ToList();
        var startedAt = Stopwatch.GetTimestamp();
        Diagnostics.Trace($"rpc> multicall x{callList.Count}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RpcTimeout);

            try
            {
                var result = await Raw.SystemMulticallAsync(callList, timeout.Token);
                LogRpcFinished($"multicall x{callList.Count}", startedAt, error: null);
                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"multicall x{callList.Count} got no reply within {RpcTimeout.TotalSeconds:0}s");
            }
        }
        catch (Exception ex)
        {
            LogRpcFinished($"multicall x{callList.Count}", startedAt, ex);
            throw;
        }
    }

    public async Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        await Raw.WaitForCloseAsync(cancellationToken);
    }

    public async Task<string> GetPlayerNicknameAsync(string login, CancellationToken cancellationToken = default)
    {
        var playerInfo = await TracedCallAsync<Dictionary<string, object>>("GetPlayerInfo", [login], cancellationToken);
        return (string)playerInfo["NickName"];
    }

    // Kept so a reconnect can register them again on the new connection.
    private IReadOnlyList<string> chatCommands = [];

    public async Task RegisterChatCommandsAsync(IEnumerable<string> commands, CancellationToken cancellationToken = default)
    {
        if (!SupportedMethods.Contains("AddChatCommand"))
        {
            chatCommands = [];
            return;
        }

        chatCommands = commands.ToList();
        await SystemMulticallAsync(chatCommands.Select(cmd => new XmlRpcCall("AddChatCommand", [cmd])), cancellationToken);
    }

    public async Task<bool> IsMultiplePlayersAsync(CancellationToken cancellationToken = default)
    {
        var playerCount = await GetPlayerCountAsync(cancellationToken);
        return playerCount > 1;
    }

    // How many players have a time on the current challenge. The dedicated server maintains this
    // itself, so it moves whether or not the callback stream is alive - which makes it the one
    // piece of evidence that someone is actually RACING, as opposed to merely being connected.
    // The health check needs exactly that distinction; see RandomizerGame.CallbacksHealthy.
    public async Task<int> GetCurrentRankingCountAsync(CancellationToken cancellationToken = default)
    {
        var ranking = await TracedCallAsync<List<object>>("GetCurrentRanking", [200, 0], cancellationToken);
        return ranking.OfType<Dictionary<string, object>>()
            .Count(p => p.TryGetValue("BestTime", out var best) && best is int time && time > 0);
    }

    public async Task<int> GetPlayerCountAsync(CancellationToken cancellationToken = default)
    {
        var players = await GetPlayersAsync(cancellationToken);
        return players.Count;
    }

    // maxCount of 200 comfortably covers TMF's real player cap (well under 100) - GetPlayerCountAsync
    // used to pass 2 here, which silently capped the reported count at 2 once a 3rd player joined
    public async Task<IReadOnlyList<PlayerSummary>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        var playerList = await TracedCallAsync<List<object>>("GetPlayerList", [200, 0], cancellationToken);
        return playerList.OfType<Dictionary<string, object>>()
            .Where(p => p.TryGetValue("Login", out var login) && !string.IsNullOrEmpty(login as string))
            .Where(p => !IsServerEntry(p))
            .Select(p => new PlayerSummary((string)p["Login"], (string)p["NickName"]))
            .ToList();
    }

    public static bool IsServerEntry(Dictionary<string, object> player) =>
        player.TryGetValue("Flags", out var flags) && flags is int value && value / 100000 % 10 == 1;

    public async Task<ChallengeSummary> GetChallengeInfoAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var mapInfo = await TracedCallAsync<Dictionary<string, object>>("GetChallengeInfo", [fileName], cancellationToken);
        return ToChallengeSummary(mapInfo);
    }

    // unlike GetChallengeInfo(fileName), which can report placeholder values (e.g. NbCheckpoints = -1)
    // for a challenge that's only queued and not yet actively loaded, this reflects the map that's
    // actually running right now
    public async Task<ChallengeSummary> GetCurrentChallengeInfoAsync(CancellationToken cancellationToken = default)
    {
        var mapInfo = await TracedCallAsync<Dictionary<string, object>>("GetCurrentChallengeInfo", [], cancellationToken);
        return ToChallengeSummary(mapInfo);
    }

    // Whether the connected dedicated server knows this XML-RPC method (system.listMethods at connect).
    // The 2006 TM server (ESWC) lacks manialinks, ghost replays, ForceSpectator and more.
    public bool Supports(string method) => SupportedMethods.Contains(MethodName(method));

    // Manialinks (widgets, menus, popups) only exist from TMF on. On the old TM server every widget
    // call is a silent no-op, so the game logic around them needs no special case; everything a
    // player must know also goes to the chat.
    public bool SupportsManialinks => Supports("SendDisplayManialinkPage");

    public async Task SendManialinkPageAsync(string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        if (!SupportsManialinks)
        {
            return;
        }

        await TracedCallAsync("SendDisplayManialinkPage", [Manialink(manialink), timeoutSeconds, hideOnClick], cancellationToken);
    }

    public async Task SendManialinkPageToLoginAsync(string login, string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        if (!SupportsManialinks)
        {
            return;
        }

        await TracedCallAsync("SendDisplayManialinkPageToLogin", [login, Manialink(manialink), timeoutSeconds, hideOnClick], cancellationToken);
    }

    private string Manialink(string tmfManialink)
    {
        if (!IsManiaPlanet)
        {
            return tmfManialink;
        }

        var page = ManialinkV3.Convert(tmfManialink, config.Game == GameTitle.TM2020);

        // RANDANY_DUMP_MANIALINKS=1: every converted page goes to manialinks/ next to the program,
        // to check the TM2/TM2020 HUD without a game client (one file per manialink id)
        if (Environment.GetEnvironmentVariable("RANDANY_DUMP_MANIALINKS") == "1")
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "manialinks");
                Directory.CreateDirectory(dir);
                var id = System.Text.RegularExpressions.Regex.Match(page, "<manialink[^>]*id=\"([^\"]*)\"").Groups[1].Value;
                File.WriteAllText(Path.Combine(dir, $"{(id.Length > 0 ? id : "page")}.xml"), page);
            }
            catch (IOException)
            {
                // diagnostics only
            }
        }

        return page;
    }

    public async Task HideAllManialinksAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsManialinks)
        {
            return;
        }

        await TracedCallAsync("SendHideManialinkPage", [], cancellationToken);
    }

    private static ChallengeSummary ToChallengeSummary(Dictionary<string, object> mapInfo)
    {
        // TMF counts the finish line as a checkpoint here, so a map with 2 checkpoint blocks
        // reports NbCheckpoints = 3. Every consumer shows this number to players - the in-game
        // CP counter, /map, the "next map is ready" line, the status page - so all of them were
        // one too high. Normalised once, here, rather than subtracting 1 in five places and
        // forgetting the sixth. A negative value stays "unknown" (see GetCurrentChallengeInfoAsync).
        var rawCheckpoints = mapInfo.TryGetValue("NbCheckpoints", out var cp) ? (int)cp : -1;
        var nbCheckpoints = rawCheckpoints >= 0 ? Math.Max(rawCheckpoints - 1, 0) : (int?)null;

        var fileName = mapInfo.TryGetValue("FileName", out var fn) ? fn as string : null;
        var lapRace = mapInfo.TryGetValue("LapRace", out var lr) && (bool)lr;
        var nbLaps = mapInfo.TryGetValue("NbLaps", out var nl) ? (int)nl : 0;
        var authorTime = mapInfo.TryGetValue("AuthorTime", out var at) ? (int)at : 0;
        var goldTime = mapInfo.TryGetValue("GoldTime", out var gt) ? (int)gt : 0;
        var silverTime = mapInfo.TryGetValue("SilverTime", out var st) ? (int)st : 0;
        var bronzeTime = mapInfo.TryGetValue("BronzeTime", out var bt) ? (int)bt : 0;
        return new ChallengeSummary((string)mapInfo["Name"], fileName, nbCheckpoints, lapRace, nbLaps, authorTime, goldTime, silverTime, bronzeTime);
    }

    // GetChallengeList returns every map currently in the server's selection (playlist) - the same
    // list InsertChallenge checks a challenge's UID against before erroring "already added". That
    // selection only ever grows (nothing in this app removes from it), so once a given TMX map has
    // been inserted once, it stays there for the rest of the server's uptime. Used to detect that a
    // map is already present so callers can reuse the existing entry instead of retrying InsertChallenge
    // forever.
    //
    // Matched by the TMX id marker every inserted map carries in its name ("_tmx<id>_"), or by the
    // exact bare file name (maps written before the marker existed). It used to match any name
    // that merely ENDED with the TMX file name, so "Race.Challenge.Gbx" found "A01-Race.Challenge.Gbx"
    // - a different map, which then got this id written onto it.
    public async Task<string?> FindSelectedChallengeFileNameAsync(int trackId, string fileName, CancellationToken cancellationToken = default)
    {
        var marker = $"_tmx{trackId.ToString(CultureInfo.InvariantCulture)}_";
        var list = await TracedCallAsync<List<object>>("GetChallengeList", [1000, 0], cancellationToken);

        return list.OfType<Dictionary<string, object>>()
            .Select(c => c.TryGetValue("FileName", out var f) ? f as string : null)
            .OfType<string>()
            .FirstOrDefault(f =>
            {
                var bareName = Path.GetFileName(f.Replace('\\', '/'));
                return bareName.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    || bareName.Equals(fileName, StringComparison.OrdinalIgnoreCase);
            });
    }

    // Every handler ever registered, so a reconnect can re-attach them to the new connection.
    // Raw.On binds to one XmlRpcClient instance; without this registry a reconnect would come
    // back silently deaf - connected, authenticated, and handling nothing.
    private readonly List<(string Method, Func<object[], CancellationToken, Task> Handler)> callbackHandlers = [];

    // A stalled callback stream is otherwise invisible: requests keep working, the socket stays
    // open, nothing throws. These two are the only direct evidence that callbacks are flowing.
    public long CallbacksReceived { get; private set; }

    public DateTimeOffset LastCallbackAt { get; private set; } = DateTimeOffset.UtcNow;

    public void On(string methodName, Func<object[], CancellationToken, Task> handler)
    {
        callbackHandlers.Add((methodName, handler));
        Attach(methodName, handler);
    }

    // Handlers per callback name the SERVER sends, for this connection. On TM2/TM2020 several TMF
    // callbacks come out of one server callback (ManiaPlanet.ModeScriptCallbackArray), so the
    // server callback is registered once and fans out.
    private Dictionary<string, List<Func<object[], CancellationToken, Task>>> serverCallbacks = [];

    private void Attach(string methodName, Func<object[], CancellationToken, Task> handler)
    {
        var (serverName, adapted) = IsManiaPlanet ? AdaptManiaPlanetCallback(methodName, handler) : (methodName, handler);
        var guarded = Guard(methodName, adapted);

        if (!serverCallbacks.TryGetValue(serverName, out var handlers))
        {
            serverCallbacks[serverName] = handlers = [];
            Raw.On(serverName, async (parameters, cancellationToken) =>
            {
                CallbacksReceived++;
                LastCallbackAt = DateTimeOffset.UtcNow;

                foreach (var h in handlers.ToArray())
                {
                    await h(parameters, cancellationToken);
                }
            });
        }

        handlers.Add(guarded);
    }

    // TMF callback name -> the TM2/TM2020 callback it comes from, with its parameters in TMF shape
    private (string ServerName, Func<object[], CancellationToken, Task> Handler) AdaptManiaPlanetCallback(
        string tmfName, Func<object[], CancellationToken, Task> handler)
    {
        const string script = "ManiaPlanet.ModeScriptCallbackArray";

        return tmfName switch
        {
            // a line starting with "/" is a command there (no AddChatCommand)
            "TrackMania.PlayerChat" => ("ManiaPlanet.PlayerChat", (p, ct) =>
                handler([p[0], p[1], p[2], p[2] is string text && text.StartsWith('/')], ct)),
            // actions are strings there; the controller's are numbers
            "TrackMania.PlayerManialinkPageAnswer" => ("ManiaPlanet.PlayerManialinkPageAnswer", (p, ct) =>
                p.Length > 2 && int.TryParse(p[2] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var action)
                    ? handler([p[0], p[1], action], ct)
                    : Task.CompletedTask),
            // ManiaPlanet.BeginMap does not come on RestartMap (TM2020); StartMap_Start always does
            "TrackMania.BeginRace" => (script, async (p, ct) =>
            {
                if (ScriptCallback(p, "Maniaplanet.StartMap_Start", out _))
                {
                    finishes.Clear();
                    var mapInfo = await TracedCallAsync<Dictionary<string, object>>("GetCurrentChallengeInfo", [], ct);
                    await handler([mapInfo], ct);
                }
            }),
            "TrackMania.EndRace" => (script, (p, ct) =>
                ScriptCallback(p, "Maniaplanet.EndMap_Start", out _) ? handler([], ct) : Task.CompletedTask),
            "TrackMania.EndRound" => (script, (p, ct) =>
                ScriptCallback(p, "Maniaplanet.EndRound_Start", out _) ? handler([], ct) : Task.CompletedTask),
            "TrackMania.PlayerFinish" => (script, (p, ct) =>
            {
                if (WayPoint(p) is { IsEndRace: true } w)
                {
                    finishes.Add(w.Login, w.RaceTime);
                    return handler([0, w.Login, w.RaceTime], ct);
                }

                return Task.CompletedTask;
            }),
            "TrackMania.PlayerCheckpoint" => (script, (p, ct) =>
                WayPoint(p) is { IsEndRace: false } w
                    ? handler([0, w.Login, w.RaceTime, w.Lap, w.CheckpointInRace], ct)
                    : Task.CompletedTask),
            _ when tmfName.StartsWith("TrackMania.", StringComparison.Ordinal) =>
                ("ManiaPlanet." + tmfName["TrackMania.".Length..], handler),
            _ => (tmfName, handler),
        };
    }

    private sealed record WayPointEvent(string Login, int RaceTime, int Lap, int CheckpointInRace, bool IsEndRace);

    // Trackmania.Event.WayPoint: every checkpoint and the finish, with "isendrace" on the finish
    private static WayPointEvent? WayPoint(object[] parameters)
    {
        if (!ScriptCallback(parameters, "Trackmania.Event.WayPoint", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var login = data.TryGetProperty("login", out var l) ? l.GetString() : null;
        if (string.IsNullOrEmpty(login))
        {
            return null;
        }

        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var isEndRace = data.TryGetProperty("isendrace", out var end) && end.ValueKind == JsonValueKind.True;
        // TMF's CurLap is not used by any handler; the race checkpoint index is what counts
        return new WayPointEvent(login, Int(data, "racetime"), 0, Int(data, "checkpointinrace"), isEndRace);
    }

    private static bool ScriptCallback(object[] parameters, string name, out JsonElement data)
    {
        data = default;

        if (parameters.Length == 0 || parameters[0] is not string callbackName || callbackName != name)
        {
            return false;
        }

        var json = parameters.Length > 1
            ? parameters[1] switch
            {
                string single => single,
                IEnumerable<object> list => list.FirstOrDefault() as string,
                _ => null,
            }
            : null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // a payload we can't read still counts as the event happening
            }
        }

        return true;
    }

    // ManiaAPI dispatches callbacks from a single loop: it reads one message off an unbounded
    // channel, awaits every handler registered for it, and only then reads the next. So a handler
    // that blocks does not delay its own event - it silently freezes the whole stream. Chat
    // commands, widgets, checkpoint HUDs, auto-skip and map picks all stop, while request/response
    // keeps working on its own task, so the status page and status.json still look perfectly
    // healthy. That is the "server randomly stops working" failure, and it hides well: the queue
    // just grows, then flushes all at once when whatever was blocking finally lets go.
    //
    // Every slow thing a handler does is bounded individually now (HTTP budgets in
    // CreateHttpClient, the map search in TmxRules), but those are policies and policies drift.
    // This is the structural guarantee underneath them: no handler, for any reason, holds the
    // stream for longer than this.
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);

    // Well above a healthy handler (milliseconds, or a few seconds for a map advance) and well
    // below the cutoff, so the log names the culprit before it becomes an outage.
    private static readonly TimeSpan SlowHandlerWarning = TimeSpan.FromSeconds(15);

    private Func<object[], CancellationToken, Task> Guard(string methodName, Func<object[], CancellationToken, Task> handler)
    {
        return async (parameters, cancellationToken) =>
        {
            using var handlerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handlerTimeout.CancelAfter(HandlerTimeout);

            var startedAt = Stopwatch.GetTimestamp();
            var handlerId = Diagnostics.HandlerStarted(methodName);
            Diagnostics.Trace($"cb> {methodName}");

            try
            {
                await handler(parameters, handlerTimeout.Token);
            }
            catch (OperationCanceledException) when (handlerTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Handlers catch their own exceptions, so this rarely surfaces here - the point is
                // that the token is cancelled either way and the loop moves on.
                Console.WriteLine($"ERROR: the {methodName} handler hit the {HandlerTimeout.TotalSeconds:0}s cutoff and was dropped to keep the callback stream moving.");
            }
            finally
            {
                Diagnostics.HandlerFinished(handlerId);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                Diagnostics.Trace($"cb< {methodName} {elapsed.TotalMilliseconds:0}ms");

                if (elapsed >= SlowHandlerWarning)
                {
                    Console.WriteLine($"Warning: the {methodName} handler held the callback stream for {elapsed.TotalSeconds:0.0}s - every other callback was queued behind it.");
                    Diagnostics.Dump($"{methodName} handler held the callback stream for {elapsed.TotalSeconds:0.0}s", recentLines: 150);
                }
                else if (elapsed >= SlowRpcThreshold)
                {
                    Console.WriteLine($"[diag] slow handler: {methodName} took {elapsed.TotalSeconds:0.0}s");
                }
            }
        };
    }

    // Rebuilds the connection in place, keeping the process - and everyone racing on it - alive.
    // The failure this exists for leaves the socket healthy and request/response working while
    // callbacks quietly stop, so redialling is the only way back. It costs seconds, where the
    // alternative was waiting for an empty server and restarting.
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var previous = raw;
        raw = null;

        if (previous is not null)
        {
            try
            {
                // bounded: the old connection may be waiting for a handler that is itself stuck
                await previous.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: disposing the stalled connection failed - {ex.Message}");
            }
        }

        try
        {
            await ConnectAsync(cancellationToken);

            // Handlers first, callbacks second: the other way round, every callback that arrived
            // between EnableCallbacks and the re-attach had no handler and was dropped.
            foreach (var (method, handler) in callbackHandlers)
            {
                Attach(method, handler);
            }

            await AuthenticateAsync(cancellationToken);
            await EnableCallbacksAsync(cancellationToken);
        }
        catch
        {
            // A half-built connection (connected, but not authenticated or without callbacks)
            // would look connected and stay deaf. Drop it so the caller sees "not connected"
            // and simply tries again.
            if (raw is { } broken)
            {
                raw = null;
                try
                {
                    await broken.DisposeAsync();
                }
                catch (Exception)
                {
                    // already failing - the original error is the one that matters
                }
            }

            throw;
        }

        // Whether TMF ties chat commands to the connection that added them is not documented, so
        // register them again - a duplicate is harmless, a missing one makes every command dead.
        if (chatCommands.Count > 0)
        {
            try
            {
                await SystemMulticallAsync(chatCommands.Select(cmd => new XmlRpcCall("AddChatCommand", [cmd])), cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: re-registering chat commands after the reconnect failed - {ex.Message}");
            }
        }

        LastCallbackAt = DateTimeOffset.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        if (raw is not null)
        {
            await raw.DisposeAsync();
            raw = null;
        }
        supportedMethods = null;
        versionInfo = null;
    }

    public void Dispose()
    {
        raw?.Dispose();
        raw = null;
        supportedMethods = null;
        versionInfo = null;
    }
}
