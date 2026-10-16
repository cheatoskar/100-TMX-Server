using RandomizerAnywhere.Config;
using Tomlyn;

namespace RandomizerAnywhere;

internal static class TomlLoader
{
    public static GlobalConfig LoadGlobalConfig(string path)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        // A config.toml that doesn't parse is fatal. Carrying on with defaults meant an empty
        // AdminLogins (everyone is admin), an empty DiscordWebhookUrl, LAN mode off/on by
        // accident - a server that looks fine and behaves wrongly.
        try
        {
            var tomlText = File.ReadAllText(path);
            return TomlSerializer.Deserialize<GlobalConfig>(tomlText) ?? throw new ConfigException($"'{path}' is empty.");
        }
        catch (TomlException ex)
        {
            throw new ConfigException($"failed to parse '{path}': {ex.Message}", ex);
        }
    }

    public static PresetConfig? LoadPresetConfig(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var tomlText = File.ReadAllText(path);
            return TomlSerializer.Deserialize<PresetConfig>(tomlText);
        }
        catch (TomlException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Warning: failed to parse '{path}': {ex.Message}");
            Console.WriteLine("Falling back to defaults.");
            Console.ResetColor();
            return null;
        }
    }
}
