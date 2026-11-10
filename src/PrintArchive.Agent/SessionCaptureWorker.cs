using PrintArchive.Core;

namespace PrintArchive.Agent;

/// <summary>
/// Runs in the interactive user session: captures screenshots and retries Word/PSD
/// original-file resolution (COM works here; usually fails inside the Session 0 service).
/// </summary>
internal sealed class SessionCaptureWorker : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _sync = new();
    private bool _busy;

    public SessionCaptureWorker()
    {
        ArchivePaths.EnsureCreated();
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        try
        {
            _watcher = new FileSystemWatcher(ArchivePaths.Pending, "*.signal")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            _watcher.Created += (_, _) => Tick();
        }
        catch
        {
            _watcher = null;
        }
    }

    private void Tick()
    {
        lock (_sync)
        {
            if (_busy) return;
            _busy = true;
        }

        try { EnrichPendingJobs(); }
        catch { /* never crash Agent */ }
        finally { lock (_sync) _busy = false; }
    }

    private static void EnrichPendingJobs()
    {
        byte[] key = ServiceKeyStore.LoadOrCreate();
        using var crypto = new CryptoService(key);
        using var store = new ArchiveStore(crypto);
        var storage = new StorageManager(store);

        foreach (var job in store.Database.QueryNeedingEnrichment()
                     .Where(j =>
                         string.IsNullOrWhiteSpace(j.ScreenshotPath) ||
                         (string.IsNullOrWhiteSpace(j.ArchiveFilePath) &&
                          DateTimeOffset.Now - j.Timestamp < TimeSpan.FromMinutes(45))))
        {
            bool changed = false;
            bool gotScreenshot = false;
            bool gotOriginal = false;

            if (string.IsNullOrWhiteSpace(job.ScreenshotPath) || !File.Exists(job.ScreenshotPath))
            {
                try
                {
                    string jobDir = Path.Combine(ArchivePaths.Jobs, job.Id);
                    Directory.CreateDirectory(jobDir);
                    string png = Path.Combine(jobDir, "screenshot-raw.png");
                    ScreenshotCapture.CaptureToFile(png);
                    storage.EnforceQuota(new FileInfo(png).Length);
                    job.ScreenshotPath = store.ArchiveScreenshotFile(job.Id, png);
                    try { File.Delete(png); } catch { /* ignore */ }
                    gotScreenshot = true;
                    changed = true;
                }
                catch
                {
                    // Desktop locked / no interactive display.
                }
            }

            if (string.IsNullOrWhiteSpace(job.ArchiveFilePath))
            {
                try
                {
                    var capture = DocumentCapture.TryCapture(job.DocumentName, job.Username);
                    if (capture.Result == CaptureResult.OriginalCaptured && capture.Path is not null)
                    {
                        storage.EnforceQuota(new FileInfo(capture.Path).Length);
                        job.OriginalFilePath = capture.Path;
                        job.ArchiveFilePath = store.ArchiveOriginalFile(job.Id, capture.Path);
                        job.CaptureStatus = capture.StatusText;
                        gotOriginal = true;
                        changed = true;
                    }
                }
                catch
                {
                    // keep metadata-only — never fail the whole enrichment pass
                }
            }

            if (changed)
            {
                bool hasOriginal = !string.IsNullOrWhiteSpace(job.ArchiveFilePath);
                bool hasShot = !string.IsNullOrWhiteSpace(job.ScreenshotPath);
                job.CaptureStatus = (hasOriginal, hasShot) switch
                {
                    (true, true) => gotOriginal ? "OriginalCaptured + Screenshot" : "OriginalCaptured + Screenshot",
                    (true, false) => "OriginalCaptured",
                    (false, true) => "MetadataOnly + Screenshot",
                    _ => job.CaptureStatus
                };
                store.Update(job);
            }

            try
            {
                string signal = Path.Combine(ArchivePaths.Pending, job.Id + ".signal");
                if (File.Exists(signal) && !string.IsNullOrWhiteSpace(job.ScreenshotPath))
                    File.Delete(signal);
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _watcher?.Dispose();
    }
}
