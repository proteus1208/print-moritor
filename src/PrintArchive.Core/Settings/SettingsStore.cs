using System.Text.Json;

namespace PrintArchive.Core;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ArchiveSettings Load()
    {
        ArchivePaths.EnsureCreated();
        if (!File.Exists(ArchivePaths.SettingsFile))
            return new ArchiveSettings();

        try
        {
            return JsonSerializer.Deserialize<ArchiveSettings>(File.ReadAllText(ArchivePaths.SettingsFile))
                   ?? new ArchiveSettings();
        }
        catch
        {
            return new ArchiveSettings();
        }
    }

    public static void Save(ArchiveSettings settings)
    {
        ArchivePaths.EnsureCreated();
        string temp = ArchivePaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, ArchivePaths.SettingsFile, true);
    }
}
