using System.Windows;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArchivePaths.EnsureCreated();
        TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TempFileManager.CleanupAllUnlocked();
        base.OnExit(e);
    }
}
