using System.Diagnostics;
using System.Runtime.InteropServices;
using PrintArchive.Core;

namespace PrintArchive.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance background agent (no tray icon).
        using var mutex = new Mutex(true, @"Local\PrintArchive.Agent", out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        ArchivePaths.EnsureCreated();
        TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));
        Application.Run(new HotkeyContext());
    }
}

internal sealed class HotkeyContext : ApplicationContext
{
    private const int HotkeyId = 0xA501;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF12 = 0x7B;

    private readonly MessageWindow _window;
    private readonly System.Windows.Forms.Timer _cleanupTimer;
    private readonly SessionCaptureWorker _captureWorker;

    public HotkeyContext()
    {
        _window = new MessageWindow(LaunchOrFocusUi);
        _captureWorker = new SessionCaptureWorker();

        // Silent background: hotkey only, no tray icon / no popups.
        RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkF12);

        _cleanupTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        _cleanupTimer.Tick += (_, _) => TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));
        _cleanupTimer.Start();
    }

    private void LaunchOrFocusUi()
    {
        foreach (var process in Process.GetProcessesByName("PrintArchive.UI"))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
            }
            catch
            {
                // fall through
            }
        }

        string ui = Path.Combine(AppContext.BaseDirectory, "PrintArchive.UI.exe");
        if (!File.Exists(ui))
            return;

        Process.Start(new ProcessStartInfo(ui) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cleanupTimer.Stop();
            _cleanupTimer.Dispose();
            _captureWorker.Dispose();
            UnregisterHotKey(_window.Handle, HotkeyId);
            _window.DestroyHandle();
            TempFileManager.CleanupAllUnlocked();
        }
        base.Dispose(disposing);
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action _onHotkey;

        public MessageWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams { Caption = "PrintArchive.HotkeyAgent" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
                _onHotkey();
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
