using ManiaAPI.XmlRpc;
using Polly;
using Polly.Retry;
using RandomizerAnywhere.Config;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Linq;

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

    public async Task<object?> TracedCallAsync(string method, object[] parameters, CancellationToken cancellationToken = default)
    {
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

        var result = await TracedCallAsync<bool>("EnableCallbacks", [true], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to enable callbacks.");
        }

        return result;
    }

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

    public async Task<IEnumerable<string>> GetChatCommandListAsync(CancellationToken cancellationToken = default)
    {
        var commandList = await TracedCallAsync<List<object>>("GetChatCommandList", [(int)short.MaxValue, 0], cancellationToken);
        return commandList.OfType<IReadOnlyDictionary<string, object>>()
            .Select(x => (string)x["Name"]);
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
            .Select(p => new PlayerSummary((string)p["Login"], (string)p["NickName"]))
            .ToList();
    }

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

    public async Task SendManialinkPageAsync(string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        await TracedCallAsync("SendDisplayManialinkPage", [manialink, timeoutSeconds, hideOnClick], cancellationToken);
    }

    public async Task SendManialinkPageToLoginAsync(string login, string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        await TracedCallAsync("SendDisplayManialinkPageToLogin", [login, manialink, timeoutSeconds, hideOnClick], cancellationToken);
    }

    public async Task HideAllManialinksAsync(CancellationToken cancellationToken = default)
    {
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
    public async Task<string?> FindSelectedChallengeFileNameAsync(string fileNameSuffix, CancellationToken cancellationToken = default)
    {
        var list = await TracedCallAsync<List<object>>("GetChallengeList", [1000, 0], cancellationToken);
        return list.OfType<Dictionary<string, object>>()
            .Select(c => c.TryGetValue("FileName", out var f) ? f as string : null)
            .FirstOrDefault(f => f is not null && f.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase));
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

    private void Attach(string methodName, Func<object[], CancellationToken, Task> handler)
    {
        Raw.On(methodName, async (parameters, cancellationToken) =>
        {
            CallbacksReceived++;
            LastCallbackAt = DateTimeOffset.UtcNow;

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
        });
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
                await previous.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: disposing the stalled connection failed - {ex.Message}");
            }
        }

        await ConnectAsync(cancellationToken);
        await AuthenticateAsync(cancellationToken);
        await EnableCallbacksAsync(cancellationToken);

        foreach (var (method, handler) in callbackHandlers)
        {
            Attach(method, handler);
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
