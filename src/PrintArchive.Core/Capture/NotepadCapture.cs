using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintArchive.Core;

/// <summary>
/// Resolves Notepad prints: saved .txt path, or dump unsaved Edit-control text to a temp file.
/// </summary>
public static class NotepadCapture
{
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    public static CaptureAttempt TryCapture(string documentName)
    {
        try
        {
            string cleaned = DocumentCapture.CleanDocumentTitle(documentName);
            if (cleaned.EndsWith(" - Notepad", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^" - Notepad".Length].Trim();

            foreach (var proc in Process.GetProcessesByName("notepad"))
            {
                string title;
                try { title = proc.MainWindowTitle ?? ""; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(title)) continue;

                string titleClean = DocumentCapture.CleanDocumentTitle(title);
                if (titleClean.EndsWith(" - Notepad", StringComparison.OrdinalIgnoreCase))
                    titleClean = titleClean[..^" - Notepad".Length].Trim();

                bool matches =
                    string.Equals(Path.GetFileName(titleClean), Path.GetFileName(cleaned), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(titleClean, cleaned, StringComparison.OrdinalIgnoreCase) ||
                    IsUntitled(cleaned) ||
                    IsUntitled(titleClean);

                if (!matches && !IsUntitled(cleaned)) continue;

                // Saved file shown in title (sometimes full path on newer Notepad).
                if (Path.IsPathRooted(titleClean) && File.Exists(titleClean))
                    return CaptureAttempt.Original(titleClean, "Notepad window path");

                string fileName = Path.GetFileName(titleClean);
                if (!string.IsNullOrWhiteSpace(fileName) && !IsUntitled(fileName))
                {
                    var found = DocumentCapture.TryCapture(fileName, null);
                    if (found.Result == CaptureResult.OriginalCaptured && found.Path is not null)
                        return found;
                }

                // Unsaved / unresolved: pull text from classic Notepad Edit control.
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                IntPtr edit = FindWindowEx(hwnd, IntPtr.Zero, "Edit", null);
                if (edit == IntPtr.Zero)
                    edit = FindWindowEx(hwnd, IntPtr.Zero, "RichEditD2DPT", null); // Win11 Notepad sometimes

                string? text = edit != IntPtr.Zero ? GetWindowText(edit) : null;
                if (string.IsNullOrEmpty(text))
                    continue;

                string tempDir = Path.Combine(ArchivePaths.Temp, "notepad-capture");
                Directory.CreateDirectory(tempDir);
                string safeName = IsUntitled(fileName) || string.IsNullOrWhiteSpace(fileName)
                    ? $"untitled-{DateTime.Now:HHmmss}.txt"
                    : Sanitize(fileName);
                if (!safeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    safeName += ".txt";
                string tempPath = Path.Combine(tempDir, safeName);
                File.WriteAllText(tempPath, text, Encoding.UTF8);
                return CaptureAttempt.Original(tempPath, "Notepad text dump");
            }
        }
        catch
        {
            // ignore
        }

        return CaptureAttempt.MetadataOnly("Notepad source not found");
    }

    private static bool IsUntitled(string name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("*Untitled", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        int len = (int)SendMessage(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        SendMessage(hwnd, WmGetText, (IntPtr)sb.Capacity, sb);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);
}
