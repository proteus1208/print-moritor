using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrintArchive.Core;

/// <summary>
/// Primary archive facade: SQLite metadata + encrypted document files (hashed names).
/// </summary>
public sealed class ArchiveStore : IDisposable
{
    private readonly JobDatabase _db;
    private readonly CryptoService _crypto;

    public ArchiveStore(CryptoService crypto, string? databasePath = null)
    {
        _crypto = crypto;
        _db = new JobDatabase(databasePath);
        TryMigrateLegacyIndex();
    }

    public CryptoService Crypto => _crypto;
    public JobDatabase Database => _db;

    public void Add(PrintJobRecord record)
    {
        if (record.EventRecordId is long eid && _db.ExistsByEventRecordId(eid))
            return;
        if (_db.ExistsById(record.Id))
            return;
        _db.Upsert(record);
    }

    public List<PrintJobRecord> ReadAll() => _db.Query();

    public List<PrintJobRecord> Query(
        string? search = null,
        string? printer = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null) =>
        _db.Query(search, printer, from, to);

    public PrintJobRecord? Get(string id) => _db.GetById(id);

    public void DeleteRecord(string id)
    {
        var record = _db.GetById(id);
        if (record is null) return;

        TryDeleteFile(record.ArchiveFilePath);
        TryDeleteFile(record.ScreenshotPath);
        string jobDir = Path.Combine(ArchivePaths.Jobs, id);
        if (Directory.Exists(jobDir))
        {
            try { Directory.Delete(jobDir, true); } catch { /* best effort */ }
        }
        _db.Delete(id);
    }

    public string? ArchiveOriginalFile(string jobId, string sourcePath)
    {
        if (!File.Exists(sourcePath)) return null;
        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        string dest = Path.Combine(ArchivePaths.Jobs, jobId, HashedFileName(jobId, "orig", ext));
        _crypto.EncryptFile(sourcePath, dest);
        return dest;
    }

    public string ArchiveScreenshotFile(string jobId, string pngPath)
    {
        string dest = Path.Combine(ArchivePaths.Jobs, jobId, HashedFileName(jobId, "shot", ".png"));
        _crypto.EncryptFile(pngPath, dest);
        return dest;
    }

    public void Update(PrintJobRecord record) => _db.Upsert(record);

    public string DecryptToTemp(PrintJobRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.ArchiveFilePath) || !File.Exists(job.ArchiveFilePath))
            throw new FileNotFoundException("No archived document is available for this job.");

        string session = Path.Combine(ArchivePaths.Temp, Environment.ProcessId.ToString(), job.Id);
        Directory.CreateDirectory(session);

        // Friendly name for opening in the associated app (not the hashed archive name).
        string name = !string.IsNullOrWhiteSpace(job.OriginalFilePath)
            ? Path.GetFileName(job.OriginalFilePath)
            : SanitizeFileName(DocumentCapture.CleanDocumentTitle(job.DocumentName));
        if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            name += GuessExtension(job);

        string dest = Path.Combine(session, name);
        _crypto.DecryptFile(job.ArchiveFilePath, dest);
        File.SetAttributes(dest, FileAttributes.Temporary);
        return dest;
    }

    public byte[]? DecryptBytes(string? encryptedPath)
    {
        if (string.IsNullOrWhiteSpace(encryptedPath) || !File.Exists(encryptedPath))
            return null;
        return _crypto.Decrypt(File.ReadAllBytes(encryptedPath));
    }

    public void Export(PrintJobRecord job, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(job.ArchiveFilePath) || !File.Exists(job.ArchiveFilePath))
            throw new FileNotFoundException("No archived document is available for this job.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        _crypto.DecryptFile(job.ArchiveFilePath, destinationPath);
    }

    private static string HashedFileName(string jobId, string kind, string ext)
    {
        string payload = $"{jobId}|{kind}|{Guid.NewGuid():N}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        string hex = Convert.ToHexString(hash).ToLowerInvariant()[..40];
        if (!ext.StartsWith('.')) ext = "." + ext;
        return hex + ext + ".enc";
    }

    private static string GuessExtension(PrintJobRecord job)
    {
        string doc = job.DocumentName.ToLowerInvariant();
        if (doc.Contains(".txt") || doc.Contains("notepad")) return ".txt";
        if (doc.Contains(".pdf")) return ".pdf";
        if (doc.Contains(".docx")) return ".docx";
        if (doc.Contains(".psd")) return ".psd";
        return ".bin";
    }

    private void TryMigrateLegacyIndex()
    {
        if (!File.Exists(ArchivePaths.LegacyIndex)) return;
        if (_db.Count() > 0) return;

        try
        {
            byte[] json = _crypto.Decrypt(File.ReadAllBytes(ArchivePaths.LegacyIndex));
            using var doc = JsonDocument.Parse(json);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var record = new PrintJobRecord
                {
                    Id = GetString(element, "Id") ?? Guid.NewGuid().ToString("N"),
                    JobId = GetInt(element, "JobId"),
                    EventRecordId = GetLong(element, "EventRecordId"),
                    DocumentName = GetString(element, "DocumentName") ?? "Unknown document",
                    Username = GetString(element, "UserName") ?? GetString(element, "Username") ?? "Unknown user",
                    PrinterName = GetString(element, "PrinterName") ?? "Unknown printer",
                    Timestamp = GetDate(element, "Timestamp") ?? DateTimeOffset.Now,
                    PageCount = GetInt(element, "Pages") ?? GetInt(element, "PageCount"),
                    DataSize = GetLong(element, "SpoolBytes") ?? GetLong(element, "DataSize"),
                    OriginalFilePath = GetString(element, "OriginalPath") ?? GetString(element, "OriginalFilePath"),
                    ArchiveFilePath = GetString(element, "ArchiveFilePath"),
                    ScreenshotPath = GetString(element, "ScreenshotPath"),
                    CaptureStatus = GetString(element, "CaptureStatus") ?? "MetadataOnly",
                    ClientComputer = GetString(element, "ClientComputer"),
                    UserSid = GetString(element, "UserSid"),
                };
                _db.Upsert(record);
            }

            string bak = ArchivePaths.LegacyIndex + ".migrated";
            File.Move(ArchivePaths.LegacyIndex, bak, true);
        }
        catch
        {
            // Leave legacy file in place if migration fails.
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    private static DateTimeOffset? GetDate(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(p.GetString(), out var d) ? d : null;

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "document.bin" : name;
    }

    public void Dispose() => _db.Dispose();
}
