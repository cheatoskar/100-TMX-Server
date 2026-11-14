using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using TmEssentials;

namespace RandomizerAnywhere;

// The in-game HUD: map panel (top left), tile bar (top right), top finishers (right), event list
// (left, only with the event module on), checkpoint counter (bottom) and personal times (bottom
// right). Laid out and styled after Records Eyepiece (XAseco): the game's own Bgs1InRace/NavButton
// boxes, BgsPlayerCard title bars and highlight bars, Icons128x128_1 icons, line height 1.8.
//
// Everything is sent per player - the top finishers show the player's own place, and every player
// can fold or switch off parts (HudSettings) - as one <manialinks> page per player and update. A
// part that is off is sent as an empty manialink with its id, which removes it.
internal sealed partial class RandomizerGame
{
    [Flags]
    private enum HudSection
    {
        None = 0,
        Map = 1,
        Toolbar = 2,
        Top = 4,
        Event = 8,
        Cp = 16,
        Times = 32,
        Keys = 64,
        All = Map | Toolbar | Top | Event | Cp | Times | Keys,
    }

    // manialink ids - the first three are the ids the old widgets used, so the new ones replace them
    private const string HudMapId = "map_info";
    private const string HudCpId = "cp_counter";
    private const string HudTopId = "top10_panel";
    private const string HudToolbarId = "hud_toolbar";
    private const string HudEventId = "event_list";
    private const string HudTimesId = "hud_times";
    private const string HudKeysId = "hud_keys";

    // click actions, all >= ActionHudBase so they never reach the preset/history ranges
    private const int ActionHudBase = 1000;
    private const int ActionHudSkipVote = 1001;
    private const int ActionHudMapVoteHint = 1002;
    private const int ActionHudPresets = 1003;
    private const int ActionHudHistory = 1004;
    private const int ActionHudToggleAll = 1005;
    private const int ActionHudHelp = 1006;
    private const int ActionHudMenuClose = 1007;
    private const int ActionHudMenuReset = 1009;
    private const int ActionHudMenuToggleBase = 1200;
    private const string HudMenuId = "hud_menu";
    private const int ActionHudCollapseMap = 1010;
    private const int ActionHudCollapseTop = 1011;
    private const int ActionHudCollapseEvent = 1012;
    private const int ActionHudCollapseTimes = 1013;
    private const int ActionHudCpPosition = 1014;
    private const int ActionEventPagePrev = 1020;
    private const int ActionEventPageNext = 1021;
    private const int ActionEventRowBase = 1100;

    private const double LineHeight = 1.8;
    private const int EventRowsPerPage = 10;

    private readonly object hudGate = new();

    // this map's best time per player, the checkpoint times of that best run, and the run in progress
    private readonly Dictionary<string, int> mapBestTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> mapBestCheckpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> runCheckpoints = new(StringComparer.OrdinalIgnoreCase);

    // TMX details of the running map (author, difficulty, awards, WR), filled in from the side
    private int? currentMapDetailsTrackId;
    private TmxRules.TrackDetails? currentMapDetails;
    private string? currentMapRawName;

    private readonly ConcurrentDictionary<int, TmxRules.TrackDetails> eventMapDetails = new();

    // the event maps' names with their colours, as the map file has them (TMX only gives plain names)
    private readonly ConcurrentDictionary<int, string> eventMapRawNames = new();

    private string EventMapName(int trackId) =>
        eventMapRawNames.TryGetValue(trackId, out var raw) ? raw
        : eventMapDetails.TryGetValue(trackId, out var d) ? d.Name
        : $"TMX #{trackId}";
    private readonly ConcurrentDictionary<string, int> eventPage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int[]> eventRowsShown = new(StringComparer.OrdinalIgnoreCase);

    private int mapsStartedCount;
    private DateTimeOffset? handledEventEnd;
    private const string EventResultsId = "event_results";
    private const int ActionEventResultsClose = 1030;
    private const string MapVoteWindowId = "map_vote_window";
    private const int ActionMapVoteClose = 1040;
    private const int ActionMapVotePrev = 1041;
    private const int ActionMapVoteNext = 1042;
    private const int ActionMapVoteRowBase = 1300;
    private const int MapVoteRowsPerPage = 14;
    private readonly ConcurrentDictionary<string, int> mapVotePage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int[]> mapVoteRowsShown = new(StringComparer.OrdinalIgnoreCase);
    private bool eventDoneAnnounced;
    private int? cachedMaxPlayers;

    private bool HudAvailable => !config.IsLegacyServer;

    private static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static string FormatTime(int ms) => ms <= 0 ? "--" : new TimeInt32(ms).ToString();

    // ---- state updates ----

    private void ResetHudForNewMap()
    {
        lock (hudGate)
        {
            mapBestTimes.Clear();
            mapBestCheckpoints.Clear();
            runCheckpoints.Clear();
            currentMapDetails = null;
            currentMapDetailsTrackId = null;
            mapsStartedCount++;
        }
    }

    // CheckpointIndex counts on across laps; the run starts over when it is 0 again
    private void RecordCheckpointTime(string login, int checkpointIndex, int time)
    {
        lock (hudGate)
        {
            if (checkpointIndex == 0 || !runCheckpoints.TryGetValue(login, out var run))
            {
                run = [];
                runCheckpoints[login] = run;
            }

            while (run.Count <= checkpointIndex)
            {
                run.Add(0);
            }

            run[checkpointIndex] = time;
        }
    }

    // split of this checkpoint against the same checkpoint of the player's best run on this map
    private int? CheckpointSplit(string login, int checkpointIndex)
    {
        lock (hudGate)
        {
            if (mapBestCheckpoints.TryGetValue(login, out var best) && checkpointIndex < best.Length
                && runCheckpoints.TryGetValue(login, out var run) && checkpointIndex < run.Count)
            {
                return run[checkpointIndex] - best[checkpointIndex];
            }

            return null;
        }
    }

    // a finish: keeps the best time and that run's checkpoints. True when it is a new best.
    private bool RecordMapFinishTime(string login, int time)
    {
        if (time <= 0)
        {
            return false;
        }

        lock (hudGate)
        {
            if (mapBestTimes.TryGetValue(login, out var best) && best <= time)
            {
                return false;
            }

            mapBestTimes[login] = time;
            if (runCheckpoints.TryGetValue(login, out var run))
            {
                mapBestCheckpoints[login] = [.. run];
            }

            return true;
        }
    }

    // Called from the PlayerFinish callback for every finish, before the auto-skip logic.
    private async Task OnHudFinishAsync(string login, int time, CancellationToken cancellationToken)
    {
        if (time <= 0)
        {
            return;
        }

        RecordMapFinishTime(login, time);

        if (eventMode.Enabled && eventMode.EndedAt is null && currentMapTrackId is { } trackId && eventMode.IsListed(trackId))
        {
            if (await eventMode.RecordFinishAsync(trackId, login, GetRawNickname(login), time))
            {
                var name = eventMapDetails.TryGetValue(trackId, out var d) ? d.Name : $"#{trackId}";
                var left = eventMode.OpenMaps.Count;
                await SendMessageAsync($"$FC0» Event: {GetNicknameOrLogin(login)}$z$FC0 finished {name} first ({FormatTime(time)}) - {left} map(s) left.", cancellationToken);

                if (left == 0)
                {
                    await eventMode.TryEndAsync();
                }
            }
        }

        await BroadcastHudAsync(HudSection.Times, cancellationToken);
    }

    // ---- sending ----

    private async Task SendHudAsync(string login, HudSection sections, CancellationToken cancellationToken)
    {
        if (!HudAvailable || sections == HudSection.None)
        {
            return;
        }

        var page = BuildHudPage(login, sections);
        await client.SendManialinkPageToLoginAsync(login, page, cancellationToken: cancellationToken);
    }

    private async Task BroadcastHudAsync(HudSection sections, CancellationToken cancellationToken)
    {
        if (!HudAvailable)
        {
            return;
        }

        IReadOnlyList<PlayerSummary> players;
        try
        {
            players = await client.GetPlayersAsync(cancellationToken);
            lastPolledPlayers = players;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            players = lastPolledPlayers;
        }

        foreach (var player in players)
        {
            try
            {
                await SendHudAsync(player.Login, sections, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // a player who just left - the others still get theirs
                Diagnostics.Trace($"hud send to {player.Login} failed: {ex.Message}");
            }
        }
    }

    // TMX details for the running map, from the side - BeginRace never waits for TMX
    private void FetchCurrentMapDetailsInBackground(int trackId)
    {
        RunInBackground($"loading the TMX details of map {trackId}", async () =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(appToken);
            budget.CancelAfter(TrackMetaBudget);
            var details = await tmxRules.GetTrackDetailsAsync(trackId, budget.Token);

            if (currentMapTrackId != trackId)
            {
                return;
            }

            lock (hudGate)
            {
                currentMapDetails = details;
                currentMapDetailsTrackId = trackId;
            }

            await BroadcastHudAsync(HudSection.Map | HudSection.Times, appToken);
        });
    }

    // the event list changed (hand edit, TMX refresh, a finish here or on another server)
    private void OnEventChanged()
    {
        RunInBackground("refreshing the event list", async () =>
        {
            await LoadEventMapDetailsAsync(appToken);
            await BroadcastHudAsync(HudSection.Event | HudSection.Toolbar, appToken);

            if (eventMode.EndedAt is { } ended && handledEventEnd != ended)
            {
                handledEventEnd = ended;
                await RunEventEndAsync(appToken);
                return;
            }

            await SkipMapFinishedElsewhereAsync();
        });
    }

    // Two servers can end up on the same event map (a vote, or nothing else left). Once the other
    // one finishes it, it is done for the event - this server moves on instead of playing it on.
    private async Task SkipMapFinishedElsewhereAsync()
    {
        if (!config.SourceFromEventList || !SessionActive || currentMapTrackId is not { } trackId
            || eventMode.GetFinish(trackId) is not { } finish
            || string.Equals(finish.Server, eventMode.ServerLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var generation = mapGeneration;
        await RunUnderGameLockAsync($"skipping event map {trackId} finished on {finish.Server}", async token =>
        {
            if (mapGeneration != generation || currentMapTrackId != trackId || SwitchAlreadyClaimed || isAdvancingToNextMap)
            {
                return;
            }

            await SendMessageAsync($"$FC0» {finish.Nickname}$z$FC0 just finished this map on {finish.Server} ({FormatTime(finish.Time)}) - it's done, moving on.", token);
            await NextRandomMapAsync(goalReached: false, token);
        });
    }

    // /votemap with the event: never a map another server is playing right now, and with the
    // preset's MapVoteScope = "event" only the list's open maps. False = refused (and said why).
    private async Task<bool> CheckEventMapVoteAsync(string login, int trackId, CancellationToken cancellationToken)
    {
        if (!eventMode.Enabled)
        {
            return true;
        }

        if (eventMode.OtherServers().FirstOrDefault(s => s.TrackId == trackId) is { } other)
        {
            await SendMessageAsync(login, $"$F00That map is running on {other.Label} right now - join it there (event list, OTHER SERVERS).", cancellationToken);
            return false;
        }

        if (config.MapVoteScope == "event" && !eventMode.OpenMapsForThisServer.Any(m => m.TrackId == trackId))
        {
            await SendMessageAsync(login, eventMode.GetFinish(trackId) is { } done
                ? $"$F00That map is already done ({done.Nickname}$z$F00, {FormatTime(done.Time)})."
                : "$F00During the event only open maps from the event list can be voted - click one in the list.", cancellationToken);
            return false;
        }

        return true;
    }

    // Every event map is finished: the results for everyone, then (EventAfterPreset) back to
    // normal play. Runs once per server, when it first sees the shared end mark.
    private async Task RunEventEndAsync(CancellationToken cancellationToken)
    {
        var standings = eventMode.Standings();
        var span = eventMode.FinishSpan();
        var duration = span is { } s ? s.Last - s.First : TimeSpan.Zero;

        await client.SendManialinkPageAsync(BuildEventResultsWindow(), cancellationToken: cancellationToken);
        await SendMessageAsync(
        [
            $"$FC0» {eventMode.Title} is finished! {eventMode.AllMaps.Count} maps in {(int)duration.TotalHours}:{duration.Minutes:00} h.",
            standings.Count > 0
                ? $"$FC0» Winner: {standings[0].Nickname}$z$FC0 with {standings[0].Maps} map(s). GG everyone!"
                : "$FC0» GG everyone!",
        ], cancellationToken);

        if (string.IsNullOrWhiteSpace(config.EventAfterPreset) || !config.SourceFromEventList)
        {
            return;
        }

        // a moment to look at the results before the next map loads
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

        await RunUnderGameLockAsync("switching to the preset after the event", async token =>
        {
            var name = ResolvePresetName(config.EventAfterPreset);
            var (preset, error) = LoadPresetForThisGame(name);
            if (preset is null)
            {
                Console.WriteLine($"Warning: EventAfterPreset '{config.EventAfterPreset}' can't be used - {error}. Staying on the event preset.");
                return;
            }

            var wasActive = SessionActive;
            if (wasActive)
            {
                ResetSessionStateForPresetSwitch();
            }

            var displayName = ApplyPreset(name, preset);
            await SendMessageAsync($"$FC0» Back to normal play: preset $FF0{ShortenPresetDisplayName(displayName)}$FC0.", token);

            if (wasActive)
            {
                await StartSessionAsync(token);
            }
        });
    }

    private async Task EventAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (!eventMode.Enabled)
        {
            await SendMessageAsync(login, "No event is running on this server.", cancellationToken);
            return;
        }

        if (eventMode.EndedAt is not null && HudAvailable)
        {
            await ShowWindowAsync(login, BuildEventResultsWindow(), cancellationToken);
            return;
        }

        var standings = eventMode.Standings();
        var mine = standings.Select((st, i) => (st, i)).FirstOrDefault(x => x.st.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        await SendMessageAsync(login,
        [
            $"$FC0{eventMode.Title}$FFF: {eventMode.FinishedCount}/{eventMode.AllMaps.Count} maps done, {eventMode.OpenMaps.Count} open.",
            standings.Count == 0 ? "Nobody has finished an event map yet."
                : $"Leading: {standings[0].Nickname}$z$FFF ({standings[0].Maps})" + (mine.st is not null ? $" · you: #{mine.i + 1} ({mine.st.Maps})" : string.Empty),
        ], cancellationToken);
    }

    // The map vote window ("Map vote" in the map panel). With the event: the open maps this server
    // can play, with author, difficulty and AT, a click starts the vote; a map running on another
    // server is shown but not clickable. Without the event: how /votemap works.
    private string BuildMapVoteWindow(string login)
    {
        const double width = 64;
        var b = new StringBuilder();
        var maps = eventMode.Enabled && eventMode.EndedAt is null ? eventMode.OpenMapsForThisServer : [];

        if (maps.Count == 0)
        {
            const double h = 12;
            b.Append($"<manialink id=\"{MapVoteWindowId}\"><frame posn=\"{Inv(-width / 2)} {Inv(h / 2 + 4)} 20\">");
            AppendWindowHeader(b, width, h, "Vote for a map", ActionMapVoteClose);
            b.Append("<label posn=\"1.5 -4.5 0.02\" sizen=\"60 2\" textsize=\"1\" text=\"Type $FF0/votemap <TMX id>$FFF in the chat - the id is the number in the map's TMX link.\"/>");
            b.Append("<label posn=\"1.5 -6.8 0.02\" sizen=\"60 2\" textsize=\"1\" textcolor=\"CCCF\" text=\"The others then vote with Yes / No. The map loads right after a passed vote.\"/>");
            b.Append("</frame></manialink>");
            return b.ToString();
        }

        var pages = Math.Max(1, (maps.Count + MapVoteRowsPerPage - 1) / MapVoteRowsPerPage);
        var page = Math.Clamp(mapVotePage.GetValueOrDefault(login, 0), 0, pages - 1);
        mapVotePage[login] = page;
        var shown = maps.Skip(page * MapVoteRowsPerPage).Take(MapVoteRowsPerPage).ToArray();
        mapVoteRowsShown[login] = [.. shown.Select(m => m.TrackId)];
        var elsewhere = eventMode.OtherServers().Where(s => s.TrackId is not null).ToDictionary(s => s.TrackId!.Value, s => s.Label);

        var height = 8.5 + shown.Length * 2.2 + 3;
        b.Append($"<manialink id=\"{MapVoteWindowId}\"><frame posn=\"{Inv(-width / 2)} {Inv(height / 2 + 4)} 20\">");
        AppendWindowHeader(b, width, height, $"Vote for a map $AAA· {maps.Count} open", ActionMapVoteClose);
        b.Append("<label posn=\"1.5 -3.9 0.02\" sizen=\"60 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"CCCF\" text=\"Click a map to start a vote for it. Everyone votes Yes / No.\"/>");

        // column heads
        b.Append("<label posn=\"4.5 -6 0.02\" sizen=\"20 2\" textsize=\"1\" scale=\"0.75\" textcolor=\"FC0F\" text=\"MAP\"/>");
        b.Append("<label posn=\"30 -6 0.02\" sizen=\"14 2\" textsize=\"1\" scale=\"0.75\" textcolor=\"FC0F\" text=\"AUTHOR\"/>");
        b.Append("<label posn=\"45 -6 0.02\" sizen=\"10 2\" textsize=\"1\" scale=\"0.75\" textcolor=\"FC0F\" text=\"DIFFICULTY\"/>");
        b.Append($"<label posn=\"{Inv(width - 1.5)} -6 0.02\" sizen=\"8 2\" halign=\"right\" textsize=\"1\" scale=\"0.75\" textcolor=\"FC0F\" text=\"AT\"/>");

        var y = -7.6;
        for (var i = 0; i < shown.Length; i++)
        {
            var map = shown[i];
            var d = eventMapDetails.GetValueOrDefault(map.TrackId);
            var busy = elsewhere.TryGetValue(map.TrackId, out var label);
            var here = map.TrackId == currentMapTrackId;

            b.Append($"<quad posn=\"0.8 {Inv(y)} 0.01\" sizen=\"{Inv(width - 1.6)} 2\" style=\"BgsPlayerCard\" substyle=\"{(here ? "BgCardSystem" : "ProgressBar")}\"{(busy || here ? string.Empty : $" action=\"{ActionMapVoteRowBase + i}\"")}/>");
            b.Append($"<label posn=\"3.6 {Inv(y - 0.35)} 0.02\" sizen=\"3 2\" halign=\"right\" textsize=\"1\" scale=\"0.9\" text=\"{page * MapVoteRowsPerPage + i + 1}.\"/>");
            b.Append($"<label posn=\"4.5 {Inv(y - 0.35)} 0.02\" sizen=\"{Inv(25 / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(Fit(EventMapName(map.TrackId), 25, CharWidth1 * 0.9))}\"/>");
            b.Append($"<label posn=\"30 {Inv(y - 0.35)} 0.02\" sizen=\"{Inv(14 / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"DDDF\" text=\"{Esc(d?.Author ?? "")}\"/>");
            var status = here ? "$0F0playing here" : busy ? $"$FC0on {label}" : d is null ? string.Empty : DifficultyColorCode(d.DifficultyLabel) + d.DifficultyLabel;
            b.Append($"<label posn=\"45 {Inv(y - 0.35)} 0.02\" sizen=\"{Inv(11 / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(status)}\"/>");
            b.Append($"<label posn=\"{Inv(width - 1.5)} {Inv(y - 0.35)} 0.02\" sizen=\"8 2\" halign=\"right\" textsize=\"1\" scale=\"0.9\" textcolor=\"3F5F\" text=\"{(d is null ? "" : FormatTime(d.AuthorTimeMs))}\"/>");
            y -= 2.2;
        }

        y -= 0.4;
        b.Append($"<quad posn=\"1 {Inv(y)} 0.02\" sizen=\"2.4 2.4\" style=\"Icons64x64_1\" substyle=\"ArrowPrev\" action=\"{ActionMapVotePrev}\"/>");
        b.Append($"<label posn=\"{Inv(width / 2)} {Inv(y - 0.5)} 0.02\" sizen=\"20 2\" halign=\"center\" textsize=\"1\" textcolor=\"CCCF\" text=\"page {page + 1}/{pages}\"/>");
        b.Append($"<quad posn=\"{Inv(width - 3.4)} {Inv(y)} 0.02\" sizen=\"2.4 2.4\" style=\"Icons64x64_1\" substyle=\"ArrowNext\" action=\"{ActionMapVoteNext}\"/>");
        b.Append("</frame></manialink>");
        return b.ToString();
    }

    // Only one window at a time: opening one (HUD menu, history, presets, map vote, results) clears
    // the others, so they never stack with one hidden behind another.
    private static readonly string[] WindowIds = [PresetListManialinkId, HistoryManialinkId, HudMenuId, MapVoteWindowId, EventResultsId];

    private async Task ShowWindowAsync(string login, string windowManialink, CancellationToken cancellationToken)
    {
        var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><manialinks>");
        foreach (var id in WindowIds)
        {
            if (!windowManialink.Contains($"id=\"{id}\"", StringComparison.Ordinal))
            {
                b.Append("<manialink id=\"").Append(id).Append("\"></manialink>");
            }
        }

        b.Append(windowManialink).Append("</manialinks>");
        await client.SendManialinkPageToLoginAsync(login, b.ToString(), cancellationToken: cancellationToken);
    }

    private static void AppendWindowHeader(StringBuilder b, double width, double height, string title, int closeAction, string iconStyle = "Icons128x128_1", string iconSubstyle = "Challenge")
    {
        AppendShade(b, width, height, "0005");
        b.Append($"<quad posn=\"0 0 0\" sizen=\"{Inv(width)} {Inv(height)}\" style=\"Bgs1InRace\" substyle=\"NavButton\"/>");
        b.Append($"<quad posn=\"0.4 -0.36 0.01\" sizen=\"{Inv(width - 0.8)} 2.6\" style=\"BgsPlayerCard\" substyle=\"BgRacePlayerName\"/>");
        b.Append($"<quad posn=\"0.7 0 0.02\" sizen=\"3 3\" style=\"{iconStyle}\" substyle=\"{iconSubstyle}\"/>");
        b.Append($"<label posn=\"4.2 -0.75 0.02\" sizen=\"{Inv(width - 9)} 2\" textsize=\"2\" text=\"{Esc(title)}\"/>");
        b.Append($"<quad posn=\"{Inv(width - 3.2)} -0.5 0.03\" sizen=\"2.4 2.4\" style=\"Icons64x64_1\" substyle=\"Close\" action=\"{closeAction}\"/>");
    }

    private string BuildEventResultsWindow()
    {
        var standings = eventMode.Standings();
        var span = eventMode.FinishSpan();
        var duration = span is { } s ? s.Last - s.First : TimeSpan.Zero;
        const double width = 56;
        var rest = standings.Skip(3).Take(7).ToList();
        var height = 21 + rest.Count * LineHeight + 2;

        var b = new StringBuilder();
        b.Append($"<manialink id=\"{EventResultsId}\"><frame posn=\"{Inv(-width / 2)} {Inv(height / 2 + 4)} 25\">");
        AppendShade(b, width, height, "0005");
        b.Append($"<quad posn=\"0 0 0\" sizen=\"{Inv(width)} {Inv(height)}\" style=\"Bgs1InRace\" substyle=\"NavButton\"/>");
        b.Append($"<quad posn=\"0.4 -0.36 0.01\" sizen=\"{Inv(width - 0.8)} 3\" style=\"BgsPlayerCard\" substyle=\"BgRacePlayerName\"/>");
        b.Append("<quad posn=\"0.7 0 0.02\" sizen=\"3.4 3.4\" style=\"Icons128x128_1\" substyle=\"Rankings\"/>");
        b.Append($"<label posn=\"4.6 -0.8 0.02\" sizen=\"44 2\" textsize=\"3\" text=\"{Esc($"$o{eventMode.Title} finished!")}\"/>");
        b.Append($"<quad posn=\"{Inv(width - 3.4)} -0.6 0.03\" sizen=\"2.6 2.6\" style=\"Icons64x64_1\" substyle=\"Close\" action=\"{ActionEventResultsClose}\"/>");
        b.Append($"<label posn=\"{Inv(width / 2)} -4.4 0.02\" sizen=\"50 2\" halign=\"center\" textsize=\"1\" textcolor=\"DDDF\" text=\"{Esc($"{eventMode.AllMaps.Count} maps done in {(int)duration.TotalHours}:{duration.Minutes:00} h - thanks for playing!")}\"/>");

        // podium: 2nd left, 1st middle (raised), 3rd right
        (int Rank, double X, double Y, string Medal)[] podium = [(1, width / 2, -7, "MedalGold"), (2, width / 2 - 17, -8.5, "MedalSilver"), (3, width / 2 + 17, -9.5, "MedalBronze")];
        foreach (var (rank, x, y, medal) in podium)
        {
            if (standings.Count < rank)
            {
                continue;
            }

            var st = standings[rank - 1];
            b.Append($"<quad posn=\"{Inv(x - 2.5)} {Inv(y)} 0.02\" sizen=\"5 5\" style=\"MedalsBig\" substyle=\"{medal}\"/>");
            b.Append($"<label posn=\"{Inv(x)} {Inv(y - 5.6)} 0.02\" sizen=\"16 2\" halign=\"center\" textsize=\"2\" text=\"{Esc(st.Nickname)}\"/>");
            b.Append($"<label posn=\"{Inv(x)} {Inv(y - 8)} 0.02\" sizen=\"16 2\" halign=\"center\" textsize=\"1\" textcolor=\"FC0F\" text=\"{st.Maps} map{(st.Maps == 1 ? "" : "s")}\"/>");
        }

        var ry = -20.0;
        for (var i = 0; i < rest.Count; i++)
        {
            b.Append($"<label posn=\"{Inv(width / 2 - 12)} {Inv(ry)} 0.02\" sizen=\"3 2\" halign=\"right\" textsize=\"1\" text=\"{i + 4}.\"/>");
            b.Append($"<label posn=\"{Inv(width / 2 - 11)} {Inv(ry)} 0.02\" sizen=\"18 2\" textsize=\"1\" text=\"{Esc(rest[i].Nickname)}\"/>");
            b.Append($"<label posn=\"{Inv(width / 2 + 12)} {Inv(ry)} 0.02\" sizen=\"6 2\" halign=\"right\" textsize=\"1\" textcolor=\"FC0F\" text=\"{rest[i].Maps}\"/>");
            ry -= LineHeight;
        }

        b.Append("</frame></manialink>");
        return b.ToString();
    }

    private async Task LoadEventMapDetailsAsync(CancellationToken cancellationToken)
    {
        var missing = eventMode.AllMaps.Select(m => m.TrackId).Where(id => !eventMapDetails.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            foreach (var (id, details) in await tmxRules.GetTrackDetailsBatchAsync(missing, budget.Token))
            {
                eventMapDetails[id] = details;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Warning: couldn't load the event maps' names from TMX - {ex.Message}. Showing ids until the next try.");
        }

        LoadEventMapRawNamesInBackground();
    }

    private int eventRawNamesLoading;

    // TMX's API only has plain names. The coloured one is in the map file: each event map is
    // downloaded once (it is needed on the event anyway), written next to the played maps and its
    // name read back through the game server. One map at a time, in the background; a failure
    // just leaves that map's plain name.
    private void LoadEventMapRawNamesInBackground()
    {
        if (!HudAvailable || Interlocked.Exchange(ref eventRawNamesLoading, 1) == 1)
        {
            return;
        }

        RunInBackground("reading the event maps' coloured names", async () =>
        {
            try
            {
                var changed = false;
                foreach (var map in eventMode.AllMaps.Where(m => !eventMapRawNames.ContainsKey(m.TrackId)).ToList())
                {
                    try
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                        budget.CancelAfter(TimeSpan.FromSeconds(30));
                        var file = await tmxRules.GetMapGbxByIdAsync(map.TrackId, budget.Token);
                        // The game server remembers a map under the first path it saw, so when this
                        // map is played later it reports THIS file. The _tmx<id>_ marker lets the
                        // controller still tie it to its TMX id (else finishes on it are lost).
                        var path = Path.Combine("_RandomizerAny", "event", $"event_tmx{map.TrackId}_.Challenge.Gbx");
                        await client.WriteFileAsync(path, file.Data, budget.Token);
                        var info = await client.GetChallengeInfoAsync(path, budget.Token);
                        if (!string.IsNullOrWhiteSpace(info.Name))
                        {
                            eventMapRawNames[map.TrackId] = TmLinkCodeRegex().Replace(info.Name, string.Empty);
                            changed = true;
                        }
                    }
                    catch (Exception ex) when (!appToken.IsCancellationRequested)
                    {
                        Diagnostics.Trace($"coloured name of event map {map.TrackId}: {ex.Message}");
                    }
                }

                if (changed)
                {
                    await BroadcastHudAsync(HudSection.Event, appToken);
                }
            }
            finally
            {
                Interlocked.Exchange(ref eventRawNamesLoading, 0);
            }
        });
    }

    // every 15 s: tiles (players, session time), the event list (other servers' maps) and, with the
    // event module on, this server's heartbeat in the shared state file
    private async Task HudLoopAsync(CancellationToken cancellationToken)
    {
        if (eventMode.Enabled)
        {
            handledEventEnd = eventMode.EndedAt;
            eventMode.Changed += OnEventChanged;
            await LoadEventMapDetailsAsync(cancellationToken);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (eventMode.Enabled && eventMode.TmxRefreshDue && eventMode.EndedAt is null)
                {
                    try
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        budget.CancelAfter(TimeSpan.FromSeconds(40));
                        var (ids, complete) = await tmxRules.ListEventPoolAsync(budget.Token);
                        await eventMode.MergeTmxPoolAsync([.. ids.Where(id => !tmxRules.IsExcludedFromRotation(id))], complete);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Warning: couldn't refresh the event list from TMX - {ex.Message}. Keeping the current list.");
                    }
                }

                if (eventMode.Enabled)
                {
                    await eventMode.PublishServerAsync(config.FavoriteLogin, currentMapTrackId, currentMapRawName is null ? null : StripTmCodes(currentMapRawName),
                        lastPolledPlayers.Count, cachedMaxPlayers ?? 0);
                }

                if (HudAvailable)
                {
                    await EnsureMaxPlayersAsync(cancellationToken);
                    await BroadcastHudAsync(HudSection.Toolbar | (eventMode.Enabled ? HudSection.Event : HudSection.None), cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.Trace($"hud loop: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private async Task EnsureMaxPlayersAsync(CancellationToken cancellationToken)
    {
        if (cachedMaxPlayers is not null)
        {
            return;
        }

        try
        {
            var result = await client.TracedCallAsync<Dictionary<string, object>>("GetMaxPlayers", [], cancellationToken);
            cachedMaxPlayers = result.TryGetValue("CurrentValue", out var v) && v is int max ? max : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // the tile shows the count alone until it works
        }
    }

    // ---- clicks and /hud ----

    private async Task HandleHudActionAsync(int playerUid, string login, int answer, CancellationToken cancellationToken)
    {
        switch (answer)
        {
            case ActionHudSkipVote:
                await SkipAsync(playerUid, login, [], cancellationToken);
                return;
            case ActionHudMapVoteHint:
                await ShowWindowAsync(login, BuildMapVoteWindow(login), cancellationToken);
                return;
            case ActionMapVoteClose:
                await client.SendManialinkPageToLoginAsync(login, $"<manialink id=\"{MapVoteWindowId}\"></manialink>", cancellationToken: cancellationToken);
                return;
            case ActionMapVotePrev:
            case ActionMapVoteNext:
                mapVotePage.AddOrUpdate(login, _ => answer == ActionMapVoteNext ? 1 : 0,
                    (_, page) => Math.Max(0, page + (answer == ActionMapVoteNext ? 1 : -1)));
                await ShowWindowAsync(login, BuildMapVoteWindow(login), cancellationToken);
                return;
            case ActionHudPresets:
                await PresetsAsync(playerUid, login, [], cancellationToken);
                return;
            case ActionHudHistory:
                await HistoryAsync(playerUid, login, [], cancellationToken);
                return;
            case ActionHudToggleAll:
                await hudSettings.UpdateAsync(login, p => p.AllHidden = !p.AllHidden);
                await SendHudAsync(login, HudSection.All, cancellationToken);
                return;
            case ActionHudHelp:
                await ShowWindowAsync(login, BuildHudMenu(login), cancellationToken);
                return;
            case ActionHudMenuClose:
                await client.SendManialinkPageToLoginAsync(login, $"<manialink id=\"{HudMenuId}\"></manialink>", cancellationToken: cancellationToken);
                return;
            case ActionHudMenuReset:
                await hudSettings.UpdateAsync(login, p =>
                {
                    p.AllHidden = false;
                    p.Hidden.Clear();
                    p.Collapsed.Clear();
                    p.CpAboveClock = false;
                });
                await SendHudAsync(login, HudSection.All, cancellationToken);
                await ShowWindowAsync(login, BuildHudMenu(login), cancellationToken);
                return;
            case ActionHudCollapseMap:
                await ToggleCollapsedAsync(login, HudPart.Map, HudSection.Map, cancellationToken);
                return;
            case ActionHudCollapseTop:
                await ToggleCollapsedAsync(login, HudPart.Top, HudSection.Top, cancellationToken);
                return;
            case ActionHudCollapseEvent:
                await ToggleCollapsedAsync(login, HudPart.Event, HudSection.Event, cancellationToken);
                return;
            case ActionHudCollapseTimes:
                await ToggleCollapsedAsync(login, HudPart.Times, HudSection.Times, cancellationToken);
                return;
            case ActionHudCpPosition:
                await hudSettings.UpdateAsync(login, p => p.CpAboveClock = !p.CpAboveClock);
                await SendHudAsync(login, HudSection.Cp, cancellationToken);
                await ShowWindowAsync(login, BuildHudMenu(login), cancellationToken);
                return;
            case ActionEventResultsClose:
                await client.SendManialinkPageToLoginAsync(login, $"<manialink id=\"{EventResultsId}\"></manialink>", cancellationToken: cancellationToken);
                return;
            case ActionEventPagePrev:
            case ActionEventPageNext:
                eventPage.AddOrUpdate(login, _ => answer == ActionEventPageNext ? 1 : 0,
                    (_, page) => Math.Max(0, page + (answer == ActionEventPageNext ? 1 : -1)));
                await SendHudAsync(login, HudSection.Event, cancellationToken);
                return;
        }

        if (answer >= ActionHudMenuToggleBase && answer < ActionHudMenuToggleBase + 20 && Enum.IsDefined(typeof(HudPart), answer - ActionHudMenuToggleBase))
        {
            var part = (HudPart)(answer - ActionHudMenuToggleBase);
            await hudSettings.UpdateAsync(login, p =>
            {
                p.AllHidden = false;
                if (!p.Hidden.Remove(part))
                {
                    p.Hidden.Add(part);
                }
            });
            await SendHudAsync(login, HudSection.All, cancellationToken);
            await ShowWindowAsync(login, BuildHudMenu(login), cancellationToken);
            return;
        }

        var voteRow = answer - ActionMapVoteRowBase;
        if (voteRow >= 0 && voteRow < MapVoteRowsPerPage && mapVoteRowsShown.TryGetValue(login, out var voteIds) && voteRow < voteIds.Length)
        {
            await client.SendManialinkPageToLoginAsync(login, $"<manialink id=\"{MapVoteWindowId}\"></manialink>", cancellationToken: cancellationToken);
            await VoteMapAsync(playerUid, login, [voteIds[voteRow].ToString(CultureInfo.InvariantCulture)], cancellationToken);
            return;
        }

        var row = answer - ActionEventRowBase;
        if (row >= 0 && row < EventRowsPerPage && eventRowsShown.TryGetValue(login, out var ids) && row < ids.Length)
        {
            await VoteMapAsync(playerUid, login, [ids[row].ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
    }

    private async Task ToggleCollapsedAsync(string login, HudPart part, HudSection section, CancellationToken cancellationToken)
    {
        await hudSettings.UpdateAsync(login, p =>
        {
            if (!p.Collapsed.Remove(part))
            {
                p.Collapsed.Add(part);
            }
        });

        await SendHudAsync(login, section, cancellationToken);
    }

    private static readonly Dictionary<string, HudPart> HudPartNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map"] = HudPart.Map,
        ["top"] = HudPart.Top,
        ["finishers"] = HudPart.Top,
        ["event"] = HudPart.Event,
        ["list"] = HudPart.Event,
        ["cp"] = HudPart.Cp,
        ["checkpoints"] = HudPart.Cp,
        ["times"] = HudPart.Times,
        ["toolbar"] = HudPart.Toolbar,
        ["tiles"] = HudPart.Toolbar,
    };

    private async Task HudAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (!HudAvailable)
        {
            await SendMessageAsync(login, "This server has no HUD widgets.", cancellationToken);
            return;
        }

        if (args.Length == 0)
        {
            await SendHudHelpAsync(login, cancellationToken);
            return;
        }

        var first = args[0].ToLowerInvariant();

        if (first is "on" or "off")
        {
            await hudSettings.UpdateAsync(login, p => p.AllHidden = first == "off");
        }
        else if (first == "reset")
        {
            await hudSettings.UpdateAsync(login, p =>
            {
                p.AllHidden = false;
                p.Hidden.Clear();
                p.Collapsed.Clear();
                p.CpAboveClock = false;
            });
        }
        else if (HudPartNames.TryGetValue(first, out var part))
        {
            var turnOn = args.Length > 1 ? args[1].Equals("on", StringComparison.OrdinalIgnoreCase) : !hudSettings.Get(login).Shows(part);
            await hudSettings.UpdateAsync(login, p =>
            {
                p.AllHidden = false;
                if (turnOn)
                {
                    p.Hidden.Remove(part);
                }
                else
                {
                    p.Hidden.Add(part);
                }
            });
            await SendMessageAsync(login, $"HUD: {first} is now {(turnOn ? "$0F0on" : "$F33off")}$FFF.", cancellationToken);
        }
        else
        {
            await SendHudHelpAsync(login, cancellationToken);
            return;
        }

        await SendHudAsync(login, HudSection.All, cancellationToken);
    }

    private async Task SendHudHelpAsync(string login, CancellationToken cancellationToken)
    {
        var prefs = hudSettings.Get(login);
        var off = prefs.Hidden.Count == 0 ? "none" : string.Join(", ", prefs.Hidden.Select(p => p.ToString().ToLowerInvariant()));
        await SendMessageAsync(login,
        [
            "$FC0HUD$FFF: $FF0F7$FFF or $FF0/hud off$FFF/$FF0on$FFF hides/shows everything, $FF0/hud reset$FFF restores the default.",
            $"Single parts: $FF0/hud map|top|event|cp|times|toolbar off$FFF (or on). Off right now: {off}.",
        ], cancellationToken);
    }

    // The HUD menu (tile "HUD" or /hud without arguments shows the chat help): every part with an
    // on/off switch, plus "reset". Kept per player in HudSettings like the "-" folding.
    private string BuildHudMenu(string login)
    {
        var prefs = hudSettings.Get(login);
        (HudPart Part, string Name, string Note)[] parts =
        [
            (HudPart.Map, "Current map", "name, author, AT, TMX, votes"),
            (HudPart.Top, "Top finishers", "top 10 and your place"),
            (HudPart.Event, "Event list", "open maps, other servers"),
            (HudPart.Cp, "Checkpoint counter", "CP and split to your best"),
            (HudPart.Times, "Personal times", "PB, server best, TMX WR"),
            (HudPart.Toolbar, "Tile bar", "players, preset, links"),
        ];

        const double width = 40;
        var height = 6.6 + parts.Length * 2.6 + 2.6 + 3.2;

        var b = new StringBuilder();
        b.Append($"<manialink id=\"{HudMenuId}\"><frame posn=\"{Inv(-width / 2)} {Inv(height / 2 + 2)} 20\">");
        AppendWindowHeader(b, width, height, "HUD settings", ActionHudMenuClose, "Icons128x128_1", "Advanced");
        b.Append("<label posn=\"1.2 -3.6 0.02\" sizen=\"42 2\" textsize=\"1\" scale=\"0.85\" textcolor=\"CCCF\" text=\"Saved for your login. F7 hides or shows everything at once.\"/>");

        var y = -6.0;
        foreach (var (part, name, note) in parts)
        {
            var on = !prefs.Hidden.Contains(part);
            b.Append($"<quad posn=\"0.8 {Inv(y)} 0.01\" sizen=\"{Inv(width - 1.6)} 2.3\" style=\"BgsPlayerCard\" substyle=\"ProgressBar\"/>");
            b.Append($"<label posn=\"1.6 {Inv(y - 0.4)} 0.02\" sizen=\"14 2\" textsize=\"1\" text=\"{Esc(name)}\"/>");
            b.Append($"<label posn=\"15.5 {Inv(y - 0.5)} 0.02\" sizen=\"{Inv(15 / 0.8)} 2\" textsize=\"1\" scale=\"0.8\" textcolor=\"AAAF\" text=\"{Esc(note)}\"/>");
            b.Append($"<quad posn=\"{Inv(width - 7.2)} {Inv(y - 0.25)} 0.02\" sizen=\"5.8 1.8\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" action=\"{ActionHudMenuToggleBase + (int)part}\"/>");
            b.Append($"<label posn=\"{Inv(width - 4.3)} {Inv(y - 1.15)} 0.03\" sizen=\"5.6 2\" halign=\"center\" valign=\"center\" textsize=\"1\" text=\"{(on ? "$3F5ON" : "$F33OFF")}\"/>");
            y -= 2.6;
        }

        // where the checkpoint counter sits
        b.Append($"<quad posn=\"0.8 {Inv(y)} 0.01\" sizen=\"{Inv(width - 1.6)} 2.3\" style=\"BgsPlayerCard\" substyle=\"ProgressBar\"/>");
        b.Append($"<label posn=\"1.6 {Inv(y - 0.4)} 0.02\" sizen=\"20 2\" textsize=\"1\" text=\"Checkpoint counter position\"/>");
        b.Append($"<quad posn=\"{Inv(width - 13.2)} {Inv(y - 0.25)} 0.02\" sizen=\"11.8 1.8\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" action=\"{ActionHudCpPosition}\"/>");
        b.Append($"<label posn=\"{Inv(width - 7.3)} {Inv(y - 1.15)} 0.03\" sizen=\"11.6 2\" halign=\"center\" valign=\"center\" textsize=\"1\" text=\"{(prefs.CpAboveClock ? "$FC0above the timer" : "$FC0left of the timer")}\"/>");
        y -= 2.6;

        y -= 0.4;
        b.Append($"<quad posn=\"0.8 {Inv(y)} 0.02\" sizen=\"12 2.2\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" action=\"{ActionHudToggleAll}\"/>");
        b.Append($"<label posn=\"6.8 {Inv(y - 1.1)} 0.03\" sizen=\"12 2\" halign=\"center\" valign=\"center\" textsize=\"1\" text=\"{(prefs.AllHidden ? "Show all (F7)" : "Hide all (F7)")}\"/>");
        b.Append($"<quad posn=\"13.4 {Inv(y)} 0.02\" sizen=\"9 2.2\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" action=\"{ActionHudMenuReset}\"/>");
        b.Append($"<label posn=\"17.9 {Inv(y - 1.1)} 0.03\" sizen=\"9 2\" halign=\"center\" valign=\"center\" textsize=\"1\" text=\"Reset\"/>");
        b.Append("</frame></manialink>");
        return b.ToString();
    }

    // ---- page building ----

    private string BuildHudPage(string login, HudSection sections)
    {
        var prefs = hudSettings.Get(login);
        var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><manialinks>");

        void Part(HudSection section, HudPart? part, string id, Func<string> build)
        {
            if (!sections.HasFlag(section))
            {
                return;
            }

            var visible = part is not { } p ? !prefs.AllHidden : prefs.Shows(p);
            var body = visible ? build() : string.Empty;
            b.Append("<manialink id=\"").Append(id).Append("\">").Append(body).Append("</manialink>");
        }

        Part(HudSection.Map, HudPart.Map, HudMapId, () => BuildMapPanel(prefs.Collapsed.Contains(HudPart.Map)));
        Part(HudSection.Toolbar, HudPart.Toolbar, HudToolbarId, BuildToolbar);
        Part(HudSection.Top, HudPart.Top, HudTopId, () => BuildTopFinishers(login, prefs.Collapsed.Contains(HudPart.Top)));
        Part(HudSection.Event, HudPart.Event, HudEventId, () => BuildEventPanel(login, prefs.Collapsed.Contains(HudPart.Event)));
        Part(HudSection.Cp, HudPart.Cp, HudCpId, () => BuildCheckpointCounter(login));
        Part(HudSection.Times, HudPart.Times, HudTimesId, () => BuildTimesBlock(login, prefs.Collapsed.Contains(HudPart.Times)));

        if (sections.HasFlag(HudSection.Keys))
        {
            // F7 (actionkey 3) toggles the whole HUD, like Records Eyepiece - the quad itself is off screen
            b.Append("<manialink id=\"").Append(HudKeysId).Append("\">")
                .Append($"<quad posn=\"70 70 1\" sizen=\"0 0\" action=\"{ActionHudToggleAll}\" actionkey=\"3\"/>")
                .Append("</manialink>")
                // TMF's own map name/author top right sat under the tile bar - the map panel replaces it
                .Append("<custom_ui><challenge_info visible=\"false\"/></custom_ui>");
        }

        b.Append("</manialinks>");
        return b.ToString();
    }

    // background box, title bar, icon, title and the fold button - Records Eyepiece's widget header
    // A dark layer under a NavButton box. A plain rectangle showed its square corners outside the
    // box's rounded ones, and a second NavButton on top added no darkness at all - so the layer is
    // three rectangles with the corners cut off, which stay inside the rounding.
    private static void AppendShade(StringBuilder b, double width, double height, string color)
    {
        const double edge = 0.25;
        const double cut = 0.7;
        b.Append($"<quad posn=\"{Inv(edge)} {Inv(-cut)} -0.01\" sizen=\"{Inv(width - 2 * edge)} {Inv(height - 2 * cut)}\" bgcolor=\"{color}\"/>");
        b.Append($"<quad posn=\"{Inv(cut)} {Inv(-edge)} -0.01\" sizen=\"{Inv(width - 2 * cut)} {Inv(cut - edge)}\" bgcolor=\"{color}\"/>");
        b.Append($"<quad posn=\"{Inv(cut)} {Inv(-(height - cut))} -0.01\" sizen=\"{Inv(width - 2 * cut)} {Inv(cut - edge)}\" bgcolor=\"{color}\"/>");
    }

    private static void AppendWidgetFrame(StringBuilder b, double width, double height, string iconStyle, string iconSubstyle, string title, int? collapseAction, bool collapsed)
    {
        AppendBox(b, width, height);
        b.Append($"<quad posn=\"0.4 -0.36 0.01\" sizen=\"{Inv(width - 0.8)} 2\" style=\"BgsPlayerCard\" substyle=\"BgRacePlayerName\"/>");
        b.Append($"<quad posn=\"0.6 -0.1 0.02\" sizen=\"2.5 2.5\" style=\"{iconStyle}\" substyle=\"{iconSubstyle}\"/>");
        b.Append($"<label posn=\"3.4 -0.62 0.02\" sizen=\"{Inv(width - 6.5)} 2\" textsize=\"1\" text=\"{Esc(title)}\"/>");

        if (collapseAction is { } action)
        {
            b.Append($"<quad posn=\"{Inv(width - 2.4)} -0.5 0.03\" sizen=\"1.7 1.7\" style=\"Icons64x64_1\" substyle=\"{(collapsed ? "ArrowDown" : "ArrowUp")}\" action=\"{action}\"/>");
        }
    }

    private string AssetBaseUrl => $"http://{(string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost)}:{config.ReplayServerPort}";

    private string ShortPresetName()
    {
        var name = config.LastPreset?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? "-" : ShortenPresetDisplayName(name);
    }

    // "100% TMX Project (TMUF Bay)" -> "100% TMX Bay", "100% TMX Project (TMNF)" -> "100% TMX"
    internal static string ShortenPresetDisplayName(string displayName)
    {
        var name = PresetGameSuffixRegex().Replace(displayName, m => m.Groups[1].Value.Length > 0 ? " " + m.Groups[1].Value : string.Empty);
        return name.Replace("100% TMX Project", "100% TMX", StringComparison.OrdinalIgnoreCase).Trim();
    }

    [GeneratedRegex(@"\s*\((?:TMNF|TMUF|ESWC|TMN)\s*([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex PresetGameSuffixRegex();

    private static string DifficultyColorCode(string difficultyLabel) => difficultyLabel switch
    {
        "Beginner" => "$0F0",
        "Intermediate" => "$0AF",
        "Expert" => "$FA0",
        "Lunatic" => "$F0F",
        _ => "$FFF",
    };

    // NavButton alone lets a bright track shine through; a light dark quad just inside its rounded
    // edge makes the text readable without turning the boxes black. Tiles get less of it, so
    // they match the game's own boxes (the "previous / best" panel top right).
    private static void AppendBox(StringBuilder b, double width, double height, string? attributes = null, string shade = "0005")
    {
        AppendShade(b, width, height, shade);

        b.Append($"<quad posn=\"0 0 0\" sizen=\"{Inv(width)} {Inv(height)}\" style=\"Bgs1InRace\" substyle=\"NavButton\" {attributes ?? string.Empty}/>");
    }

    // Rendered width of text, in manialink units per plain character, measured on screenshots:
    // textsize 1 at scale 1 is about 0.5 units per character, textsize 2 about 0.75.
    private const double CharWidth1 = 0.5;
    private const double CharWidth2 = 0.75;

    // cut to fit the width, with "…" - a label never wraps or runs over the box edge
    private static string Fit(string text, double units, double unitsPerChar) => TruncateForDisplayWidth(text, units / unitsPerChar);

    // the pieces joined with " · ", wrapped onto as many lines as needed (each piece stays whole)
    private static List<string> WrapPieces(IReadOnlyList<string> pieces, double units, double unitsPerChar)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var piece in pieces)
        {
            var candidate = current.Length == 0 ? piece : current + "$z$FFF · " + piece;
            if (current.Length > 0 && MeasureDisplayWidth(StripTmCodes(candidate), int.MaxValue, out _) * unitsPerChar > units)
            {
                lines.Add(current);
                current = piece;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private const double MapPanelWidth = 20.8;

    private string BuildMapPanel(bool collapsed)
    {
        const double width = MapPanelWidth;
        const double inner = width - 1.6;
        var b = new StringBuilder();
        b.Append("<frame posn=\"-63.6 47.6 1\">");

        var title = $"Current Map $AAA· {ShortPresetName()}";

        if (collapsed)
        {
            AppendWidgetFrame(b, width, 2.8, "Icons128x128_1", "NewTrack", title, ActionHudCollapseMap, collapsed: true);
            b.Append("</frame>");
            return b.ToString();
        }

        TmxRules.TrackDetails? details;
        string? rawName;
        int? trackId;
        lock (hudGate)
        {
            details = currentMapDetailsTrackId == currentMapTrackId ? currentMapDetails : null;
            rawName = currentMapRawName;
            trackId = currentMapTrackId;
        }

        // the name is one line, cut with "…"; the details wrap onto a further line when an author
        // name or a tag is long
        var name = Fit(rawName ?? details?.Name ?? "Loading...", inner, CharWidth2);
        var infoLines = details is null
            ? [trackId is { } id ? $"TMX #{id}" : "not a TMX map"]
            : WrapPieces(
                [
                    $"by {Fit(details.Author, 12, CharWidth1 * 0.9)}",
                    $"{DifficultyColorCode(details.DifficultyLabel)}{details.DifficultyLabel}",
                    $"$FC0{details.Awards} award{(details.Awards == 1 ? "" : "s")}",
                    .. details.Tags.Take(1).Select(t => Fit(t, 8, CharWidth1 * 0.9)),
                ], inner, CharWidth1 * 0.9);

        var height = 2.9 + 2.7 + infoLines.Count * 1.9 + 1.9 + 2.7;
        AppendWidgetFrame(b, width, height, "Icons128x128_1", "NewTrack", title, ActionHudCollapseMap, collapsed: false);

        var y = -3.0;
        b.Append($"<label posn=\"0.8 {Inv(y)} 0.02\" sizen=\"{Inv(inner)} 2.6\" textsize=\"2\" text=\"{Esc(name)}\"/>");
        y -= 2.7;

        foreach (var line in infoLines)
        {
            b.Append($"<label posn=\"0.8 {Inv(y)} 0.02\" sizen=\"{Inv(inner / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(line)}\"/>");
            y -= 1.9;
        }

        var authorTime = currentMap?.AuthorTime ?? details?.AuthorTimeMs ?? 0;
        // the game's own author medal in front of the author time
        var timesLine = $"$FFF{FormatTime(authorTime)}"
            + (details?.WrTimeMs is { } wr ? $"$z   $AAATMX WR $8DF{FormatTime(wr)}" : details is not null ? "$z   $AAANo TMX record yet" : string.Empty);
        b.Append($"<quad posn=\"0.6 {Inv(y + 0.25)} 0.02\" sizen=\"2.1 2.1\" style=\"MedalsBig\" substyle=\"MedalNadeo\"/>");
        b.Append($"<label posn=\"2.9 {Inv(y)} 0.02\" sizen=\"{Inv((inner - 2.1) / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(timesLine)}\"/>");
        y -= 2.0;

        // buttons: TMX logo, Skip vote, Map vote - the panel is exactly as wide as they need
        var x = 0.8;
        void Button(double w, string text, string attributes)
        {
            b.Append($"<quad posn=\"{Inv(x)} {Inv(y)} 0.02\" sizen=\"{Inv(w)} 2.2\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" {attributes}/>");
            b.Append($"<label posn=\"{Inv(x + w / 2)} {Inv(y - 1.1)} 0.03\" sizen=\"{Inv(w)} 2\" halign=\"center\" valign=\"center\" textsize=\"1\" scale=\"0.85\" text=\"{Esc(text)}\"/>");
            x += w + 0.4;
        }

        if (trackId is { } tmxId)
        {
            b.Append($"<quad posn=\"{Inv(x)} {Inv(y)} 0.02\" sizen=\"5.2 2.2\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\" url=\"{tmxRules.GetSiteUrl()}/trackshow/{tmxId}\"/>");
            b.Append($"<quad posn=\"{Inv(x + 0.5)} {Inv(y - 0.15)} 0.03\" sizen=\"4.2 1.9\" image=\"{AssetBaseUrl}/tmx-logo.png\"/>");
            x += 5.6;
        }

        Button(6.6, "Skip vote", $"action=\"{ActionHudSkipVote}\"");
        Button(6.6, "Map vote", $"action=\"{ActionHudMapVoteHint}\"");

        b.Append("</frame>");
        return b.ToString();
    }

    private string BuildToolbar()
    {
        const double tile = 4.6;
        const double gap = 0.3;
        const double height = 5.6;
        const double wideTile = 9.6;

        var host = string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost;
        var assetBaseUrl = $"http://{host}:{config.ReplayServerPort}";

        var openEventMaps = eventMode.Enabled && eventMode.AllMaps.Count > 0 ? eventMode.OpenMaps.Count : (int?)null;
        var players = lastPolledPlayers.Count;
        var session = sessionStopwatch is { } sw ? sw.Elapsed : (TimeSpan?)null;

        var tiles = new List<(double Width, Action<StringBuilder, double> Draw, string? Attributes)>
        {
            (tile, (b, w) => TileText(b, w, cachedMaxPlayers is { } max ? $"{players}/{max}" : players.ToString(CultureInfo.InvariantCulture), "PLAYERS"), null),
            (tile, (b, w) => TileText(b, w, openEventMaps is { } open ? open.ToString(CultureInfo.InvariantCulture) : mapsStartedCount.ToString(CultureInfo.InvariantCulture), openEventMaps is null ? "MAPS" : "OPEN"), null),
            (tile, (b, w) => TileText(b, w, session is { } s ? $"{(int)s.TotalHours}:{s.Minutes:00}" : "--", "SESSION"), null),
            (wideTile, (b, w) => TileText(b, w, ShortPresetName(), config.PresetLocked ? "PRESET · LOCKED" : "PRESET · VOTE"), config.PresetLocked ? null : $"action=\"{ActionHudPresets}\""),
            (tile, (b, w) => TileIcon(b, w, "style=\"Icons128x128_1\" substyle=\"LoadTrack\"", "HISTORY"), $"action=\"{ActionHudHistory}\""),
            (tile, (b, w) => TileIcon(b, w, $"image=\"{assetBaseUrl}/discord.png\"", "DISCORD"), $"url=\"{DiscordInviteUrlNoScheme}\""),
            (tile, (b, w) => TileIcon(b, w, $"image=\"{assetBaseUrl}/favorite.png\"", "FAVORITE"), $"manialink=\"{FavoriteServerUrl}\""),
            (tile, (b, w) => TileIcon(b, w, "style=\"Icons128x128_1\" substyle=\"Advanced\"", "HUD · F7"), $"action=\"{ActionHudHelp}\""),
        };

        var total = tiles.Sum(t => t.Width) + gap * (tiles.Count - 1);
        var sb = new StringBuilder();
        sb.Append($"<frame posn=\"{Inv(63.6 - total)} 47.6 1\">");

        var x = 0.0;
        foreach (var (w, draw, attributes) in tiles)
        {
            sb.Append($"<frame posn=\"{Inv(x)} 0 0\">");
            AppendBox(sb, w, height, attributes, shade: "0003");
            draw(sb, w);
            sb.Append("</frame>");
            x += w + gap;
        }

        sb.Append("</frame>");
        return sb.ToString();

        static void TileText(StringBuilder b, double w, string value, string label)
        {
            b.Append($"<label posn=\"{Inv(w / 2)} -1.3 0.01\" sizen=\"{Inv(w - 0.4)} 2\" halign=\"center\" textsize=\"1\" text=\"{Esc(value)}\"/>");
            b.Append($"<label posn=\"{Inv(w / 2)} -3.7 0.01\" sizen=\"{Inv((w - 0.2) / 0.6)} 2\" halign=\"center\" textsize=\"1\" scale=\"0.6\" textcolor=\"FC0F\" text=\"{Esc(label)}\"/>");
        }

        static void TileIcon(StringBuilder b, double w, string iconAttributes, string label)
        {
            b.Append($"<quad posn=\"{Inv(w / 2 - 1.6)} -0.2 0.01\" sizen=\"3.2 3.2\" {iconAttributes}/>");
            b.Append($"<label posn=\"{Inv(w / 2)} -3.7 0.01\" sizen=\"{Inv((w - 0.2) / 0.6)} 2\" halign=\"center\" textsize=\"1\" scale=\"0.6\" textcolor=\"FC0F\" text=\"{Esc(label)}\"/>");
        }
    }

    private string BuildTopFinishers(string login, bool collapsed)
    {
        const double width = 15.5;
        var top = leaderboard.GetTop(10);
        var own = leaderboard.GetRank(login);
        var ownInTop = own is { } o && o.Position <= top.Count;

        var b = new StringBuilder();
        b.Append($"<frame posn=\"{Inv(63.6 - width)} 23.5 1\">");

        if (collapsed)
        {
            AppendWidgetFrame(b, width, 2.8, "Icons128x128_1", "Rankings", "Top Finishers", ActionHudCollapseTop, collapsed: true);
            b.Append("</frame>");
            return b.ToString();
        }

        var rows = top.Count + (own is not null && !ownInTop ? 1 : 0);
        var height = 3.3 + Math.Max(rows, 1) * LineHeight + (own is not null && !ownInTop ? 0.6 : 0) + 0.4;
        AppendWidgetFrame(b, width, height, "Icons128x128_1", "Rankings", "Top Finishers", ActionHudCollapseTop, collapsed: false);

        // Records Eyepiece colours: top 3 yellow, ahead of you red, you green, behind you grey
        string CountColor(int position) =>
            own is { } me && position == me.Position ? "3F5F"
            : position <= 3 ? "FF0F"
            : own is { } me2 && position < me2.Position ? "F33F"
            : own is null ? "FFFF" : "CCCF";

        // dark bar behind the top 3, like the records widgets
        b.Append($"<quad posn=\"0.4 -2.6 0.01\" sizen=\"{Inv(width - 0.8)} {Inv(Math.Min(3, top.Count) * LineHeight + 0.3)}\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\"/>");

        var y = -2.75;
        for (var i = 0; i < top.Count; i++)
        {
            var position = i + 1;
            if (own is { } me && me.Position == position)
            {
                b.Append($"<quad posn=\"0.4 {Inv(y + 0.15)} 0.015\" sizen=\"{Inv(width - 0.8)} {Inv(LineHeight + 0.1)}\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\"/>");
            }

            AppendRankRow(b, y, position, top[i].Finishes, string.IsNullOrEmpty(top[i].RawNickname) ? top[i].LastNickname : top[i].RawNickname, CountColor(position), width);
            y -= LineHeight;
        }

        if (top.Count == 0)
        {
            b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv(width - 2)} 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"CCCF\" text=\"No finishes yet\"/>");
        }

        if (own is { } mine && !ownInTop)
        {
            y -= 0.6;
            b.Append($"<quad posn=\"0.4 {Inv(y + 0.15)} 0.015\" sizen=\"{Inv(width - 0.8)} {Inv(LineHeight + 0.1)}\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\"/>");
            AppendRankRow(b, y, mine.Position, mine.Finishes, GetRawNickname(login), "3F5F", width);
        }

        b.Append("</frame>");
        return b.ToString();
    }

    private static void AppendRankRow(StringBuilder b, double y, int position, int count, string nickname, string countColor, double width)
    {
        b.Append($"<label posn=\"2.6 {Inv(y)} 0.02\" sizen=\"2.4 2\" halign=\"right\" textsize=\"1\" scale=\"0.9\" text=\"{position}.\"/>");
        b.Append($"<label posn=\"2.9 {Inv(y)} 0.02\" sizen=\"3.4 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"{countColor}\" text=\"{count}\"/>");
        b.Append($"<label posn=\"6.4 {Inv(y)} 0.02\" sizen=\"{Inv((width - 7) / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(Fit(nickname, width - 7, CharWidth1 * 0.9))}\"/>");
    }

    private string BuildEventPanel(string login, bool collapsed)
    {
        if (!eventMode.Enabled || eventMode.AllMaps.Count == 0)
        {
            return string.Empty;
        }

        const double width = 17.5;
        var all = eventMode.AllMaps;
        var open = eventMode.OpenMaps;
        var winner = eventMode.EndedAt is not null ? eventMode.Standings().FirstOrDefault() : null;
        var title = winner is not null
            ? $"{Fit(eventMode.Title, 5, CharWidth1)} $AAA· $z{Fit(winner.Nickname, 5, CharWidth1)}"
            : $"{Fit(eventMode.Title, 6.5, CharWidth1)} $AAA· {all.Count - open.Count}/{all.Count}";

        var b = new StringBuilder();
        b.Append("<frame posn=\"-63.6 23.5 1\">");

        if (collapsed)
        {
            AppendWidgetFrame(b, width, 2.8, "Icons128x128_1", "Challenge", title, ActionHudCollapseEvent, collapsed: true);
            b.Append("</frame>");
            return b.ToString();
        }

        var pages = Math.Max(1, (open.Count + EventRowsPerPage - 1) / EventRowsPerPage);
        var page = Math.Clamp(eventPage.GetValueOrDefault(login, 0), 0, pages - 1);
        eventPage[login] = page;
        var shown = open.Skip(page * EventRowsPerPage).Take(EventRowsPerPage).ToArray();
        eventRowsShown[login] = [.. shown.Select(m => m.TrackId)];

        var others = eventMode.OtherServers();
        var recent = eventMode.RecentFinishes(3);
        var runningHere = currentMapTrackId;
        var runningElsewhere = others.Where(s => s.TrackId is not null).ToDictionary(s => s.TrackId!.Value, s => s.Label);

        var rowsHeight = Math.Max(shown.Length, 1) * LineHeight;
        var serversHeight = others.Count > 0 ? 2.2 + others.Count * LineHeight : 0;
        var recentHeight = recent.Count > 0 ? 2.2 + recent.Count * 2 * LineHeight : 0;
        var height = 3.3 + rowsHeight + 2.4 + serversHeight + recentHeight + 0.3;

        AppendWidgetFrame(b, width, height, "Icons128x128_1", "Challenge", title, ActionHudCollapseEvent, collapsed: false);

        var y = -2.75;
        if (shown.Length == 0)
        {
            b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv(width - 2)} 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"3F5F\" text=\"All maps finished - GG!\"/>");
            y -= LineHeight;
        }

        const double nameX = 3.0;
        const double tagWidth = 4.4;
        var nameWidth = width - 0.8 - nameX - tagWidth - 0.3;

        for (var i = 0; i < shown.Length; i++)
        {
            var map = shown[i];
            var number = page * EventRowsPerPage + i + 1;
            eventMapDetails.TryGetValue(map.TrackId, out var d);
            var name = EventMapName(map.TrackId);
            var tag = map.TrackId == runningHere ? "$0F0here"
                : runningElsewhere.TryGetValue(map.TrackId, out var label) ? $"$FC0{Fit(label, tagWidth, CharWidth1 * 0.8)}"
                : map.Unlimiter ? "$F80Unlim."
                : d is not null ? $"$AAA{FormatTime(d.AuthorTimeMs)}" : string.Empty;

            if (map.TrackId == runningHere)
            {
                b.Append($"<quad posn=\"0.4 {Inv(y + 0.15)} 0.015\" sizen=\"{Inv(width - 0.8)} {Inv(LineHeight + 0.1)}\" style=\"BgsPlayerCard\" substyle=\"BgCardSystem\"/>");
            }

            // the whole row votes for the map
            b.Append($"<quad posn=\"0.4 {Inv(y + 0.15)} 0.016\" sizen=\"{Inv(width - 0.8)} {Inv(LineHeight)}\" bgcolor=\"0000\" action=\"{ActionEventRowBase + i}\"/>");
            b.Append($"<label posn=\"2.6 {Inv(y)} 0.02\" sizen=\"2.4 2\" halign=\"right\" textsize=\"1\" scale=\"0.9\" text=\"{number}.\"/>");
            b.Append($"<label posn=\"{Inv(nameX)} {Inv(y)} 0.02\" sizen=\"{Inv(nameWidth / 0.9)} 2\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(Fit(name, nameWidth, CharWidth1 * 0.9))}\"/>");
            b.Append($"<label posn=\"{Inv(width - 0.8)} {Inv(y)} 0.02\" sizen=\"{Inv(tagWidth / 0.8)} 2\" halign=\"right\" textsize=\"1\" scale=\"0.8\" text=\"{Esc(tag)}\"/>");
            y -= LineHeight;
        }

        // pages
        y -= 0.3;
        b.Append($"<quad posn=\"0.8 {Inv(y + 0.1)} 0.02\" sizen=\"2 2\" style=\"Icons64x64_1\" substyle=\"ArrowPrev\" action=\"{ActionEventPagePrev}\"/>");
        b.Append($"<label posn=\"{Inv(width / 2)} {Inv(y - 0.2)} 0.02\" sizen=\"{Inv((width - 6) / 0.8)} 2\" halign=\"center\" textsize=\"1\" scale=\"0.8\" textcolor=\"CCCF\" text=\"{Esc($"{page + 1}/{pages} · {open.Count} open · click = vote")}\"/>");
        b.Append($"<quad posn=\"{Inv(width - 2.8)} {Inv(y + 0.1)} 0.02\" sizen=\"2 2\" style=\"Icons64x64_1\" substyle=\"ArrowNext\" action=\"{ActionEventPageNext}\"/>");
        y -= 2.1;

        if (others.Count > 0)
        {
            b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv((width - 2) / 0.8)} 2\" textsize=\"1\" scale=\"0.8\" textcolor=\"FC0F\" text=\"OTHER SERVERS $AAA· click = join\"/>");
            y -= 1.9;
            foreach (var server in others)
            {
                var mapName = server.TrackId is { } sid ? EventMapName(sid) : server.MapName ?? "-";
                b.Append($"<quad posn=\"0.4 {Inv(y + 0.15)} 0.016\" sizen=\"{Inv(width - 0.8)} {Inv(LineHeight)}\" bgcolor=\"0000\" manialink=\"tmtp://#join={Esc(server.Login)}\"/>");
                b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv(4 / 0.85)} 2\" textsize=\"1\" scale=\"0.85\" textcolor=\"FC0F\" text=\"{Esc(Fit(server.Label, 4, CharWidth1 * 0.85))}\"/>");
                b.Append($"<label posn=\"5.2 {Inv(y)} 0.02\" sizen=\"{Inv(7.6 / 0.85)} 2\" textsize=\"1\" scale=\"0.85\" text=\"{Esc(Fit(mapName, 7.6, CharWidth1 * 0.85))}\"/>");
                b.Append($"<label posn=\"{Inv(width - 0.8)} {Inv(y)} 0.02\" sizen=\"{Inv(3.6 / 0.8)} 2\" halign=\"right\" textsize=\"1\" scale=\"0.8\" text=\"{Esc($"{server.Players}/{server.MaxPlayers}")}\"/>");
                y -= LineHeight;
            }
        }

        if (recent.Count > 0)
        {
            y -= 0.3;
            b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv((width - 2) / 0.8)} 2\" textsize=\"1\" scale=\"0.8\" textcolor=\"FC0F\" text=\"JUST FINISHED\"/>");
            y -= 1.9;
            foreach (var (trackId, finish) in recent)
            {
                var mapName = EventMapName(trackId);
                var ago = DateTimeOffset.UtcNow - finish.At;
                var agoText = ago.TotalMinutes < 1 ? "now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m ago" : $"{(int)ago.TotalHours}h ago";

                // first line the map and when, second line who and in what time
                b.Append($"<label posn=\"1 {Inv(y)} 0.02\" sizen=\"{Inv(11.4 / 0.85)} 2\" textsize=\"1\" scale=\"0.85\" text=\"{Esc(Fit(mapName, 11.4, CharWidth1 * 0.85))}\"/>");
                b.Append($"<label posn=\"{Inv(width - 0.8)} {Inv(y)} 0.02\" sizen=\"{Inv(4.4 / 0.75)} 2\" halign=\"right\" textsize=\"1\" scale=\"0.75\" textcolor=\"AAAF\" text=\"{Esc(agoText)}\"/>");
                y -= LineHeight;
                b.Append($"<label posn=\"2 {Inv(y)} 0.02\" sizen=\"{Inv(9.4 / 0.8)} 2\" textsize=\"1\" scale=\"0.8\" text=\"{Esc(Fit(finish.Nickname, 9.4, CharWidth1 * 0.8))}\"/>");
                b.Append($"<label posn=\"{Inv(width - 0.8)} {Inv(y)} 0.02\" sizen=\"{Inv(5 / 0.8)} 2\" halign=\"right\" textsize=\"1\" scale=\"0.8\" text=\"{Esc($"$3F5{FormatTime(finish.Time)}")}\"/>");
                y -= LineHeight;
            }
        }

        b.Append("</frame>");
        return b.ToString();
    }

    // The checkpoint counter as in the sketches: one small pill "CP 3/7 -0.42" with up to ten
    // progress bars under it. Left of the race clock by default, or centred above it (HUD menu).
    private string BuildCheckpointCounter(string login)
    {
        if (currentMapCheckpointTotal is not { } total || total <= 0)
        {
            return string.Empty;
        }

        int current;
        int? split = null;
        lock (hudGate)
        {
            current = playerCheckpointProgress.GetValueOrDefault(login, 0);
            if (current > 0 && runCheckpoints.TryGetValue(login, out var run) && run.Count > 0)
            {
                split = CheckpointSplit(login, run.Count - 1);
            }
        }

        current = Math.Min(current, total);
        var splitText = split is { } s ? (s <= 0 ? $"  $3F5-{FormatSplit(-s)}" : $"  $F33+{FormatSplit(s)}") : string.Empty;
        var text = $"$FC0CP $FFF{current}$AAA/{total}{splitText}";

        const double width = 9;
        const double height = 3.4;
        var aboveClock = hudSettings.Get(login).CpAboveClock;
        // Lower than this above the clock and the game's own timer panel covers the counter
        // (seen at -39.2; -37.4 was visible).
        var (x, y) = aboveClock ? (-width / 2, -37.8) : (-21.5, -44.6);

        var b = new StringBuilder($"<frame posn=\"{Inv(x)} {Inv(y)} 1\">");
        AppendBox(b, width, height);
        b.Append($"<label posn=\"{Inv(width / 2)} -1.25 0.01\" sizen=\"{Inv((width - 0.6) / 0.9)} 2\" halign=\"center\" valign=\"center\" textsize=\"1\" scale=\"0.9\" text=\"{Esc(text)}\"/>");

        // At most 10 bars; with more checkpoints each bar stands for an even share of them
        // (20 CPs: 2 each; 12 CPs: 1 or 2) and lights up once all of its share is passed.
        var bars = Math.Min(total, 10);
        const double gap = 0.15;
        var barWidth = (width - 1.4 - gap * (bars - 1)) / bars;
        for (var i = 0; i < bars; i++)
        {
            var threshold = (int)Math.Round((i + 1) * (double)total / bars, MidpointRounding.AwayFromZero);
            var lit = current >= threshold;
            b.Append($"<quad posn=\"{Inv(0.7 + i * (barWidth + gap))} -2.35 0.01\" sizen=\"{Inv(barWidth)} 0.45\" bgcolor=\"{(lit ? "FC0F" : "FFF5")}\"/>");
        }

        b.Append("</frame>");
        return b.ToString();
    }

    private static string FormatSplit(int ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);

    // personal best, server best and TMX record on this map - a widget like the others (title bar,
    // fold arrow), an icon per line and the TMX logo for the TMX record
    private string BuildTimesBlock(string login, bool collapsed)
    {
        int? own;
        int? best;
        int? wr;
        lock (hudGate)
        {
            own = mapBestTimes.TryGetValue(login, out var t) ? t : null;
            best = mapBestTimes.Count > 0 ? mapBestTimes.Values.Min() : null;
            wr = currentMapDetailsTrackId == currentMapTrackId ? currentMapDetails?.WrTimeMs : null;
        }

        const double width = 14;
        // below the top finishers even with ten rows and the player's own row (those end near y=0)
        var b = new StringBuilder($"<frame posn=\"{Inv(63.6 - width)} -22 1\">");

        if (collapsed)
        {
            AppendWidgetFrame(b, width, 2.8, "Icons128x128_1", "Race", "Times", ActionHudCollapseTimes, collapsed: true);
            b.Append("</frame>");
            return b.ToString();
        }

        AppendWidgetFrame(b, width, 9.3, "Icons128x128_1", "Race", "Times", ActionHudCollapseTimes, collapsed: false);

        (string Icon, string Label, string Value)[] rows =
        [
            ("style=\"MedalsBig\" substyle=\"MedalGold\"", "Your best", own is { } o ? "$3F5" + FormatTime(o) : "$AAA--"),
            ("style=\"Icons128x128_1\" substyle=\"Rankings\"", "Server", best is { } sb ? "$FF0" + FormatTime(sb) : "$AAA--"),
            ($"image=\"{AssetBaseUrl}/tmx-logo.png\"", string.Empty, wr is { } w ? "$8DF" + FormatTime(w) : "$AAA--"),
        ];

        var y = -3.1;
        foreach (var (icon, label, value) in rows)
        {
            var isLogo = label.Length == 0;
            b.Append(isLogo
                ? $"<quad posn=\"0.8 {Inv(y + 0.1)} 0.02\" sizen=\"4 1.8\" {icon}/>"
                : $"<quad posn=\"0.9 {Inv(y + 0.15)} 0.02\" sizen=\"1.9 1.9\" {icon}/>");
            if (!isLogo)
            {
                b.Append($"<label posn=\"3.2 {Inv(y)} 0.02\" sizen=\"6 2\" textsize=\"1\" scale=\"0.9\" textcolor=\"DDDF\" text=\"{Esc(label)}\"/>");
            }

            b.Append($"<label posn=\"{Inv(width - 0.9)} {Inv(y)} 0.02\" sizen=\"7 2\" halign=\"right\" textsize=\"1\" text=\"{Esc(value)}\"/>");
            y -= 2.0;
        }

        b.Append("</frame>");
        return b.ToString();
    }
}
