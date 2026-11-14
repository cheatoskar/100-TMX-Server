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

    // On a server that isn't the hub (SyncHubUrl set) the leaderboard lives on the hub: finishes are
    // sent there, and the board shown here is the hub's, fetched every few seconds. A finish the hub
    // doesn't take (it is restarting, the network hiccups) waits in leaderboard-pending.json and is
    // sent again - a finish is never just dropped.
    private readonly SyncClient? sync;
    private readonly List<SyncFinish> pending = [];

    // one sync at a time: two at once would both send the first pending finish
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private DateTime lastSyncWarning;

    // the hub's memory of finish ids it already counted (see SyncFinish.Id)
    private readonly HashSet<string> countedIds = [];
    private readonly Queue<string> countedOrder = new();
    private string PendingPath => Path.Combine(Path.GetDirectoryName(filePath)!, "leaderboard-pending.json");

    public Leaderboard(AppConfig config, SyncClient sync)
        : this(string.IsNullOrWhiteSpace(config.LeaderboardFile)
            ? Path.Combine(AppContext.BaseDirectory, "leaderboard.json")
            : config.LeaderboardFile)
    {
        this.sync = sync;
    }

    internal Leaderboard(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    private bool Remote => sync is { Enabled: true };

    public string FilePath => filePath;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Remote)
        {
            await LoadRemoteAsync(cancellationToken);
            return;
        }

        await LoadLocalAsync();
    }

    private Task LoadLocalAsync()
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

    // ---- shared through the hub ----

    private async Task LoadRemoteAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Leaderboard: shared, kept by the hub (SyncHubUrl).");

        // finishes that didn't reach the hub before the last stop
        if (File.Exists(PendingPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<SyncFinish>>(await File.ReadAllTextAsync(PendingPath, cancellationToken)) ?? [];
                lock (gate)
                {
                    pending.AddRange(saved);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                AtomicFile.QuarantineCorrupt(PendingPath);
                Console.WriteLine($"Warning: leaderboard-pending.json couldn't be read - {ex.Message}");
            }
        }

        // This server counted on its own before it joined the hub: those finishes move over once,
        // and the file is renamed so they are never sent twice.
        if (File.Exists(filePath) && TryLoadFrom(filePath, quiet: true))
        {
            List<SyncFinish> local;
            lock (gate)
            {
                local = [.. entries.Select(kv => new SyncFinish(kv.Key, kv.Value.LastNickname, kv.Value.RawNickname, kv.Value.Finishes, Guid.NewGuid().ToString("N")))];
                entries.Clear();
            }

            lock (gate)
            {
                pending.AddRange(local);
            }

            await SavePendingAsync();
            File.Move(filePath, filePath + ".moved-to-hub", overwrite: true);
            Console.WriteLine($"Leaderboard: {local.Sum(f => f.Count)} finish(es) of {local.Count} player(s) from this server's own leaderboard go to the hub.");
        }

        await SyncWithHubAsync(cancellationToken);
    }

    // Sends what is pending, then takes the hub's board. Called every few seconds.
    public async Task SyncWithHubAsync(CancellationToken cancellationToken)
    {
        if (!Remote)
        {
            return;
        }

        await syncGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                SyncFinish? next;
                lock (gate)
                {
                    next = pending.Count > 0 ? pending[0] : null;
                }

                if (next is null)
                {
                    break;
                }

                await sync!.PostFinishesAsync(next, cancellationToken);
                lock (gate)
                {
                    pending.RemoveAt(0);
                }

                await SavePendingAsync();
            }

            var board = await sync!.GetLeaderboardAsync(cancellationToken);
            lock (gate)
            {
                entries.Clear();
                foreach (var (login, entry) in board)
                {
                    entries[login] = entry;
                }

                // what is still on its way counts already, so a player sees their finish at once
                foreach (var finish in pending)
                {
                    AddLocally(finish.Login, finish.Nickname, finish.RawNickname, finish.Count);
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Diagnostics.Trace($"leaderboard hub sync: {ex.Message}");

            // finishes that don't get through are worth a line in the log, at most every 10 min
            int waiting;
            lock (gate)
            {
                waiting = pending.Count;
            }

            if (waiting > 0 && DateTime.UtcNow - lastSyncWarning > TimeSpan.FromMinutes(10))
            {
                lastSyncWarning = DateTime.UtcNow;
                Console.WriteLine($"Warning: {waiting} finish(es) wait for the leaderboard hub - {ex.Message}. Kept in leaderboard-pending.json and sent again.");
            }
        }
        finally
        {
            syncGate.Release();
        }
    }

    public async Task RunHubSyncLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (Remote)
        {
            await Task.Delay(interval, cancellationToken);
            await SyncWithHubAsync(cancellationToken);
        }
    }

    private void AddLocally(string login, string nickname, string rawNickname, int count)
    {
        var finishes = entries.TryGetValue(login, out var existing) ? existing.Finishes + count : count;
        entries[login] = new LeaderboardEntry(nickname, finishes, rawNickname);
    }

    private async Task SavePendingAsync()
    {
        string json;
        lock (gate)
        {
            json = JsonSerializer.Serialize(pending);
        }

        try
        {
            await AtomicFile.WriteAllTextAsync(PendingPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: couldn't save leaderboard-pending.json - {ex.Message}");
        }
    }

    // ---- the hub's side (SyncHub) ----

    public Dictionary<string, LeaderboardEntry> Snapshot()
    {
        RefreshIfChanged();
        lock (gate)
        {
            return new Dictionary<string, LeaderboardEntry>(entries);
        }
    }

    public async Task RecordFinishAsync(string login, string nickname, string rawNickname, CancellationToken cancellationToken = default)
    {
        if (Remote)
        {
            var finish = new SyncFinish(login, nickname, rawNickname, 1, Guid.NewGuid().ToString("N"));
            lock (gate)
            {
                pending.Add(finish);
                AddLocally(login, nickname, rawNickname, 1);
            }

            await SavePendingAsync();
            _ = SyncWithHubAsync(CancellationToken.None);
            return;
        }

        await AddFinishesAsync(login, nickname, rawNickname, 1);
    }

    // a finish another server sent; one it sent before (same id) is not counted again
    public async Task AddRemoteFinishesAsync(SyncFinish finish)
    {
        if (!string.IsNullOrEmpty(finish.Id))
        {
            lock (gate)
            {
                if (!countedIds.Add(finish.Id))
                {
                    return;
                }

                countedOrder.Enqueue(finish.Id);
                if (countedOrder.Count > 5000)
                {
                    countedIds.Remove(countedOrder.Dequeue());
                }
            }
        }

        await AddFinishesAsync(finish.Login, finish.Nickname, finish.RawNickname, Math.Clamp(finish.Count, 1, 100000));
    }

    // count finishes into the local file (this server's own, or as the hub for another server)
    public async Task AddFinishesAsync(string login, string nickname, string rawNickname, int count)
    {
        // no cancellation inside: a finish that was counted must also be saved
        using var fileLock = await AcquireFileLockAsync();

        // count on top of what is on disk right now, not on this process's possibly older copy
        RefreshIfChanged();

        lock (gate)
        {
            AddLocally(login, nickname, rawNickname, count);
        }

        await SaveAsync();
    }

    public IReadOnlyList<LeaderboardEntry> GetTop(int count)
    {
        if (!Remote)
        {
            RefreshIfChanged();
        }

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
        if (!Remote)
        {
            RefreshIfChanged();
        }

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
