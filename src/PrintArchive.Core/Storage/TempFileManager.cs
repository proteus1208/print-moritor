namespace PrintArchive.Core;

/// <summary>
/// Tracks temporary decrypted files and cleans leftovers after crash/reboot.
/// </summary>
public static class TempFileManager
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> OpenFiles = new(StringComparer.OrdinalIgnoreCase);

    public static void Track(string path)
    {
        lock (Sync) OpenFiles.Add(path);
    }

    public static void Release(string path)
    {
        lock (Sync) OpenFiles.Remove(path);
        TrySecureDelete(path);
        TryDeleteEmptyParents(path);
    }

    public static void CleanupOrphans(TimeSpan? olderThan = null)
    {
        ArchivePaths.EnsureCreated();
        if (!Directory.Exists(ArchivePaths.Temp)) return;

        TimeSpan age = olderThan ?? TimeSpan.FromHours(12);
        DateTime cutoff = DateTime.UtcNow - age;

        foreach (string path in Directory.EnumerateFiles(ArchivePaths.Temp, "*", SearchOption.AllDirectories))
        {
            lock (Sync)
            {
                if (OpenFiles.Contains(path)) continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc <= cutoff || !IsFileLocked(path))
                    TrySecureDelete(path);
            }
            catch
            {
                // ignore
            }
        }

        foreach (string dir in Directory.EnumerateDirectories(ArchivePaths.Temp, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* ignore */ }
        }
    }

    public static void CleanupAllUnlocked()
    {
        ArchivePaths.EnsureCreated();
        if (!Directory.Exists(ArchivePaths.Temp)) return;

        foreach (string path in Directory.EnumerateFiles(ArchivePaths.Temp, "*", SearchOption.AllDirectories))
        {
            lock (Sync)
            {
                if (OpenFiles.Contains(path)) continue;
            }
            if (!IsFileLocked(path))
                TrySecureDelete(path);
        }
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySecureDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            // Best-effort overwrite before delete. Not a guarantee on SSDs.
            var info = new FileInfo(path);
            if (info.Length > 0 && info.Length < 64 * 1024 * 1024)
            {
                byte[] zeros = new byte[Math.Min(info.Length, 1024 * 1024)];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    long remaining = info.Length;
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(remaining, zeros.Length);
                        fs.Write(zeros, 0, chunk);
                        remaining -= chunk;
                    }
                    fs.Flush(true);
                }
            }
            File.Delete(path);
        }
        catch
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static void TryDeleteEmptyParents(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir) &&
                   dir.StartsWith(ArchivePaths.Temp, StringComparison.OrdinalIgnoreCase) &&
                   dir.Length > ArchivePaths.Temp.Length)
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* ignore */ }
    }
}
