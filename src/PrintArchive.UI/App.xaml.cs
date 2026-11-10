using System.Windows;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        ArchivePaths.EnsureCreated();
        TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));

        try
        {
            byte[]? cached = SessionCache.TryLoad();
            if (cached is not null)
            {
                OpenHistory(cached);
                return;
            }
        }
        catch
        {
            SessionCache.Clear();
        }

        var login = new LoginWindow();
        MainWindow = login;
        login.Show();
    }

    internal static void OpenHistory(byte[] masterKey)
    {
        var history = new HistoryWindow(masterKey);
        Current.MainWindow = history;
        history.Show();
        history.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TempFileManager.CleanupAllUnlocked();
        base.OnExit(e);
    }
}
