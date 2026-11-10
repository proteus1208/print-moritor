using System.Runtime.InteropServices;

namespace PrintArchive.Core;

internal static class ComActiveObject
{
    public static object? TryGet(string progId)
    {
        try
        {
            int hr = CLSIDFromProgID(progId, out Guid clsid);
            if (hr < 0) return null;
            hr = GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
            return hr >= 0 ? obj : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
