using System.Runtime.InteropServices;
using PrintArchive.Core;

namespace PrintArchive.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
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
    private readonly MessageWindow _window = new();

    public HotkeyContext()
    {
        if (!RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkF12))
            MessageBox.Show("Ctrl+Alt+F12 could not be registered. Another application may be using it.",
                "PrintArchive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _window.DestroyHandle();
        }
        base.Dispose(disposing);
    }

    private sealed class MessageWindow : NativeWindow
    {
        public MessageWindow()
        {
            CreateHandle(new CreateParams { Caption = "PrintArchive.HotkeyAgent" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                string ui = Path.Combine(AppContext.BaseDirectory, "PrintArchive.UI.exe");
                if (File.Exists(ui))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ui) { UseShellExecute = true });
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
