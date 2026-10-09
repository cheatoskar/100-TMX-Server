using RandomizerAnywhere.Config;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Tomlyn;

namespace RandomizerAnywhere;

internal sealed partial class ServerSetup
{
    private const string CacheFileName = ".download-cache.toml";
    private const int CopyBufferSize = 81920;

    private readonly HttpClient http;
    private readonly TmxRules tmxRules;
    private readonly AppConfig config;
    private readonly DedicatedServerType game;
    private readonly string serverDir;
    private readonly string dedicatedServerDir;
    private readonly string dedicatedExeFileName;

    private Process? serverProcess;

    public ServerSetup(HttpClient http, TmxRules tmxRules, AppConfig config)
    {
        this.http = http;
        this.tmxRules = tmxRules;
        this.config = config;

        game = config.Game switch
        {
            GameTitle.TMNF or GameTitle.TMUF => DedicatedServerType.TMF,
            GameTitle.TMN or GameTitle.TMS or GameTitle.TMO => DedicatedServerType.TM,
            _ => throw new InvalidOperationException($"Unsupported game: {config.Game}")
        };
        serverDir = Path.Combine(AppContext.BaseDirectory, "Servers", game.ToString());

        switch (game)
        {
            case DedicatedServerType.TMF:
                dedicatedServerDir = serverDir;
                dedicatedExeFileName = OperatingSystem.IsWindows() ? "TrackmaniaServer.exe" : "TrackmaniaServer";
                break;
            case DedicatedServerType.TM:
                dedicatedServerDir = Path.Combine(serverDir, "TmDedicatedServer");
                dedicatedExeFileName = OperatingSystem.IsWindows() ? "TrackManiaServer.exe" : "TrackManiaServer";
                break;
            default:
                throw new InvalidOperationException($"Unsupported server type: {game}");
        }
    }

    public string ReplaysDir => Path.Combine(dedicatedServerDir, "GameData", "Tracks", "Replays", config.ServerName);

    public async Task<bool> TrySetupAsync(CancellationToken cancellationToken = default)
    {
        if (!config.DedicatedServerMode || config.SkipSetup)
        {
            return false;
        }

        var firstTimeSetup = await SetupDedicatedServerAsync(cancellationToken);
        await SetupDedicatedCfgAsync(cancellationToken);
        await SetupMatchSettingsAsync(cancellationToken);
        StartServer(showServerWindow: firstTimeSetup);

        return true;
    }

    private async Task<bool> SetupDedicatedServerAsync(CancellationToken cancellationToken)
    {
        var downloadUrl = config.DownloadUrls.TryGetValue(game, out var url) ? url : throw new InvalidOperationException($"Download URL for {game} not found in configuration.");

        var serverDir = Path.Combine(AppContext.BaseDirectory, "Servers", game.ToString());
        var cacheFilePath = Path.Combine(serverDir, CacheFileName);

        var cachedEntry = await ReadCacheEntryAsync(cacheFilePath, cancellationToken);
        var canUseCachedEntry = cachedEntry is not null && cachedEntry.Url == downloadUrl && Directory.Exists(serverDir);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (canUseCachedEntry)
        {
            if (cachedEntry!.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var etagValue))
            {
                request.Headers.IfNoneMatch.Add(etagValue);
            }

            if (cachedEntry.LastModified is { } lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (canUseCachedEntry && response.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();

        var newEntry = new DownloadCacheEntry(
            downloadUrl,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            response.Content.Headers.ContentLength);

        // Some servers don't support conditional requests but still report ETag/Last-Modified/Content-Length,
        // so we can avoid re-downloading and re-extracting the archive if nothing actually changed.
        if (canUseCachedEntry && cachedEntry == newEntry)
        {
            return false;
        }

        Directory.CreateDirectory(serverDir);

        var tempZipPath = Path.GetTempFileName();
        try
        {
            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await DownloadWithProgressAsync(response.Content, fileStream, cancellationToken);
            }

            await ZipFile.ExtractToDirectoryAsync(tempZipPath, serverDir, overwriteFiles: true, cancellationToken);
        }
        finally
        {
            File.Delete(tempZipPath);
        }

        await WriteCacheEntryAsync(cacheFilePath, newEntry, cancellationToken);
        return true;
    }

    private async Task SetupDedicatedCfgAsync(CancellationToken cancellationToken)
    {
        var filePath = game == DedicatedServerType.TM
            ? Path.Combine(dedicatedServerDir, "dedicated.cfg")
            : Path.Combine(dedicatedServerDir, "GameData", "Config", "dedicated_cfg.txt");

        var contents = await File.ReadAllTextAsync(filePath, cancellationToken);

        if (game == DedicatedServerType.TMF)
        {
            var packmask = config.Game == GameTitle.TMNF ? "nations" : "";

            contents = PackmaskRegex().Replace(contents, $"<packmask>{packmask}</packmask>");
        }

        contents = XmlRpcPortRegex().Replace(contents, $"<xmlrpc_port>{config.XmlRpcPort}</xmlrpc_port>");

        // A second server on the same machine needs its own game and peer-to-peer ports, and its own
        // server account. Only written when configured, so the first server's file stays as it is.
        if (config.ServerPort > 0)
        {
            contents = ServerPortRegex().Replace(contents, _ => $"<server_port>{config.ServerPort}</server_port>");
        }

        if (config.ServerP2PPort > 0)
        {
            contents = ServerP2PPortRegex().Replace(contents, _ => $"<server_p2p_port>{config.ServerP2PPort}</server_p2p_port>");
        }

        if (game == DedicatedServerType.TMF)
        {
            contents = MasterServerAccountRegex().Replace(contents, block =>
            {
                var text = block.Value;

                if (!string.IsNullOrWhiteSpace(config.ServerLogin))
                {
                    text = LoginTagRegex().Replace(text, _ => $"<login>{System.Security.SecurityElement.Escape(config.ServerLogin)}</login>");
                }

                if (!string.IsNullOrWhiteSpace(config.ServerPassword))
                {
                    text = PasswordTagRegex().Replace(text, _ => $"<password>{System.Security.SecurityElement.Escape(config.ServerPassword)}</password>");
                }

                if (!string.IsNullOrWhiteSpace(config.ServerValidationKey))
                {
                    text = ValidationKeyTagRegex().Replace(text, _ => $"<validation_key>{System.Security.SecurityElement.Escape(config.ServerValidationKey)}</validation_key>");
                }

                return text;
            });
        }
        // Off on purpose: a changing seed perturbs the physics per run, so press-forward lines drift and
        // blockmixed starts behave differently than offline. Server-saved ghost replays don't need it.
        contents = ValidationSeedRegex().Replace(contents, "<use_changing_validation_seed>False</use_changing_validation_seed>");

        await File.WriteAllTextAsync(filePath, contents, cancellationToken);
    }

    private async Task SetupMatchSettingsAsync(CancellationToken cancellationToken)
    {
        var matchSettingsFilePath = Path.Combine(dedicatedServerDir, "GameData", "Tracks", config.GameSettings);

        var warmupChallengeGbxFile = await tmxRules.NextMapGbxAsync(cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(dedicatedServerDir, "GameData", "Tracks", warmupChallengeGbxFile.FileName), warmupChallengeGbxFile.Data, cancellationToken);

        string matchSettingsXml;

        if (game == DedicatedServerType.TM)
        {
            matchSettingsXml = @$"
<?xml version=""1.0"" encoding=""utf-8"" ?>
<playlist>
    <gameinfos>
        <game_mode>1</game_mode>
        <chat_time>0</chat_time>
        <rounds_pointslimit>2</rounds_pointslimit>
        <rounds_usenewrules>1</rounds_usenewrules>
        <timeattack_limit>600000</timeattack_limit>
        <timeattack_synchstartperiod>0</timeattack_synchstartperiod>
        <team_pointslimit>5</team_pointslimit>
        <team_maxpoints>6</team_maxpoints>
        <team_usenewrules>0</team_usenewrules>
        <laps_nblaps>5</laps_nblaps>
        <laps_timelimit>0</laps_timelimit>
    </gameinfos>

    <hotseat>
        <game_mode>1</game_mode>
        <timeattack_limit>3</timeattack_limit>
        <rounds_count>5</rounds_count>
    </hotseat>

    <filter>
        <is_solo>0</is_solo>
        <is_hotseat>0</is_hotseat>
        <is_lan>0</is_lan>
        <is_internet>1</is_internet>
        <sort_index>200</sort_index>
        <random_map_order>1</random_map_order>
    </filter>

    <challenge>
        <file>{warmupChallengeGbxFile.FileName}</file>
    </challenge>
</playlist>
        ";
        }
        else
        {
            matchSettingsXml = @$"
<?xml version=""1.0"" encoding=""utf-8"" ?>
<playlist>
	<gameinfos>
		<game_mode>1</game_mode>
		<chat_time>0</chat_time>
		<finishtimeout>1</finishtimeout>
		<allwarmupduration>0</allwarmupduration>
		<disablerespawn>0</disablerespawn>
		<forceshowallopponents>0</forceshowallopponents>
		<rounds_pointslimit>30</rounds_pointslimit>
		<rounds_usenewrules>0</rounds_usenewrules>
		<rounds_forcedlaps>0</rounds_forcedlaps>
		<rounds_pointslimitnewrules>5</rounds_pointslimitnewrules>
		<team_pointslimit>50</team_pointslimit>
		<team_maxpoints>6</team_maxpoints>
		<team_usenewrules>0</team_usenewrules>
		<team_pointslimitnewrules>5</team_pointslimitnewrules>
		<timeattack_limit>600000</timeattack_limit>
		<timeattack_synchstartperiod>0</timeattack_synchstartperiod>
		<laps_nblaps>5</laps_nblaps>
		<laps_timelimit>0</laps_timelimit>
		<cup_pointslimit>100</cup_pointslimit>
		<cup_roundsperchallenge>5</cup_roundsperchallenge>
		<cup_nbwinners>3</cup_nbwinners>
		<cup_warmupduration>2</cup_warmupduration>
	</gameinfos>

	<hotseat>
		<game_mode>1</game_mode>
		<time_limit>300000</time_limit>
		<rounds_count>5</rounds_count>
	</hotseat>

	<filter>
		<is_lan>1</is_lan>
		<is_internet>1</is_internet>
		<is_solo>0</is_solo>
		<is_hotseat>0</is_hotseat>
		<sort_index>27</sort_index>
		<random_map_order>0</random_map_order>
		<force_default_gamemode>0</force_default_gamemode>
	</filter>

	<startindex>0</startindex>
	<challenge>
		<file>{warmupChallengeGbxFile.FileName}</file>
	</challenge>
</playlist>
";
        }

        await File.WriteAllTextAsync(matchSettingsFilePath, matchSettingsXml, cancellationToken);
    }

    private void StartServer(bool showServerWindow)
    {
        serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(dedicatedServerDir, dedicatedExeFileName),
                WorkingDirectory = dedicatedServerDir,
                UseShellExecute = false,
                CreateNoWindow = !showServerWindow,
                WindowStyle = showServerWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized, // ProcessWindowStyle.Hidden
            }
        };

        serverProcess.StartInfo.ArgumentList.Add($"/game_settings={config.GameSettings}");
        serverProcess.StartInfo.ArgumentList.Add($"/servername={config.ServerName}");
        serverProcess.StartInfo.ArgumentList.Add("/verbose_rpc");

        if (!string.IsNullOrWhiteSpace(config.ServerComment))
        {
            serverProcess.StartInfo.ArgumentList.Add($"/comment={config.ServerComment}");
        }

        if (game == DedicatedServerType.TM)
        {
            var gameId = config.Game switch
            {
                GameTitle.TMN => "nations",
                GameTitle.TMS => "sunrise",
                GameTitle.TMO => "original",
                _ => throw new InvalidOperationException($"Unsupported game: {config.Game}")
            };

            serverProcess.StartInfo.ArgumentList.Add($"/game={gameId}");
            serverProcess.StartInfo.ArgumentList.Add("/dedicated_cfg=dedicated.cfg");
        }
        else
        {
            serverProcess.StartInfo.ArgumentList.Add("/dedicated_cfg=dedicated_cfg.txt");
        }

        if (config.Lan)
        {
            serverProcess.StartInfo.ArgumentList.Add("/lan");
        }

        if (config.BindIP is not null)
        {
            serverProcess.StartInfo.ArgumentList.Add($"/bindip={config.BindIP}");
        }

        serverProcess.Start();
    }

    public void StopServer()
    {
        var process = serverProcess;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        finally
        {
            process.Dispose();
            serverProcess = null;
        }
    }

    private static async Task DownloadWithProgressAsync(HttpContent content, Stream destination, CancellationToken cancellationToken)
    {
        var totalBytes = content.Headers.ContentLength;

        await using var source = await content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[CopyBufferSize];
        long totalRead = 0;
        var lastReportedPercent = -1;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            var downloadedMb = totalRead / 1024.0 / 1024.0;

            if (totalBytes is { } total && total > 0)
            {
                var percent = (int)(totalRead * 100 / total);
                if (percent == lastReportedPercent)
                {
                    continue;
                }

                lastReportedPercent = percent;
                Console.Write($"\rDownloading server files... {percent}% ({downloadedMb:F1} MB / {total / 1024.0 / 1024.0:F1} MB)");
            }
            else
            {
                Console.Write($"\rDownloading server files... {downloadedMb:F1} MB");
            }
        }

        Console.WriteLine();
    }

    private static async Task<DownloadCacheEntry?> ReadCacheEntryAsync(string cacheFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            var tomlText = await File.ReadAllTextAsync(cacheFilePath, cancellationToken);
            return TomlSerializer.Deserialize<DownloadCacheEntry>(tomlText);
        }
        catch (TomlException)
        {
            return null;
        }
    }

    private static async Task WriteCacheEntryAsync(string cacheFilePath, DownloadCacheEntry entry, CancellationToken cancellationToken)
    {
        var tomlText = TomlSerializer.Serialize(entry);
        await File.WriteAllTextAsync(cacheFilePath, tomlText, cancellationToken);
    }

    private sealed record DownloadCacheEntry(string Url, string? ETag, DateTimeOffset? LastModified, long? ContentLength);

    [GeneratedRegex(@"<xmlrpc_port>(\d+)<\/xmlrpc_port>", RegexOptions.IgnoreCase)]
    private static partial Regex XmlRpcPortRegex();

    [GeneratedRegex(@"<server_port>(\d+)<\/server_port>", RegexOptions.IgnoreCase)]
    private static partial Regex ServerPortRegex();

    [GeneratedRegex(@"<server_p2p_port>(\d+)<\/server_p2p_port>", RegexOptions.IgnoreCase)]
    private static partial Regex ServerP2PPortRegex();

    // the <masterserver_account> block only - the file has several other <login>/<password> tags
    // (the SuperAdmin/Admin/User levels) that must never be touched
    [GeneratedRegex(@"<masterserver_account>.*?<\/masterserver_account>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MasterServerAccountRegex();

    [GeneratedRegex(@"<login>.*?<\/login>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LoginTagRegex();

    [GeneratedRegex(@"<password>.*?<\/password>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PasswordTagRegex();

    [GeneratedRegex(@"<validation_key>.*?<\/validation_key>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ValidationKeyTagRegex();

    [GeneratedRegex(@"<packmask>(.*?)<\/packmask>", RegexOptions.IgnoreCase)]
    private static partial Regex PackmaskRegex();

    [GeneratedRegex(@"<use_changing_validation_seed>(.*?)<\/use_changing_validation_seed>", RegexOptions.IgnoreCase)]
    private static partial Regex ValidationSeedRegex();
}
