using TmEssentials;

namespace RandomizerAnywhere.Config;

internal sealed class PresetConfig
{
    public string DisplayName { get; set; } = string.Empty;
    public string[] Games { get; init; } = [];

    public int TimeLimit { get; set; } = 0;
    public string AutoSkipMode { get; set; } = "AuthorMedal";

    public string? ServerName { get; set; }
    public string? WelcomeMessage { get; set; }

    public Dictionary<string, object> TmxQuery { get; set; } = [];

    // when true, ignore TmxQuery and instead pick randomly among the locally tracked
    // impossible/cheated map ids (for admin review, not normal play)
    public bool SourceFromImpossibleList { get; set; }

    // an empty Games list means "any game"
    public bool SupportsGame(GameTitle game) =>
        Games.Length == 0 || Games.Any(g => string.Equals(g, game.ToString(), StringComparison.OrdinalIgnoreCase));

    // Checked before a preset is switched to: Apply would otherwise throw half-way through on a
    // typo in AutoSkipMode, after the session had already been reset.
    public bool IsValid(out string? reason)
    {
        if (!Enum.TryParse<AutoSkipMode>(AutoSkipMode, ignoreCase: true, out _))
        {
            reason = $"unknown AutoSkipMode '{AutoSkipMode}'";
            return false;
        }

        reason = null;
        return true;
    }

    public void Apply(AppConfig config)
    {
        config.TimeLimit = new TimeInt32(TimeLimit);
        // IsValid is checked before every Apply, so this can't throw half-way through
        config.AutoSkipMode = Enum.TryParse<AutoSkipMode>(AutoSkipMode, ignoreCase: true, out var autoSkipMode)
            ? autoSkipMode
            : config.AutoSkipMode;

        if (ServerName is not null) config.ServerName = ServerName;
        if (WelcomeMessage is not null) config.WelcomeMessage = WelcomeMessage.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        config.TmxQuery = TmxQuery;
        config.SourceFromImpossibleList = SourceFromImpossibleList;
    }
}
