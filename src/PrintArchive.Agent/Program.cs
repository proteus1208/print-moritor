using System.Diagnostics;
using System.Runtime.InteropServices;
using PrintArchive.Core;

namespace PrintArchive.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _cleanupTimer;
    private readonly SessionCaptureWorker _captureWorker;

    public HotkeyContext()
    {
        _window = new MessageWindow(LaunchOrFocusUi);
        _captureWorker = new SessionCaptureWorker();

        _tray = new NotifyIcon
        {
            Text = "PrintArchive Agent",
            Visible = true,
            Icon = SystemIcons.Application,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => LaunchOrFocusUi();

        if (!RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkF12))
            MessageBox.Show("Ctrl+Alt+F12 could not be registered. Another application may be using it.",
                "PrintArchive", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        _cleanupTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        _cleanupTimer.Tick += (_, _) => TempFileManager.CleanupOrphans(TimeSpan.FromHours(1));
        _cleanupTimer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PrintArchive", null, (_, _) => LaunchOrFocusUi());
        menu.Items.Add("Cleanup temp files", null, (_, _) =>
        {
            TempFileManager.CleanupAllUnlocked();
            MessageBox.Show("Temporary decrypted files cleaned.", "PrintArchive",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void LaunchOrFocusUi()
    {
        // If History/Login is already open, bring it forward instead of starting another process.
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
                // fall through to launch
            }
        }

        string ui = Path.Combine(AppContext.BaseDirectory, "PrintArchive.UI.exe");
        if (!File.Exists(ui))
        {
            MessageBox.Show(
                "PrintArchive.UI.exe was not found next to the Agent.\nPublish Agent and UI into the same folder.",
                "PrintArchive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
            _tray.Visible = false;
            _tray.Dispose();
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
