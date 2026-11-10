namespace PrintArchive.Core;

public sealed record PrintJobRecord(
    string Id,
    DateTimeOffset Timestamp,
    string DocumentName,
    string PrinterName,
    string UserName,
    string? ClientComputer,
    int? JobId,
    int? Pages,
    long? SpoolBytes,
    string CaptureStatus,
    string? OriginalPath,
    string? ScreenshotPath,
    long? EventRecordId = null,
    string? UserSid = null);

public sealed record ArchiveSettings(long MaxBytes = 5L * 1024 * 1024 * 1024);

public sealed record EncryptedFileInfo(string FileName, string Algorithm, DateTimeOffset CreatedUtc);
