using System.Text.Json;
using RandomizerAnywhere.Config;

namespace RandomizerAnywhere;

// LastNickname is stripped of $-formatting codes (safe for Discord/the status page).
// RawNickname keeps the original $-codes so the in-game HUD can show the player's real name color.
internal sealed record LeaderboardEntry(string LastNickname, int Finishes, string RawNickname = "");

// One leaderboard can be shared by several servers (TMNF + TMUF on one box, LeaderboardFile in
// config.toml). The file is then the truth, not this process's memory: a finish is counted under a
// cross-process lock on the freshly read file, and the in-memory copy is re-read whenever the file
// changed. Without that, each server would save its own memory over the other's finishes.
internal sealed class Leaderboard
{
    // How long a finish waits for the other server to finish its write. Writes take milliseconds;
    // past this something is wrong, and the finish is saved without the lock rather than lost.
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly string filePath;

    // The version before the last save. Every save keeps one, so a damaged leaderboard.json
    // costs at most the very last finish.
    private string BackupPath => filePath + ".bak";
    private string LockPath => filePath + ".lock";

    private readonly Dictionary<string, LeaderboardEntry> entries = [];
    private readonly object gate = new();

    // write time of the file as last read or written by this process
    private DateTime loadedWriteTimeUtc;

    public Leaderboard(AppConfig config)
        : this(string.IsNullOrWhiteSpace(config.LeaderboardFile)
            ? Path.Combine(AppContext.BaseDirectory, "leaderboard.json")
            : config.LeaderboardFile)
    {
    }

    internal Leaderboard(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => filePath;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Leaderboard: {filePath}");

        if (!File.Exists(filePath) && !File.Exists(BackupPath))
        {
            return Task.CompletedTask;
        }

        // The leaderboard is the one piece of data that can't be rebuilt. If it can't be read,
        // starting empty used to mean the next finish saved a one-entry board over everything.
        // Now: try the backup, and move the unreadable file aside instead of overwriting it.
        if (TryLoadFrom(filePath))
        {
            return Task.CompletedTask;
        }

        var quarantined = AtomicFile.QuarantineCorrupt(filePath);

        if (TryLoadFrom(BackupPath))
        {
            Console.WriteLine($"ERROR: leaderboard.json was unreadable{(quarantined is null ? string.Empty : $" (kept as {Path.GetFileName(quarantined)})")} - loaded the backup from before the last save instead.");
            return Task.CompletedTask;
        }

        Console.WriteLine($"ERROR: neither leaderboard.json nor its backup could be read{(quarantined is null ? string.Empty : $" - the file was kept as {Path.GetFileName(quarantined)}")}. Starting with an empty leaderboard.");
        return Task.CompletedTask;
    }

    private bool TryLoadFrom(string path, bool quiet = false)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LeaderboardEntry>>(json)
                ?? throw new JsonException("the file is empty or 'null'");

            lock (gate)
            {
                entries.Clear();
                foreach (var (login, entry) in loaded)
                {
                    entries[login] = entry;
                }

                if (path == filePath)
                {
                    loadedWriteTimeUtc = writeTime;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!quiet)
            {
                Console.WriteLine($"Warning: failed to read {Path.GetFileName(path)} - {ex.Message}");
            }

            return false;
        }
    }

    // Picks up finishes the other server saved. Cheap: one stat per call, a read only after a
    // change. Writes are atomic renames, so a reader never sees half a file. An unreadable file
    // keeps the current memory - the startup path decides about quarantining, not this.
    private void RefreshIfChanged()
    {
        DateTime writeTime;
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            writeTime = File.GetLastWriteTimeUtc(filePath);
        }
        catch (IOException)
        {
            return;
        }

        lock (gate)
        {
            if (writeTime == loadedWriteTimeUtc)
            {
                return;
            }
        }

        TryLoadFrom(filePath, quiet: true);
    }

    public async Task RecordFinishAsync(string login, string nickname, string rawNickname, CancellationToken cancellationToken = default)
    {
        // no cancellation inside: a finish that was counted must also be saved
        using var fileLock = await AcquireFileLockAsync();

        // count on top of what is on disk right now, not on this process's possibly older copy
        RefreshIfChanged();

        lock (gate)
        {
            var finishes = entries.TryGetValue(login, out var existing) ? existing.Finishes + 1 : 1;
            entries[login] = new LeaderboardEntry(nickname, finishes, rawNickname);
        }

        await SaveAsync();
    }

    public IReadOnlyList<LeaderboardEntry> GetTop(int count)
    {
        RefreshIfChanged();

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
        RefreshIfChanged();

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

    private async Task SaveAsync()
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

        try
        {
            var writeTime = File.GetLastWriteTimeUtc(filePath);
            lock (gate)
            {
                loadedWriteTimeUtc = writeTime;
            }
        }
        catch (IOException)
        {
            // next refresh just re-reads what we wrote
        }
    }

    // An exclusive lock on leaderboard.json.lock, held for read-count-write. FileShare.None is a
    // real share lock on Windows and an advisory flock() on Linux, which every .NET process here
    // honours. A lock that can't be had in time is logged and the finish saved anyway.
    private async Task<IDisposable?> AcquireFileLockAsync()
    {
        var deadline = DateTime.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Warning: couldn't lock {Path.GetFileName(LockPath)} - {ex.Message}. Saving the finish without the lock.");
                return null;
            }
        }
    }
}
