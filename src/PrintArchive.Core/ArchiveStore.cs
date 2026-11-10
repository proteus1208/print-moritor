using System.Text.Json;

namespace PrintArchive.Core;

public sealed class ArchiveStore
{
    private readonly CryptoService _crypto;
    private readonly string _indexPath = Path.Combine(ArchivePaths.Root, "jobs.index.enc");

    public ArchiveStore(CryptoService crypto) => _crypto = crypto;

    public void Add(PrintJobRecord record)
    {
        ArchivePaths.EnsureCreated();
        var records = ReadAll();
        if (records.Any(x => x.Id == record.Id)) return;
        records.Add(record);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(records);
        string temp = _indexPath + ".tmp";
        File.WriteAllBytes(temp, _crypto.Encrypt(json));
        File.Move(temp, _indexPath, true);
    }

    public List<PrintJobRecord> ReadAll()
    {
        if (!File.Exists(_indexPath)) return [];
        var json = _crypto.Decrypt(File.ReadAllBytes(_indexPath));
        return JsonSerializer.Deserialize<List<PrintJobRecord>>(json)
            ?? throw new InvalidDataException("The encrypted archive index contains invalid data.");
    }

    public void DeleteRecord(string id)
    {
        ArchivePaths.EnsureCreated();
        var records = ReadAll();
        records.RemoveAll(x => x.Id == id);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(records);
        File.WriteAllBytes(_indexPath, _crypto.Encrypt(json));
    }
}
