namespace PrintArchive.Core;

public static class PrintOutcomes
{
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
    public const string Unknown = "Unknown";
}

public sealed class PrintJobRecord
{
    public string Id { get; set; } = "";
    public int? JobId { get; set; }
    public long? EventRecordId { get; set; }
    public int? WindowsEventId { get; set; }
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
    public string PrintOutcome { get; set; } = PrintOutcomes.Success;
    public int? DurationSeconds { get; set; }
    public string? ClientComputer { get; set; }
    public string? UserSid { get; set; }

    // UI-only fields filled when loading a day view.
    public int RepeatCount { get; set; } = 1;
    public int RepeatIndex { get; set; } = 1;

    public string UserName => Username;
    public int? Pages => PageCount;
    public long? SpoolBytes => DataSize;
    public string? OriginalPath => OriginalFilePath;

    public string RepeatLabel => RepeatCount <= 1 ? "1x" : $"{RepeatIndex}/{RepeatCount}";
    public string DurationLabel => DurationSeconds is int s
        ? (s < 60 ? $"{s}s" : $"{s / 60}m {s % 60}s")
        : "-";
}

public sealed class ArchiveSettings
{
    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public string Hotkey { get; set; } = "Ctrl+Alt+F12";
    /// <summary>How long to wait for a terminal print event after spooling starts.</summary>
    public int OutcomeWaitSeconds { get; set; } = 120;
}

public enum CaptureResult
{
    MetadataOnly,
    OriginalCaptured,
    RenderedCaptured,
    PathResolvedNotCopied,
    Failed
}
