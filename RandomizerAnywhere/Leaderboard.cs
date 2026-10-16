using System.Text.Json;

namespace RandomizerAnywhere;

// LastNickname is stripped of $-formatting codes (safe for Discord/the status page).
// RawNickname keeps the original $-codes so the in-game HUD can show the player's real name color.
internal sealed record LeaderboardEntry(string LastNickname, int Finishes, string RawNickname = "");

internal sealed class Leaderboard
{
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "leaderboard.json");

    // The version before the last save. Every save keeps one, so a damaged leaderboard.json
    // costs at most the very last finish.
    private string BackupPath => filePath + ".bak";

    private readonly Dictionary<string, LeaderboardEntry> entries = [];
    private readonly object gate = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath) && !File.Exists(BackupPath))
        {
            return;
        }

        // The leaderboard is the one piece of data that can't be rebuilt. If it can't be read,
        // starting empty used to mean the next finish saved a one-entry board over everything.
        // Now: try the backup, and move the unreadable file aside instead of overwriting it.
        if (await TryLoadFromAsync(filePath, cancellationToken))
        {
            return;
        }

        var quarantined = AtomicFile.QuarantineCorrupt(filePath);

        if (await TryLoadFromAsync(BackupPath, cancellationToken))
        {
            Console.WriteLine($"ERROR: leaderboard.json was unreadable{(quarantined is null ? string.Empty : $" (kept as {Path.GetFileName(quarantined)})")} - loaded the backup from before the last save instead.");
            return;
        }

        Console.WriteLine($"ERROR: neither leaderboard.json nor its backup could be read{(quarantined is null ? string.Empty : $" - the file was kept as {Path.GetFileName(quarantined)}")}. Starting with an empty leaderboard.");
    }

    private async Task<bool> TryLoadFromAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LeaderboardEntry>>(json)
                ?? throw new JsonException("the file is empty or 'null'");

            lock (gate)
            {
                entries.Clear();
                foreach (var (login, entry) in loaded)
                {
                    entries[login] = entry;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine($"Warning: failed to read {Path.GetFileName(path)} - {ex.Message}");
            return false;
        }
    }

    public async Task RecordFinishAsync(string login, string nickname, string rawNickname, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var finishes = entries.TryGetValue(login, out var existing) ? existing.Finishes + 1 : 1;
            entries[login] = new LeaderboardEntry(nickname, finishes, rawNickname);
        }

        await SaveAsync(cancellationToken);
    }

    public IReadOnlyList<LeaderboardEntry> GetTop(int count)
    {
        lock (gate)
        {
            return entries.Values
                .OrderByDescending(e => e.Finishes)
                .Take(count)
                .ToList();
        }
    }

    public (int Position, int Finishes)? GetRank(string login)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(login, out var entry))
            {
                return null;
            }

            var position = entries.Values.Count(e => e.Finishes > entry.Finishes) + 1;
            return (position, entry.Finishes);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, LeaderboardEntry> snapshot;
        lock (gate)
        {
            snapshot = new Dictionary<string, LeaderboardEntry>(entries);
        }

        // no cancellation once the write has started - a half-written leaderboard is the one
        // outcome that must not happen (see AtomicFile)
        var json = JsonSerializer.Serialize(snapshot);
        await AtomicFile.WriteAllTextAsync(filePath, json, BackupPath);
    }
}
