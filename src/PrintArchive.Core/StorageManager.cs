namespace PrintArchive.Core;

public sealed class StorageManager
{
    private readonly ArchiveStore _store;
    private readonly long _quotaBytes;

    public StorageManager(ArchiveStore store, long quotaBytes = 5L * 1024 * 1024 * 1024)
    {
        _store = store;
        _quotaBytes = quotaBytes;
    }

    public void EnforceQuota(long incomingBytes = 0)
    {
        ArchivePaths.EnsureCreated();
        while (GetArchiveBytes() + incomingBytes > _quotaBytes)
        {
            var oldest = _store.ReadAll().OrderBy(x => x.Timestamp).FirstOrDefault();
            if (oldest is null) break;

            string jobDir = Path.Combine(ArchivePaths.Jobs, oldest.Id);
            if (Directory.Exists(jobDir))
            {
                try { Directory.Delete(jobDir, true); }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
            }
            _store.DeleteRecord(oldest.Id);
        }
    }

    public long GetArchiveBytes()
    {
        if (!Directory.Exists(ArchivePaths.Jobs)) return 0;
        return Directory.EnumerateFiles(ArchivePaths.Jobs, "*", SearchOption.AllDirectories)
            .Sum(path => { try { return new FileInfo(path).Length; } catch { return 0; } });
    }
}
