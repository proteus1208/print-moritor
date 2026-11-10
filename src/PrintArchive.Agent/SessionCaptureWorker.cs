using PrintArchive.Core;

namespace PrintArchive.Agent;

/// <summary>
/// Interactive-session enrichment: screenshot + original (Word/PSD/Notepad/path search).
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
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
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
            bool gotOriginal = false;

            if (string.IsNullOrWhiteSpace(job.ScreenshotPath) || !File.Exists(job.ScreenshotPath))
            {
                try
                {
                    string jobDir = Path.Combine(ArchivePaths.Jobs, job.Id);
                    Directory.CreateDirectory(jobDir);
                    string png = Path.Combine(ArchivePaths.Temp, $"shot-{job.Id}.png");
                    ScreenshotCapture.CaptureToFile(png);
                    storage.EnforceQuota(new FileInfo(png).Length);
                    job.ScreenshotPath = store.ArchiveScreenshotFile(job.Id, png);
                    try { File.Delete(png); } catch { /* ignore */ }
                    changed = true;
                }
                catch
                {
                    // Desktop locked / no interactive display.
                }
            }

            bool allowOriginal = string.IsNullOrWhiteSpace(job.PrintOutcome) ||
                                 job.PrintOutcome == PrintOutcomes.Success;
            if (string.IsNullOrWhiteSpace(job.ArchiveFilePath) && allowOriginal)
            {
                try
                {
                    var capture = ResolveOriginal(job.DocumentName, job.Username);
                    if (capture.Result == CaptureResult.OriginalCaptured && capture.Path is not null &&
                        File.Exists(capture.Path))
                    {
                        storage.EnforceQuota(new FileInfo(capture.Path).Length);
                        bool isTempDump = capture.Path.StartsWith(ArchivePaths.Temp, StringComparison.OrdinalIgnoreCase);
                        job.OriginalFilePath = isTempDump ? Path.GetFileName(capture.Path) : capture.Path;
                        job.ArchiveFilePath = store.ArchiveOriginalFile(job.Id, capture.Path);
                        job.CaptureStatus = capture.StatusText;
                        gotOriginal = true;
                        changed = true;
                        if (isTempDump)
                        {
                            try { File.Delete(capture.Path); } catch { /* ignore */ }
                        }
                    }
                }
                catch
                {
                    // keep metadata-only
                }
            }

            if (changed)
            {
                bool hasOriginal = !string.IsNullOrWhiteSpace(job.ArchiveFilePath);
                bool hasShot = !string.IsNullOrWhiteSpace(job.ScreenshotPath);
                job.CaptureStatus = (hasOriginal, hasShot) switch
                {
                    (true, true) => gotOriginal || hasOriginal
                        ? "OriginalCaptured + Screenshot"
                        : "OriginalCaptured + Screenshot",
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

    private static CaptureAttempt ResolveOriginal(string documentName, string? username)
    {
        // 1) Notepad first (saved path or live text dump).
        string lower = documentName.ToLowerInvariant();
        if (lower.Contains("notepad") || lower.EndsWith(".txt") ||
            lower.StartsWith("untitled") || !documentName.Contains('.'))
        {
            var notepad = NotepadCapture.TryCapture(documentName);
            if (notepad.Result == CaptureResult.OriginalCaptured)
                return notepad;
        }

        // 2) General resolver (Word/PSD/Recent/path search).
        var general = DocumentCapture.TryCapture(documentName, username);
        if (general.Result == CaptureResult.OriginalCaptured)
            return general;

        // 3) Last chance: Notepad anyway.
        return NotepadCapture.TryCapture(documentName);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _watcher?.Dispose();
    }
}
