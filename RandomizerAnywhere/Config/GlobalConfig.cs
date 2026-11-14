namespace RandomizerAnywhere.Config;

internal sealed class GlobalConfig
{
    public string Game { get; set; } = string.Empty;
    public string TmxGame { get; set; } = string.Empty;
    public string BindIP { get; set; } = string.Empty;
    public ushort XmlRpcPort { get; set; }
    public string AutoSkipMode { get; set; } = string.Empty;
    public int TimeLimit { get; set; }
    public bool CallVoteOnFinish { get; set; }
    public string GameSettings { get; set; } = string.Empty;

    public string Preset { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    // The name shown in the game's server list, with TM format codes ($0BF, $s, ...). Optional:
    // empty = ServerName. ServerName itself stays plain text - it is also the replay folder (the
    // download links point into it) and what status.json / the website show.
    public string ServerDisplayName { get; set; } = string.Empty;
    public string ServerComment { get; set; } = string.Empty;
    public string WelcomeMessage { get; set; } = string.Empty;

    public bool AutoStart { get; set; }
    public string AdminLogins { get; set; } = string.Empty;

    public string PublicHost { get; set; } = string.Empty;
    public ushort ReplayServerPort { get; set; }
    public bool Lan { get; set; }

    // Per-instance game server settings, so a second server (TMUF) can run next to the first one
    // from its own folder. 0 / empty = leave whatever dedicated_cfg.txt already has.
    public ushort ServerPort { get; set; }
    public ushort ServerP2PPort { get; set; }
    public string ServerLogin { get; set; } = string.Empty;
    public string ServerPassword { get; set; } = string.Empty;
    public string ServerValidationKey { get; set; } = string.Empty;

    // Old TM server (ESWC/Sunrise/Original) only: the nation its master server account is
    // registered under. For Nations the account is created on the first online start.
    public string ServerNation { get; set; } = string.Empty;

    // Path of the leaderboard file. Empty = leaderboard.json next to the program. Two servers that
    // point at the same file share one leaderboard (writes are locked across processes).
    public string LeaderboardFile { get; set; } = string.Empty;
    public string DiscordWebhookUrl { get; set; } = string.Empty;
    public string DiscordWebhookUrlHard { get; set; } = string.Empty;

    // The dedicated server's whole upload budget in kbit/s, written into dedicated_cfg.txt.
    // 0 = leave the file as it is (TMF's template ships 512, i.e. ~64 KB/s for everyone, which
    // is also what every joining player downloads the random map through).
    public int ConnectionUploadRate { get; set; }

    // /verbose_rpc logs every XML-RPC call in the dedicated server's ConsoleLog - the only record
    // of whether a call the controller sent actually reached the server. On by default.
    public bool DisableVerboseRpc { get; set; }

    // How many of the dedicated server's ConsoleLog.<pid>.txt files to keep (one per start,
    // 15-30 MB each with /verbose_rpc). 0 = the default of 30.
    public int KeepConsoleLogs { get; set; }

    public Dictionary<string, string> DownloadUrls { get; set; } = [];

    // Optional SHA-256 of the server zip per server type. The 2006 TM server comes over plain HTTP
    // from a private host and is then executed; with a hash set, a changed file is refused.
    public Dictionary<string, string> DownloadSha256 { get; set; } = [];
    public Dictionary<string, object> TmxQuery { get; set; } = [];

    // The event module (EventMode): off unless EventEnabled. Several servers on one machine run one
    // event together by pointing EventMapsFile and EventStateFile at the same files.
    public bool EventEnabled { get; set; }
    public string EventMapsFile { get; set; } = string.Empty;
    public string EventStateFile { get; set; } = string.Empty;
    public string EventServerLabel { get; set; } = string.Empty;
    public bool EventUnlimiter { get; set; }

    // "file" (the hand-made EventMapsFile) or "tmx" (every map [EventTmxQuery] finds, refreshed
    // every EventTmxRefreshMinutes so new uploads join the list)
    public string EventMapsSource { get; set; } = string.Empty;
    public string EventTitle { get; set; } = string.Empty;
    public int EventTmxRefreshMinutes { get; set; }
    public Dictionary<string, object> EventTmxQuery { get; set; } = [];

    // the preset the servers switch to once every event map is finished; empty = stay
    public string EventAfterPreset { get; set; } = string.Empty;
}
