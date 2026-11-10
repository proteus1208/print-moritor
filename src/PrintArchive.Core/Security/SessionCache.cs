using System.Security.Cryptography;

namespace PrintArchive.Core;

/// <summary>
/// Keeps the archive unlocked for the interactive user session via DPAPI CurrentUser.
/// Survives UI process restarts (hotkey) without re-entering the password.
/// </summary>
public static class SessionCache
{
    private static readonly string SessionPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrintArchive", "session.bin");

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    public static void Save(byte[] masterKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        byte[] stamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        byte[] payload = new byte[8 + masterKey.Length];
        Buffer.BlockCopy(stamp, 0, payload, 0, 8);
        Buffer.BlockCopy(masterKey, 0, payload, 8, masterKey.Length);
        byte[] protectedBytes = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(payload);
        string temp = SessionPath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, SessionPath, true);
    }

    public static byte[]? TryLoad()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            byte[] protectedBytes = File.ReadAllBytes(SessionPath);
            byte[] payload = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            if (payload.Length < 8 + 32)
            {
                CryptographicOperations.ZeroMemory(payload);
                return null;
            }

            long unix = BitConverter.ToInt64(payload, 0);
            var created = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (DateTimeOffset.UtcNow - created > MaxAge)
            {
                CryptographicOperations.ZeroMemory(payload);
                Clear();
                return null;
            }

            byte[] key = new byte[payload.Length - 8];
            Buffer.BlockCopy(payload, 8, key, 0, key.Length);
            CryptographicOperations.ZeroMemory(payload);
            return key;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { /* ignore */ }
    }
}
