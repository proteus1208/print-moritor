using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintArchive.Core;

namespace PrintArchive.Service;

public sealed class PrintMonitorWorker : BackgroundService
{
    private readonly ILogger<PrintMonitorWorker> _logger;
    private EventLogWatcher? _watcher;
    private ArchiveStore? _store;
    private StorageManager? _storage;
    private CryptoService? _crypto;

    // JobId -> spool/print start time (for duration until Success/Failed/Canceled).
    private readonly ConcurrentDictionary<int, DateTimeOffset> _jobStarts = new();
    private int _outcomeWaitSeconds = 120;

    private const string PrintLog = "Microsoft-Windows-PrintService/Operational";
    private static readonly string LogFile = Path.Combine(ArchivePaths.Logs, "service.log");
    private static readonly object FileLock = new();

    public PrintMonitorWorker(ILogger<PrintMonitorWorker> logger) => _logger = logger;

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(ArchivePaths.Logs);
            lock (FileLock)
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* Diagnostic logging must not terminate the service. */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteLog("PrintArchive worker starting.");
        try
        {
            ArchivePaths.EnsureCreated();
            TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));

            _outcomeWaitSeconds = Math.Clamp(SettingsStore.Load().OutcomeWaitSeconds, 15, 600);

            var key = ServiceKeyStore.LoadOrCreate();
            _crypto = new CryptoService(key);
            _store = new ArchiveStore(_crypto);
            _storage = new StorageManager(_store);

            // 800/801 = start, 307 = success, 310 = canceled/deleted, 828/824 = failed
            var query = new EventLogQuery(PrintLog, PathType.LogName,
                "*[System[(EventID=307 or EventID=310 or EventID=828 or EventID=824 or EventID=800 or EventID=801)]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventRecordWritten;
            _watcher.Enabled = true;

            WriteLog($"SUCCESS: watching print events (wait={_outcomeWaitSeconds}s); SQLite ready.");
            _logger.LogInformation("PrintArchive is monitoring Windows print events.");
        }
        catch (Exception ex)
        {
            WriteLog($"STARTUP ERROR: {ex}");
            _logger.LogError(ex, "Could not initialize PrintArchive monitoring or storage.");
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { WriteLog("PrintArchive worker stopping."); }
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException != null) { WriteLog($"EVENT ERROR: {e.EventException}"); return; }
        if (e.EventRecord == null || _store is null) return;

        using var record = e.EventRecord;
        try
        {
            int eventId = record.Id;
            string xml = record.ToXml();
            var values = ParseEvent(xml);
            var timestamp = record.TimeCreated.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(record.TimeCreated.Value, DateTimeKind.Local))
                : DateTimeOffset.Now;

            int? jobId = TryInt(Get(values, "Param1", "")) ?? TryInt(Get(values, "JobId", ""));

            // Spool / print start — remember for duration; do not archive yet.
            if (eventId is 800 or 801)
            {
                if (jobId is int jid)
                    _jobStarts[jid] = timestamp;
                WriteLog($"START: EventID={eventId}, Job={jobId}");
                return;
            }

            if (record.RecordId is long existingId && _store.Database.ExistsByEventRecordId(existingId))
            {
                WriteLog($"SKIP DUPLICATE: RecordID={existingId}");
                return;
            }

            string outcome = eventId switch
            {
                307 => PrintOutcomes.Success,
                310 => PrintOutcomes.Canceled,
                824 or 828 => PrintOutcomes.Failed,
                _ => PrintOutcomes.Unknown
            };

            int? durationSeconds = null;
            if (jobId is int idKey && _jobStarts.TryRemove(idKey, out var started))
            {
                durationSeconds = (int)Math.Max(0, (timestamp - started).TotalSeconds);
                // If outcome took longer than configured wait, still keep measured duration.
                _ = _outcomeWaitSeconds;
            }

            string id = record.RecordId?.ToString(CultureInfo.InvariantCulture)
                ?? Guid.NewGuid().ToString("N");

            string documentName = Get(values, "Param2", "Unknown document");
            string username = Get(values, "Param3", "Unknown user");
            string printerName = Get(values, "Param5", Get(values, "Param4", "Unknown printer"));
            long? dataSize = TryLong(Get(values, "Param7", ""));

            // Only capture originals for successful prints.
            CaptureAttempt capture = CaptureAttempt.MetadataOnly(outcome);
            string? archivePath = null;
            string? originalPath = null;

            if (outcome == PrintOutcomes.Success)
            {
                capture = DocumentCapture.TryCapture(documentName, username);
                originalPath = capture.Path;
                if (capture.Result == CaptureResult.OriginalCaptured && originalPath is not null)
                {
                    try
                    {
                        long fileBytes = new FileInfo(originalPath).Length;
                        _storage?.EnforceQuota(fileBytes);
                        archivePath = _store.ArchiveOriginalFile(id, originalPath);
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"CAPTURE ENCRYPT ERROR: {ex.Message}");
                        capture = CaptureAttempt.MetadataOnly($"Found {originalPath} but encrypt failed: {ex.Message}");
                        originalPath = null;
                        archivePath = null;
                    }
                }
                else
                {
                    _storage?.EnforceQuota(0);
                }
            }

            var job = new PrintJobRecord
            {
                Id = id,
                Timestamp = timestamp,
                DocumentName = documentName,
                PrinterName = printerName,
                Username = username,
                ClientComputer = NullIfEmpty(Get(values, "Param4", "")),
                JobId = jobId,
                PageCount = TryInt(Get(values, "Param8", "")),
                DataSize = dataSize,
                CaptureStatus = capture.StatusText,
                OriginalFilePath = originalPath,
                ArchiveFilePath = archivePath,
                ScreenshotPath = null,
                EventRecordId = record.RecordId,
                UserSid = record.UserId?.Value,
                PrintOutcome = outcome,
                DurationSeconds = durationSeconds,
                WindowsEventId = eventId
            };

            _store.Add(job);
            ArchivePaths.SignalPendingJob(job.Id);
            WriteLog($"ARCHIVED: Outcome={outcome}, EventID={eventId}, RecordID={record.RecordId}, " +
                     $"Job={job.JobId}, Duration={durationSeconds?.ToString() ?? "-"}s, " +
                     $"Document={job.DocumentName}, Printer={job.PrinterName}");
        }
        catch (Exception ex)
        {
            WriteLog($"EVENT PROCESSING / STORE ERROR: {ex}");
            _logger.LogError(ex, "Failed to archive print-job metadata.");
        }
    }

    private static Dictionary<string, string> ParseEvent(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName.StartsWith("Param", StringComparison.OrdinalIgnoreCase))
                result[element.Name.LocalName] = element.Value;

            // Diagnostic events sometimes use named Data Name="JobId"
            if (element.Name.LocalName == "Data")
            {
                string? name = element.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    result[name] = element.Value;
            }
        }

        if (!result.ContainsKey("Param1"))
        {
            var dataNodes = document.Descendants()
                .Where(x => x.Name.LocalName == "Data" && x.Attribute("Name") is null)
                .ToList();
            for (int i = 0; i < dataNodes.Count; i++)
                result["Param" + (i + 1)] = dataNodes[i].Value;
        }

        return result;
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int? TryInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static long? TryLong(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public override void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EventRecordWritten -= OnEventRecordWritten;
            _watcher.Enabled = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _store?.Dispose();
        _crypto?.Dispose();
        base.Dispose();
    }
}
