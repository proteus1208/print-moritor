namespace PrintArchive.Core;

public sealed class PrintJobRecord
{
    public string Id { get; set; } = "";
    public int? JobId { get; set; }
    public long? EventRecordId { get; set; }
    public string DocumentName { get; set; } = "";
    public string Username { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public int? PageCount { get; set; }
    public long? DataSize { get; set; }
    public string? OriginalFilePath { get; set; }
    public string? ArchiveFilePath { get; set; }
    public string? ScreenshotPath { get; set; }
    public string CaptureStatus { get; set; } = "MetadataOnly";
    public string? ClientComputer { get; set; }
    public string? UserSid { get; set; }

    // Compatibility aliases used by older UI bindings / migration.
    public string UserName => Username;
    public int? Pages => PageCount;
    public long? SpoolBytes => DataSize;
    public string? OriginalPath => OriginalFilePath;
}

public sealed class ArchiveSettings
{
    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public string Hotkey { get; set; } = "Ctrl+Alt+F12";
}

public enum CaptureResult
{
    MetadataOnly,
    OriginalCaptured,
    RenderedCaptured,
    PathResolvedNotCopied,
    Failed
}
