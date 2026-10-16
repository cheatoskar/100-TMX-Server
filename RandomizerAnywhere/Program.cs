using Jab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using RandomizerAnywhere;
using RandomizerAnywhere.Config;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using var cts = new CancellationTokenSource();

// Exit codes systemd sees. Anything non-zero is restarted (Restart=always); 78 (EX_CONFIG) is the
// one a restart can't fix, so the journal should say so plainly.
const int ExitConfigError = 78;
const int ExitUnexpectedError = 1;

// SIGTERM only asks for a shutdown. The handler used to cancel AND stop the dedicated server
// right there on the signal thread, racing Main's own finally doing the same; an exception
// escaping on that thread aborted the runtime ("Cannot print exception string", SIGABRT, core
// dump - twice on a plain `systemctl stop`). Main's finally is now the only place that tears down.
using var sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    _ = Task.Run(cts.Cancel);
});

var provider = new AppServiceProvider();
ServerSetup? serverSetup = null;

try
{
    AppConfig appConfig;
    try
    {
        appConfig = provider.GetRequiredService<AppConfig>();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Anything that goes wrong while reading the configuration (a broken file, a wrong value
        // type, an unknown [DownloadUrls] key, a missing config.default.toml) is not fixed by
        // starting again - exit 78 so systemd stops instead of looping through the whole setup.
        Console.WriteLine($"ERROR: {(ex is ConfigException ? ex.Message : $"couldn't load the configuration - {ex.GetType().Name}: {ex.Message}")}");
        Console.WriteLine("Fix the configuration and start again - restarting won't help until then.");
        return ExitConfigError;
    }

    var impossibleMaps = provider.GetRequiredService<ImpossibleMaps>();
    await impossibleMaps.LoadAsync(cts.Token);
    _ = impossibleMaps.RunPeriodicRefreshAsync(TimeSpan.FromHours(24), cts.Token);

    var skippedMaps = provider.GetRequiredService<SkippedMaps>();
    await skippedMaps.LoadAsync(cts.Token);

    var leaderboard = provider.GetRequiredService<Leaderboard>();
    await leaderboard.LoadAsync(cts.Token);

    serverSetup = provider.GetRequiredService<ServerSetup>();
    await serverSetup.TrySetupAsync(cts.Token);

    if (appConfig.DedicatedServerMode)
    {
        var statusDir = Path.Combine(AppContext.BaseDirectory, "WebStatus");
        var replayServer = new ReplayServer(serverSetup.ReplaysDir, statusDir, appConfig.ReplayServerPort, appConfig.ServerName, appConfig.FavoriteLogin);

        // The web page (replays, status.json) is an extra - a port already in use must not stop
        // the game server, or it crash-loops through the whole setup every 5 seconds.
        try
        {
            await replayServer.StartAsync();
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            Console.WriteLine($"ERROR: the web server on port {appConfig.ReplayServerPort} could not start - replay links and the status page are off until the next restart. {ex.Message}");
        }
    }

    await Task.Delay(1000, cts.Token);

    const int maxSetupAttempts = 3;
    var setupCompleted = false;

    for (var attempt = 1; attempt <= maxSetupAttempts; attempt++)
    {
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        setupCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            Console.WriteLine($"Starting controller setup (attempt {attempt}/{maxSetupAttempts})...");
            var randomizerSetup = provider.GetRequiredService<RandomizerSetup>();
            await randomizerSetup.RunAsync(setupCts.Token);
            setupCompleted = true;
            break;
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested && setupCts.IsCancellationRequested)
        {
            Console.WriteLine($"ERROR: Controller setup timed out after 30s on attempt {attempt}/{maxSetupAttempts}.");
            var client = provider.GetRequiredService<RemoteClient>();
            await client.DisposeAsync();
            if (attempt < maxSetupAttempts)
            {
                await Task.Delay(2000, cts.Token);
            }
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            Console.WriteLine($"ERROR: Controller setup failed on attempt {attempt}/{maxSetupAttempts}: {ex.Message}");
            var client = provider.GetRequiredService<RemoteClient>();
            await client.DisposeAsync();
            if (attempt < maxSetupAttempts)
            {
                await Task.Delay(2000, cts.Token);
            }
        }
    }

    if (!setupCompleted && !cts.IsCancellationRequested)
    {
        throw new InvalidOperationException("Controller XML-RPC setup failed after multiple attempts.");
    }

    var randomizerGame = provider.GetRequiredService<RandomizerGame>();
    await randomizerGame.RunAsync(cts.Token);
    return 0;
}
catch (Exception) when (cts.IsCancellationRequested)
{
    // A shutdown was asked for (systemd stop). Whatever is thrown on the way out - a cancelled
    // call, the XML-RPC socket dropping while the dedicated server goes down, "not connected" -
    // is part of stopping, not a failure. Only IOException/SocketException used to be excused;
    // anything else became an unhandled exception and a core dump on a plain stop.
    return 0;
}
catch (Exception ex)
{
    // One readable line instead of an unhandled-exception abort and a core dump; systemd
    // restarts us either way.
    Console.WriteLine($"ERROR: the controller stopped on an unexpected error - {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return ExitUnexpectedError;
}
finally
{
    serverSetup?.StopServer();
}

[ServiceProvider]
[Singleton(typeof(HttpClient), Factory = nameof(CreateHttpClient))]
[Singleton(typeof(AppConfig), Factory = nameof(CreateAppConfig))]
// Singleton: the recently-played list and the /imp session exclusions are per process, not per
// consumer - as a transient, ServerSetup's warmup pick and the game each had their own and the
// game could pick the warmup map again right away.
[Singleton(typeof(TmxRules))]
[Transient(typeof(ServerSetup))]
[Transient(typeof(RandomizerSetup))]
[Singleton(typeof(RemoteClient))]
[Transient(typeof(RandomizerGame))]
[Singleton(typeof(ImpossibleMaps))]
[Singleton(typeof(SkippedMaps))]
[Singleton(typeof(DiscordNotifier))]
[Singleton(typeof(Leaderboard))]
[Singleton(typeof(MapPool))]
internal partial class AppServiceProvider
{
    // Every TMX call on this client runs on the XML-RPC callback dispatch loop, which handles
    // callbacks strictly one at a time and awaits each handler before reading the next message.
    // So whatever budget is set here is also how long a single slow TMX request can freeze the
    // ENTIRE controller - widgets, chat commands, auto-skip, all of it. This used to be
    // MaxRetryAttempts = int.MaxValue with no timeout strategy at all, which let one struggling
    // tm-exchange.com request retry until HttpClient's 100s default killed it, per attempt, over
    // and over. Measured live on 2026-09-11: 19 minutes of a completely deaf controller, then the
    // whole queued backlog flushed in a single second.
    //
    // Retry over timeout (AddRetry added first = outermost) so the per-attempt ceiling applies to
    // each try rather than to all of them together, hard-capped by HttpClient.Timeout. A TMX
    // outage now fails fast and loudly instead of stalling the game.
    private static readonly TimeSpan HttpAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HttpTotalTimeout = TimeSpan.FromSeconds(40);

    // Says who is asking, so TMX can tell this server apart (and reach its owner) instead of
    // seeing an anonymous .NET client.
    private const string UserAgent = "RandomizerAnywhere-100TMX (+https://github.com/cheatoskar/randomizer-anywhere-100--tmx)";

    public static HttpClient CreateHttpClient()
    {
        var httpResilienceOptions = new HttpStandardResilienceOptions();
        httpResilienceOptions.Retry.MaxRetryAttempts = 2;
        httpResilienceOptions.Retry.Delay = TimeSpan.FromSeconds(1);

        var httpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(httpResilienceOptions.Retry)
            .AddTimeout(HttpAttemptTimeout)
            .Build();

        var client = new HttpClient(new ResilienceHandler(httpRetryPipeline)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AllowAutoRedirect = false
            }
        })
        {
            // The absolute ceiling for one request including its retries. Without it the only
            // bound is HttpClient's 100s default, which applies per attempt and is far too long
            // to sit on the callback loop.
            Timeout = HttpTotalTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static AppConfig CreateAppConfig()
    {
        var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "config.default.toml");
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");

        if (!File.Exists(configPath))
        {
            File.Copy(defaultConfigPath, configPath);
        }

        // throws ConfigException on a broken file - see TomlLoader
        var globalConfig = TomlLoader.LoadGlobalConfig(configPath);
        var cmdConfig = CmdConfig.Parse(Environment.GetCommandLineArgs());
        var appConfig = new AppConfig
        {
            Game = Configurator.GetOrAskEnum(globalConfig.Game, cmdConfig.Game, "RANDANY_GAME", "a game"),
            TmxGame = Configurator.GetOptionalEnum(globalConfig.TmxGame, cmdConfig.TmxGame, "RANDANY_TMX_GAME"),
            BindIP = Configurator.GetIP(globalConfig.BindIP, cmdConfig.BindIP, "RANDANY_BIND_IP"),
            XmlRpcPort = Configurator.GetNumber(globalConfig.XmlRpcPort, cmdConfig.XmlRpcPort, "RANDANY_XMLRPC_PORT"),
            AutoSkipMode = Configurator.GetEnum<AutoSkipMode>(globalConfig.AutoSkipMode, cmdValue: null, "RANDANY_AUTO_SKIP_MODE"),
            DownloadUrls = globalConfig.DownloadUrls
                .ToDictionary(
                    x => Enum.Parse<DedicatedServerType>(x.Key, ignoreCase: true),
                    x => x.Value),
            TmxQuery = globalConfig.TmxQuery,
            TmxQueryOverride = cmdConfig.TmxQuery,
            DedicatedServerMode = Configurator.GetBool(cfgValue: null, cmdConfig.DedicatedServerMode, "RANDANY_DEDICATED"),
            SkipSetup = Configurator.GetBool(cfgValue: null, cmdConfig.SkipSetup, "RANDANY_SKIP_SETUP"),
            TimeLimit = new(globalConfig.TimeLimit),
            CallVoteOnFinish = Configurator.GetBool(globalConfig.CallVoteOnFinish, cmdValue: null, "RANDANY_CALLVOTE_ON_FINISH"),
            WelcomeMessage = globalConfig.WelcomeMessage.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            ServerName = Configurator.GetString(globalConfig.ServerName, cmdConfig.ServerName, "RANDANY_SERVER_NAME"),
            ServerComment = Configurator.GetString(globalConfig.ServerComment, cmdValue: null, "RANDANY_SERVER_COMMENT"),
            GameSettings = Configurator.GetString(globalConfig.GameSettings, cmdValue: null, "RANDANY_GAMESETTINGS"),
            AutoStart = Configurator.GetBool(globalConfig.AutoStart, cmdValue: null, "RANDANY_AUTO_START"),
            AdminLogins = Configurator.GetString(globalConfig.AdminLogins, cmdValue: null, "RANDANY_ADMIN_LOGINS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            PublicHost = Configurator.GetString(globalConfig.PublicHost, cmdValue: null, "RANDANY_PUBLIC_HOST"),
            ReplayServerPort = Configurator.GetNumber(globalConfig.ReplayServerPort, cmdValue: null, "RANDANY_REPLAY_SERVER_PORT"),
            Lan = Configurator.GetBool(globalConfig.Lan, cmdValue: null, "RANDANY_LAN"),
            ServerPort = Configurator.GetNumber(globalConfig.ServerPort, cmdValue: null, "RANDANY_SERVER_PORT"),
            ServerP2PPort = Configurator.GetNumber(globalConfig.ServerP2PPort, cmdValue: null, "RANDANY_SERVER_P2P_PORT"),
            ServerLogin = Configurator.GetString(globalConfig.ServerLogin, cmdValue: null, "RANDANY_SERVER_LOGIN"),
            ServerPassword = Configurator.GetString(globalConfig.ServerPassword, cmdValue: null, "RANDANY_SERVER_PASSWORD"),
            ServerValidationKey = Configurator.GetString(globalConfig.ServerValidationKey, cmdValue: null, "RANDANY_SERVER_VALIDATION_KEY"),
            DiscordWebhookUrl = Configurator.GetString(globalConfig.DiscordWebhookUrl, cmdValue: null, "RANDANY_DISCORD_WEBHOOK_URL"),
            DiscordWebhookUrlHard = Configurator.GetString(globalConfig.DiscordWebhookUrlHard, cmdValue: null, "RANDANY_DISCORD_WEBHOOK_URL_HARD"),
            ConnectionUploadRate = Math.Max(globalConfig.ConnectionUploadRate, 0),
            VerboseRpc = !globalConfig.DisableVerboseRpc,
            KeepConsoleLogs = globalConfig.KeepConsoleLogs > 0 ? globalConfig.KeepConsoleLogs : 30,
        };

        if (!string.IsNullOrWhiteSpace(globalConfig.Preset))
        {
            var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", globalConfig.Preset + ".toml");

            if (File.Exists(presetPath))
            {
                var presetConfig = TomlLoader.LoadPresetConfig(presetPath)
                    ?? throw new ConfigException($"the preset '{globalConfig.Preset}' in config.toml could not be read (see the line above).");

                if (!presetConfig.IsValid(out var invalidReason))
                {
                    throw new ConfigException($"the preset '{globalConfig.Preset}' in config.toml is broken: {invalidReason}.");
                }

                presetConfig.Apply(appConfig);
                appConfig.LastPreset = presetConfig;
            }
            else
            {
                Console.WriteLine($"Warning: preset '{globalConfig.Preset}' not found - running with config.toml's own settings.");
            }
        }

        if (appConfig.AdminLogins.Count == 0)
        {
            Console.WriteLine("Warning: AdminLogins is empty - EVERY player can use the admin commands (/stop, /preset, /loadmap, /blockmap, /timelimit).");
        }

        return appConfig;
    }
}