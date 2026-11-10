namespace PrintArchive.Core;

public static class ArchivePaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintArchive");

    public static string Jobs => Path.Combine(Root, "Archive", "Jobs");
    public static string Temp => Path.Combine(Root, "Temp");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Pending => Path.Combine(Root, "Pending");
    public static string Database => Path.Combine(Root, "jobs.db");
    public static string LegacyIndex => Path.Combine(Root, "jobs.index.enc");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string VaultFile => Path.Combine(Root, "vault.bin");
    public static string ServiceKeyFile => Path.Combine(Root, "service.key.dpapi");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Pending);
    }

    public static void SignalPendingJob(string jobId)
    {
        try
        {
            EnsureCreated();
            File.WriteAllText(Path.Combine(Pending, jobId + ".signal"), DateTimeOffset.Now.ToString("O"));
        }
        catch { /* Agent polls as fallback */ }
    }
}
