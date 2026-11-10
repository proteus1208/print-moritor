using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PrintArchive.Core;

/// <summary>
/// Captures the interactive desktop. Must run in the user session (Agent), not Session 0.
/// </summary>
public static class ScreenshotCapture
{
    public static string CaptureToFile(string destinationPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPngPath)!);

        int x = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int y = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        int w = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        int h = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (w <= 0 || h <= 0)
        {
            x = 0;
            y = 0;
            w = GetSystemMetrics(0); // SM_CXSCREEN
            h = GetSystemMetrics(1); // SM_CYSCREEN
        }

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        string temp = destinationPngPath + ".tmp";
        bitmap.Save(temp, ImageFormat.Png);
        File.Move(temp, destinationPngPath, true);
        return destinationPngPath;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
