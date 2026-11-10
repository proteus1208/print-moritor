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

    private const string PrintLog = "Microsoft-Windows-PrintService/Operational";
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PrintArchive", "logs");
    private static readonly string LogFile = Path.Combine(LogDirectory, "service.log");
    private static readonly object FileLock = new();

    public PrintMonitorWorker(ILogger<PrintMonitorWorker> logger) => _logger = logger;

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
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
            var key = ServiceKeyStore.LoadOrCreate();
            _store = new ArchiveStore(new CryptoService(key));

            var query = new EventLogQuery(PrintLog, PathType.LogName,
                "*[System[(EventID=307)]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventRecordWritten;
            _watcher.Enabled = true;
            WriteLog("SUCCESS: EventLogWatcher enabled; encrypted metadata store initialized.");
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
        if (e.EventRecord == null) return;

        using var record = e.EventRecord;
        try
        {
            string xml = record.ToXml();
            var values = ParseEvent(xml);
            var timestamp = record.TimeCreated.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(record.TimeCreated.Value, DateTimeKind.Local))
                : DateTimeOffset.Now;
            string id = record.RecordId?.ToString(CultureInfo.InvariantCulture)
                ?? Guid.NewGuid().ToString("N");

            var job = new PrintJobRecord(
                Id: id,
                Timestamp: timestamp,
                DocumentName: Get(values, "Param2", "Unknown document"),
                PrinterName: Get(values, "Param5", "Unknown printer"),
                UserName: Get(values, "Param3", "Unknown user"),
                ClientComputer: NullIfEmpty(Get(values, "Param4", "")),
                JobId: TryInt(Get(values, "Param1", "")),
                Pages: TryInt(Get(values, "Param8", "")),
                SpoolBytes: TryLong(Get(values, "Param7", "")),
                CaptureStatus: "Metadata only — source file not captured",
                OriginalPath: null,
                ScreenshotPath: null,
                EventRecordId: record.RecordId,
                UserSid: record.UserId?.Value);

            _store?.Add(job);
            WriteLog($"ARCHIVED METADATA: EventID={record.Id}, RecordID={record.RecordId}, " +
                     $"Job={job.JobId}, Document={job.DocumentName}, Printer={job.PrinterName}, " +
                     $"Pages={job.Pages}, Bytes={job.SpoolBytes}");
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
        base.Dispose();
    }
}
