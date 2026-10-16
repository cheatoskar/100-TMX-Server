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
    public required IReadOnlyDictionary<string, object> TmxQuery { get; set; }
    public required string? TmxQueryOverride { get; set; }
    public required bool DedicatedServerMode { get; init; }
    public required bool SkipSetup { get; init; }
    public required AutoSkipMode AutoSkipMode { get; set; }
    public required TimeInt32 TimeLimit { get; set; }
    public required bool CallVoteOnFinish { get; set; }
    public required string[] WelcomeMessage { get; set; }
    public required string ServerName { get; set; }
    public required string ServerComment { get; init; }
    public required string GameSettings { get; init; }
    public bool SourceFromImpossibleList { get; set; }
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
