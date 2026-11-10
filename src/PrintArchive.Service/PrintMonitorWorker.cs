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

            var query = new EventLogQuery(PrintLog, PathType.LogName,
                "*[System[(EventID=307)]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventRecordWritten;
            _watcher.Enabled = true;

            WriteLog("SUCCESS: EventLogWatcher enabled; SQLite archive initialized.");
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
            if (record.RecordId is long existingId && _store.Database.ExistsByEventRecordId(existingId))
            {
                WriteLog($"SKIP DUPLICATE: RecordID={existingId}");
                return;
            }

            string xml = record.ToXml();
            var values = ParseEvent(xml);
            var timestamp = record.TimeCreated.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(record.TimeCreated.Value, DateTimeKind.Local))
                : DateTimeOffset.Now;
            string id = record.RecordId?.ToString(CultureInfo.InvariantCulture)
                ?? Guid.NewGuid().ToString("N");

            string documentName = Get(values, "Param2", "Unknown document");
            string username = Get(values, "Param3", "Unknown user");
            string printerName = Get(values, "Param5", "Unknown printer");
            long? dataSize = TryLong(Get(values, "Param7", ""));

            var capture = DocumentCapture.TryCapture(documentName, username);
            string? archivePath = null;
            string? originalPath = capture.Path;

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
                    originalPath = capture.Path;
                    archivePath = null;
                }
            }
            else
            {
                _storage?.EnforceQuota(0);
            }

            var job = new PrintJobRecord
            {
                Id = id,
                Timestamp = timestamp,
                DocumentName = documentName,
                PrinterName = printerName,
                Username = username,
                ClientComputer = NullIfEmpty(Get(values, "Param4", "")),
                JobId = TryInt(Get(values, "Param1", "")),
                PageCount = TryInt(Get(values, "Param8", "")),
                DataSize = dataSize,
                CaptureStatus = capture.StatusText,
                OriginalFilePath = originalPath,
                ArchiveFilePath = archivePath,
                ScreenshotPath = null,
                EventRecordId = record.RecordId,
                UserSid = record.UserId?.Value
            };

            _store.Add(job);
            WriteLog($"ARCHIVED: RecordID={record.RecordId}, Job={job.JobId}, " +
                     $"Document={job.DocumentName}, Printer={job.PrinterName}, " +
                     $"Pages={job.PageCount}, Bytes={job.DataSize}, Status={job.CaptureStatus}");
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

        // Named ParamN elements (common on recent Windows).
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName.StartsWith("Param", StringComparison.OrdinalIgnoreCase))
                result[element.Name.LocalName] = element.Value;
        }

        // Fallback: EventData/Data positional mapping used by some builds.
        if (!result.ContainsKey("Param1"))
        {
            var dataNodes = document.Descendants()
                .Where(x => x.Name.LocalName == "Data")
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
