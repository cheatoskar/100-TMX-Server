using System.Text.Json;

namespace RandomizerAnywhere;

// The parts of the in-game HUD a player can switch off or fold up.
internal enum HudPart
{
    Map,
    Top,
    Event,
    Cp,
    Times,
    Toolbar,
}

internal sealed class HudPrefs
{
    // switched off with /hud <part> off or in the HUD menu
    public HashSet<HudPart> Hidden { get; set; } = [];

    // folded to their title bar with the "-" button
    public HashSet<HudPart> Collapsed { get; set; } = [];

    // F7: everything off at once, for racing
    public bool AllHidden { get; set; }

    // the checkpoint counter centred above the race clock instead of left of it
    public bool CpAboveClock { get; set; }

    public bool Shows(HudPart part) => !AllHidden && !Hidden.Contains(part);
}

// Every player's HUD choices, kept in hud-settings.json so they survive a reconnect and a restart.
// Small, written whenever someone changes something; a lost write only costs that one change.
internal sealed class HudSettings
{
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "hud-settings.json");
    private readonly Dictionary<string, HudPrefs> prefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, HudPrefs>>(await File.ReadAllTextAsync(filePath, cancellationToken)) ?? [];

            lock (gate)
            {
                foreach (var (login, p) in loaded)
                {
                    prefs[login] = p;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AtomicFile.QuarantineCorrupt(filePath);
            Console.WriteLine($"Warning: hud-settings.json couldn't be read ({ex.Message}) - everyone starts with the default HUD.");
        }
    }

    // a copy: callers must go through Update to change anything
    public HudPrefs Get(string login)
    {
        lock (gate)
        {
            var p = prefs.GetValueOrDefault(login);
            return p is null
                ? new HudPrefs()
                : new HudPrefs { Hidden = [.. p.Hidden], Collapsed = [.. p.Collapsed], AllHidden = p.AllHidden, CpAboveClock = p.CpAboveClock };
        }
    }

    public async Task UpdateAsync(string login, Action<HudPrefs> change)
    {
        string json;

        lock (gate)
        {
            if (!prefs.TryGetValue(login, out var p))
            {
                p = new HudPrefs();
                prefs[login] = p;
            }

            change(p);

            // nothing worth keeping - don't grow the file with defaults
            if (p.Hidden.Count == 0 && p.Collapsed.Count == 0 && !p.AllHidden && !p.CpAboveClock)
            {
                prefs.Remove(login);
            }

            json = JsonSerializer.Serialize(prefs);
        }

        await writeLock.WaitAsync();
        try
        {
            await AtomicFile.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: couldn't save hud-settings.json - {ex.Message}");
        }
        finally
        {
            writeLock.Release();
        }
    }
}
