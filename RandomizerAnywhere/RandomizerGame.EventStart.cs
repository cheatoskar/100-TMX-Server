using System.Collections.Concurrent;
using System.Globalization;

namespace RandomizerAnywhere;

// EventStartsAt: the event opens at a set time, without a restart. Until then every player except
// the admins is held as a spectator (they can join and watch) and no finish counts for the event; at
// the time the server switches to EventStartPreset, lets everyone play and says so. A controller
// started after that time switches right away, unless the event has already ended.
internal sealed partial class RandomizerGame
{
    private readonly ConcurrentDictionary<string, byte> heldForEventStart = new(StringComparer.OrdinalIgnoreCase);
    private int eventStartDone;

    private bool EventStartPending => eventMode.Enabled && config.EventStartsAt is { } at && DateTimeOffset.UtcNow < at && Volatile.Read(ref eventStartDone) == 0;

    private bool IsAdmin(string login) => config.AdminLogins.Count == 0 || config.AdminLogins.Contains(login);

    // before the start nothing is voted: no map is played, skipped or loaded ahead of everyone
    private async Task<bool> RefusedBeforeEventStartAsync(string login, CancellationToken cancellationToken)
    {
        if (!EventStartPending || config.AdminLogins.Contains(login))
        {
            return false;
        }

        await SendMessageAsync(login, $"$FC0» Votes open when {eventMode.Title} starts at {EventStartClock()}.", cancellationToken);
        return true;
    }

    // "/event start": an admin opens the event now instead of at EventStartsAt
    private async Task<bool> TryStartEventByHandAsync(string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !args[0].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsAdmin(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can start the event.", cancellationToken);
        }
        else if (!EventStartPending)
        {
            await SendMessageAsync(login, "The event is not waiting for its start.", cancellationToken);
        }
        else
        {
            // a chat command holds the game lock; the preset switch takes it again - so not inline
            Console.WriteLine($"Event: started by hand by {login}.");
            RunInBackground("starting the event by hand", () => StartEventAsync(appToken));
        }

        return true;
    }

    // "20:00" in the HUD's clock zone
    private string EventStartClock()
    {
        if (config.EventStartsAt is not { } at)
        {
            return string.Empty;
        }

        var local = ClockZone is null ? at.ToUniversalTime() : TimeZoneInfo.ConvertTime(at, ClockZone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture) + (ClockZone is null ? " UTC" : string.Empty);
    }

    private async Task HoldForEventStartAsync(string login, CancellationToken cancellationToken)
    {
        if (!EventStartPending || config.AdminLogins.Contains(login))
        {
            return;
        }

        try
        {
            await client.TracedCallAsync("ForceSpectator", [login, 1], cancellationToken);
            if (heldForEventStart.TryAdd(login, 0))
            {
                await SendMessageAsync(login, $"$FC0» {eventMode.Title} starts at {EventStartClock()} - you are watching until then, everyone starts together.", cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"Warning: couldn't hold {login} as a spectator until the event starts - {ex.Message}");
        }
    }

    // Called once when the controller is up.
    private void ScheduleEventStart()
    {
        if (!eventMode.Enabled || config.EventStartsAt is not { } at || eventMode.EndedAt is not null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < at)
        {
            Console.WriteLine($"Event: starts at {at:yyyy-MM-dd HH:mm zzz} - until then players watch and no finish counts; then preset '{config.EventStartPreset}'.");
        }

        RunInBackground("starting the event", async () =>
        {
            // whoever is on the server already (and anyone the connect callback missed)
            while (EventStartPending && !appToken.IsCancellationRequested)
            {
                foreach (var player in lastPolledPlayers)
                {
                    if (!heldForEventStart.ContainsKey(player.Login))
                    {
                        await HoldForEventStartAsync(player.Login, appToken);
                    }
                }

                var left = at - DateTimeOffset.UtcNow;
                await Task.Delay(left < TimeSpan.FromSeconds(10) ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : TimeSpan.FromSeconds(10), appToken);
            }

            await StartEventAsync(appToken);
        });
    }

    private async Task StartEventAsync(CancellationToken cancellationToken)
    {
        if (eventMode.EndedAt is not null || Interlocked.Exchange(ref eventStartDone, 1) == 1)
        {
            return;
        }

        var late = config.EventStartsAt is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(1);

        if (!string.IsNullOrWhiteSpace(config.EventStartPreset))
        {
            await RunUnderGameLockAsync("switching to the event preset", async token =>
            {
                var name = ResolvePresetName(config.EventStartPreset);
                var (preset, error) = LoadPresetForThisGame(name);
                if (preset is null)
                {
                    Console.WriteLine($"Warning: EventStartPreset '{config.EventStartPreset}' can't be used - {error}. Staying on the current preset.");
                    return;
                }

                if (config.LastPreset?.DisplayName == preset.DisplayName)
                {
                    return;
                }

                var wasActive = SessionActive;
                if (wasActive)
                {
                    ResetSessionStateForPresetSwitch();
                }

                ApplyPreset(name, preset);

                if (wasActive)
                {
                    await StartSessionAsync(token);
                }
            });
        }

        // everyone may play now: the held ones are put back as players, the rest only unlocked
        IReadOnlyList<PlayerSummary> players;
        try
        {
            players = await client.GetPlayersAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            players = lastPolledPlayers;
        }

        foreach (var player in players)
        {
            try
            {
                if (heldForEventStart.ContainsKey(player.Login))
                {
                    await client.TracedCallAsync("ForceSpectator", [player.Login, 2], cancellationToken);
                }

                await client.TracedCallAsync("ForceSpectator", [player.Login, 0], cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Warning: couldn't let {player.Login} play at the event start - {ex.Message}");
            }
        }

        heldForEventStart.Clear();
        Console.WriteLine($"Event: '{eventMode.Title}' started{(late ? " (controller started after the start time)" : string.Empty)}.");

        if (!late)
        {
            await SendMessageAsync($"$FC0» {eventMode.Title} has started! Everyone plays now - good luck!", cancellationToken);
        }

        await BroadcastHudAsync(HudSection.Event, cancellationToken);
    }

    // The last open map was just finished: before the results, one direct look at TMX, so maps
    // uploaded since the site's last sync (up to ~25 min) still join. True = the list grew.
    private async Task<bool> EventListGrewOnTmxAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            var before = eventMode.AllMaps.Count;
            var (ids, complete) = await tmxRules.ListEventPoolAsync(budget.Token);
            var unlimiter = await tmxRules.ListEventUnlimiterIdsAsync(budget.Token);
            await eventMode.MergeTmxPoolAsync([.. ids.Where(id => !tmxRules.IsExcludedFromRotation(id))], complete, unlimiter, force: true);
            var grown = eventMode.AllMaps.Count - before;
            if (grown > 0)
            {
                await LoadEventMapDetailsAsync(cancellationToken);
                Console.WriteLine($"Event: the last map was finished, but TMX has {grown} new open map(s) - the event goes on.");
            }

            return eventMode.OpenMaps.Count > 0;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // the site's list was current a few minutes ago: end on that
            Console.WriteLine($"Warning: couldn't check TMX for new maps before the event ends - {ex.Message}. Ending on the current list.");
            return false;
        }
    }
}
