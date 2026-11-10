namespace PrintArchive.Core;

public sealed class StorageManager
{
    private readonly ArchiveStore _store;
    private long _quotaBytes;

    public StorageManager(ArchiveStore store, long? quotaBytes = null)
    {
        _store = store;
        _quotaBytes = quotaBytes ?? SettingsStore.Load().MaxBytes;
    }

    public long QuotaBytes
    {
        get => _quotaBytes;
        set => _quotaBytes = value;
    }

    public void EnforceQuota(long incomingBytes = 0)
    {
        ArchivePaths.EnsureCreated();
        while (GetArchiveBytes() + incomingBytes > _quotaBytes)
        {
            var oldest = _store.Database.GetOldest();
            if (oldest is null) break;
            _store.DeleteRecord(oldest.Id);
        }
    }

    public long GetArchiveBytes()
    {
        long total = 0;
        if (Directory.Exists(ArchivePaths.Jobs))
        {
            total += Directory.EnumerateFiles(ArchivePaths.Jobs, "*", SearchOption.AllDirectories)
                .Sum(SafeLength);
        }
        if (File.Exists(ArchivePaths.Database))
            total += SafeLength(ArchivePaths.Database);
        return total;
    }

    public string FormatUsage()
    {
        long used = GetArchiveBytes();
        return $"{FormatBytes(used)} / {FormatBytes(_quotaBytes)}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
