namespace RandomizerAnywhere.Config;

// A configuration problem a restart cannot fix. Program turns it into exit code 78 (EX_CONFIG)
// with a readable message, instead of carrying on with defaults - a config.toml that failed to
// parse used to fall back to an empty AdminLogins, which made every player an admin.
internal sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
