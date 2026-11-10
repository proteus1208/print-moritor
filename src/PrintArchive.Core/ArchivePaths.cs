namespace PrintArchive.Core;

public static class ArchivePaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintArchive");

    public static string Jobs => Path.Combine(Root, "Archive", "Jobs");
    public static string Temp => Path.Combine(Root, "Temp");
    public static string Logs => Path.Combine(Root, "Logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Logs);
    }
}
