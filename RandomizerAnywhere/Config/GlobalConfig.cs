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
    public Dictionary<string, object> TmxQuery { get; set; } = [];
}
