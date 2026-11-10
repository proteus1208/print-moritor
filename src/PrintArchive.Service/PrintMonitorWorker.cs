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
    private readonly List<EventLogWatcher> _watchers = [];
    private ArchiveStore? _store;
    private StorageManager? _storage;
    private CryptoService? _crypto;

    // JobId -> spool/print start time (for duration until Success/Failed/Canceled).
    private readonly ConcurrentDictionary<int, DateTimeOffset> _jobStarts = new();

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

            var key = ServiceKeyStore.LoadOrCreate();
            _crypto = new CryptoService(key);
            _store = new ArchiveStore(_crypto);
            _storage = new StorageManager(_store);

            // Separate watchers so a bad multi-ID XPath cannot disable all recording.
            // 307 = any successful print (physical, PDF, XPS, AnyPrinter, etc.)
            StartWatcher("*[System[(EventID=307)]]", "success-307");
            StartWatcher("*[System[(EventID=310)]]", "canceled-310");
            StartWatcher("*[System[(EventID=824 or EventID=828)]]", "failed-824-828");
            StartWatcher("*[System[(EventID=800 or EventID=801)]]", "start-800-801");

            if (_watchers.Count == 0)
                throw new InvalidOperationException("No print-event watchers could be enabled.");

            WriteLog($"SUCCESS: {_watchers.Count} watcher(s) active; recording all printers.");
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

    private void StartWatcher(string xpath, string label)
    {
        try
        {
            var query = new EventLogQuery(PrintLog, PathType.LogName, xpath);
            var watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += OnEventRecordWritten;
            watcher.Enabled = true;
            _watchers.Add(watcher);
            WriteLog($"Watcher OK: {label} ({xpath})");
        }
        catch (Exception ex)
        {
            WriteLog($"Watcher FAILED: {label} — {ex.Message}");
        }
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

            int? jobId = TryInt(First(values, "Param1", "JobId", "JobID"));

            // Spool / print start — remember for duration only.
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
                durationSeconds = (int)Math.Max(0, (timestamp - started).TotalSeconds);

            string id = record.RecordId?.ToString(CultureInfo.InvariantCulture)
                ?? Guid.NewGuid().ToString("N");

            // Event 307 field order (Microsoft docs / EVTX):
            // Param1=JobId, Param2=Document, Param3=User, Param4=Computer,
            // Param5=Printer, Param6=Port, Param7=Size, Param8=Pages
            string documentName = First(values, "Param2", "DocumentName", "DocName");
            if (string.IsNullOrWhiteSpace(documentName)) documentName = "Unknown document";

            string username = First(values, "Param3", "User", "UserName");
            if (string.IsNullOrWhiteSpace(username))
                username = record.UserId?.Value ?? "Unknown user";

            string printerName = ExtractPrinterName(values);
            string? clientComputer = NullIfEmpty(First(values, "Param4", "Computer", "Machine"));
            long? dataSize = TryLong(First(values, "Param7", "Size", "JobSize"));
            int? pages = TryInt(First(values, "Param8", "Pages", "PageCount"));

            // IMPORTANT: always persist metadata first for EVERY printer (PDF / AnyPrinter / etc.).
            // Heavy original-file capture runs in the Agent so it cannot block this callback.
            var job = new PrintJobRecord
            {
                Id = id,
                Timestamp = timestamp,
                DocumentName = documentName,
                PrinterName = printerName,
                Username = username,
                ClientComputer = clientComputer,
                JobId = jobId,
                PageCount = pages,
                DataSize = dataSize,
                CaptureStatus = "MetadataOnly - pending session capture",
                OriginalFilePath = null,
                ArchiveFilePath = null,
                ScreenshotPath = null,
                EventRecordId = record.RecordId,
                UserSid = record.UserId?.Value,
                PrintOutcome = outcome,
                DurationSeconds = durationSeconds,
                WindowsEventId = eventId
            };

            // Fast path only: if the event already contains a real file path, archive it now.
            if (outcome == PrintOutcomes.Success)
                TryFastPathArchive(job);

            _storage?.EnforceQuota(0);
            _store.Add(job);
            ArchivePaths.SignalPendingJob(job.Id);

            WriteLog($"ARCHIVED: Outcome={outcome}, EventID={eventId}, RecordID={record.RecordId}, " +
                     $"Job={job.JobId}, Printer={job.PrinterName}, Document={job.DocumentName}, " +
                     $"Duration={durationSeconds?.ToString() ?? "-"}s");
        }
        catch (Exception ex)
        {
            WriteLog($"EVENT PROCESSING / STORE ERROR: {ex}");
            _logger.LogError(ex, "Failed to archive print-job metadata.");
        }
    }

    private void TryFastPathArchive(PrintJobRecord job)
    {
        try
        {
            string candidate = job.DocumentName.Trim().Trim('"');
            if (!Path.IsPathRooted(candidate) || !File.Exists(candidate))
                return;

            long fileBytes = new FileInfo(candidate).Length;
            _storage?.EnforceQuota(fileBytes);
            job.OriginalFilePath = candidate;
            job.ArchiveFilePath = _store!.ArchiveOriginalFile(job.Id, candidate);
            job.CaptureStatus = "OriginalCaptured - path in event";
        }
        catch (Exception ex)
        {
            WriteLog($"FAST PATH CAPTURE ERROR: {ex.Message}");
            job.CaptureStatus = "MetadataOnly - pending session capture";
            job.OriginalFilePath = null;
            job.ArchiveFilePath = null;
        }
    }

    private static string ExtractPrinterName(Dictionary<string, string> values)
    {
        // Prefer real printer fields; never fall back to computer name (Param4).
        foreach (string key in new[]
                 {
                     "Param5", "PrinterName", "Printer", "QueueName", "PrintQueue",
                     "Param6" // some builds put printer/port oddly; still better than blank
                 })
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                // Param6 is often a port (nul:, FILE:, USB001) — only use if it looks like a printer.
                if (key == "Param6" && LooksLikePort(value))
                    continue;
                return value.Trim();
            }
        }

        return "Unknown printer";
    }

    private static bool LooksLikePort(string value)
    {
        string v = value.Trim();
        return v.EndsWith(':', StringComparison.Ordinal) ||
               v.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("nul", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("FILE:", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseEvent(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);
        var unnamedData = new List<string>();

        foreach (var element in document.Descendants())
        {
            string local = element.Name.LocalName;
            if (local.StartsWith("Param", StringComparison.OrdinalIgnoreCase))
            {
                result[local] = element.Value;
                continue;
            }

            if (local != "Data") continue;

            string? name = element.Attribute("Name")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                result[name] = element.Value;
                // Also map Name="Param5" style and plain numeric order aliases.
                if (name.StartsWith("Param", StringComparison.OrdinalIgnoreCase))
                    result[name] = element.Value;
            }
            else
            {
                unnamedData.Add(element.Value);
            }
        }

        // Positional <Data> nodes without Name attributes.
        for (int i = 0; i < unnamedData.Count; i++)
        {
            string key = "Param" + (i + 1);
            if (!result.ContainsKey(key) || string.IsNullOrWhiteSpace(result[key]))
                result[key] = unnamedData[i];
        }

        return result;
    }

    private static string First(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int? TryInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static long? TryLong(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EventRecordWritten -= OnEventRecordWritten;
                watcher.Enabled = false;
                watcher.Dispose();
            }
            catch { /* ignore */ }
        }
        _watchers.Clear();
        _store?.Dispose();
        _crypto?.Dispose();
        base.Dispose();
    }
}
