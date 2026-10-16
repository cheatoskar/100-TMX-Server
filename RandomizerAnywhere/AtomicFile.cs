using System.Text;

namespace RandomizerAnywhere;

// Writes a file so that a reader - or the next start after a crash - sees either the old content
// or the new content, never an empty or half-written file.
//
// File.WriteAllText empties the file first and then writes it. A kill, a full disk or a cancelled
// token in between left leaderboard.json empty; the next start read that as "no leaderboard yet"
// and the first finish then saved a one-entry board over all the history. Here the new content goes
// to a temp file next to the target, is flushed to disk, and only then replaces the target in one
// rename. The write itself takes no cancellation token on purpose: once started, finish it.
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // One writer per path at a time, so two saves of the same file cannot race on its temp file.
    private static readonly Dictionary<string, SemaphoreSlim> pathLocks = new(StringComparer.Ordinal);

    public static Task WriteAllTextAsync(string path, string contents, string? backupPath = null) =>
        WriteAllBytesAsync(path, Utf8NoBom.GetBytes(contents), backupPath);

    // backupPath: when given and the target exists, the previous version is kept there.
    public static async Task WriteAllBytesAsync(string path, byte[] contents, string? backupPath = null)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = GetLock(fullPath);
        await gate.WaitAsync();

        try
        {
            var tempPath = fullPath + ".tmp";

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(contents);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (backupPath is not null && File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, Path.GetFullPath(backupPath), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath, overwrite: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // A file that exists but can't be read is moved aside, never overwritten: the caller starts
    // empty, and whoever looks later still has the original bytes.
    public static string? QuarantineCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // the ticks keep two quarantines in the same second from colliding (a failed move
            // here would let the caller overwrite the unreadable file after all)
            var now = DateTimeOffset.UtcNow;
            var quarantined = $"{path}.corrupt-{now:yyyyMMdd-HHmmss}-{now.Ticks % 10_000_000:0000000}";
            File.Move(path, quarantined);
            return quarantined;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: couldn't move the unreadable {Path.GetFileName(path)} aside - {ex.Message}");
            return null;
        }
    }

    private static SemaphoreSlim GetLock(string fullPath)
    {
        lock (pathLocks)
        {
            if (!pathLocks.TryGetValue(fullPath, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                pathLocks[fullPath] = gate;
            }

            return gate;
        }
    }
}
