using System.Net;
using TmEssentials;

namespace RandomizerAnywhere.Config;

internal sealed class AppConfig
{
    public required GameTitle Game { get; init; }
    public required GameTitle? TmxGame { get; init; }
    public required IPAddress? BindIP { get; init; }
    public required ushort XmlRpcPort { get; init; }
    public required Dictionary<DedicatedServerType, string> DownloadUrls { get; init; }
    public Dictionary<DedicatedServerType, string> DownloadSha256 { get; init; } = [];
    public required IReadOnlyDictionary<string, object> TmxQuery { get; set; }
    public required string? TmxQueryOverride { get; set; }
    public required bool DedicatedServerMode { get; init; }
    public required bool SkipSetup { get; init; }
    public required AutoSkipMode AutoSkipMode { get; set; }
    public required TimeInt32 TimeLimit { get; set; }
    public required bool CallVoteOnFinish { get; set; }
    public required string[] WelcomeMessage { get; set; }
    public required string ServerName { get; set; }

    // in-game name with format codes; falls back to the plain ServerName (see GlobalConfig)
    public string ServerDisplayName { get; init; } = string.Empty;
    public string InGameServerName => string.IsNullOrWhiteSpace(ServerDisplayName) ? ServerName : ServerDisplayName;
    public required string ServerComment { get; init; }
    public required string GameSettings { get; init; }
    public bool SourceFromImpossibleList { get; set; }

    // the preset draws from the event list's open maps (see EventMode), not from TmxQuery
    public bool SourceFromEventList { get; set; }

    public bool EventEnabled { get; init; }
    public string EventMapsFile { get; init; } = string.Empty;
    public string EventStateFile { get; init; } = string.Empty;
    public string EventServerLabel { get; init; } = string.Empty;
    public bool EventUnlimiter { get; init; }
    public bool EventMapsFromTmx { get; init; }
    public string EventTitle { get; init; } = string.Empty;
    public int EventTmxRefreshMinutes { get; init; } = 5;
    public IReadOnlyDictionary<string, object> EventTmxQuery { get; init; } = new Dictionary<string, object>();
    public string EventAfterPreset { get; init; } = string.Empty;

    // "skip", "only" or "include" (see GlobalConfig.EventUnlimiterMaps)
    public string EventUnlimiterMaps { get; init; } = "skip";
    public bool EventEveryday { get; init; }

    // see GlobalConfig.SyncHubUrl: set = this server uses the hub's event list and leaderboard
    public string SyncHubUrl { get; init; } = string.Empty;
    public string SyncToken { get; init; } = string.Empty;
    public string SyncAllowedIps { get; init; } = string.Empty;

    // who may be voted for with /votemap: "any" TMX map, or only the event list's open maps
    // ("event"). Set by the preset, so the event preset can restrict it and every other one doesn't.
    public string MapVoteScope { get; set; } = "any";

    // set by a preset with LockPreset = true (the event): no preset votes until an admin switches
    public bool PresetLocked { get; set; }
    public required bool AutoStart { get; init; }
    public required IReadOnlySet<string> AdminLogins { get; init; }
    public required string PublicHost { get; init; }
    public required ushort ReplayServerPort { get; init; }
    public required bool Lan { get; init; }
    public required ushort ServerPort { get; init; }
    public required ushort ServerP2PPort { get; init; }
    public required string ServerLogin { get; init; }
    public required string ServerPassword { get; init; }
    public required string ServerValidationKey { get; init; }
    public string ServerNation { get; init; } = string.Empty;

    // The 2006 TM dedicated server (ESWC, Sunrise, Original) rather than TMF. It has no manialinks,
    // no server-saved ghost replays and only a handful of callbacks - no PlayerFinish, PlayerCheckpoint
    // or StatusChanged - so finishes are polled from the ranking instead (see RandomizerGame).
    public bool IsLegacyServer => Game is GameTitle.TMN or GameTitle.TMS or GameTitle.TMO;

    // TM2 (ManiaPlanet) and TM2020: one server generation - maps instead of challenges, the game
    // mode is a script, manialinks are version 3 (see RemoteClient, ManialinkV3)
    public bool IsManiaPlanet => Game is GameTitle.TM2 or GameTitle.TM2020;

    // TM2 only: the title the server runs - TMStadium@nadeo, TMCanyon@nadeo, TMValley@nadeo or
    // TMLagoon@nadeo. Players need the same title to join.
    public string TitlePack { get; init; } = "TMStadium@nadeo";

    // map files: .Map.Gbx on TM2/TM2020, .Challenge.Gbx before
    public string MapFileExtension => IsManiaPlanet ? ".Map.Gbx" : ".Challenge.Gbx";

    // leaderboard.json to use; empty = the one next to the program. The TMUF server points this at
    // the TMNF server's file, so both servers count into one leaderboard (see Leaderboard).
    public string LeaderboardFile { get; init; } = string.Empty;

    // The login players add to their favourites (tmtp://#addfavourite=<login>). The first server's
    // login predates the config key, so it stays the fallback.
    public string FavoriteLogin => string.IsNullOrWhiteSpace(ServerLogin) ? "100_tmx-project" : ServerLogin;

    // the TMX exchange this server draws maps from
    public GameTitle EffectiveTmxGame => TmxGame ?? Game;
    public required string DiscordWebhookUrl { get; init; }
    public required string DiscordWebhookUrlHard { get; init; }
    public PresetConfig? LastPreset { get; set; }

    public int ConnectionUploadRate { get; init; }
    public bool VerboseRpc { get; init; } = true;
    public int KeepConsoleLogs { get; init; } = 30;
}
