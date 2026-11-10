using System.Security.Cryptography;

namespace PrintArchive.Core;

public static class ServiceKeyStore
{
    /// <summary>
    /// Loads or creates the machine-scoped archive master key used by the Windows service.
    /// Protected with DPAPI LocalMachine so encryption continues while the UI is locked.
    /// </summary>
    public static byte[] LoadOrCreate()
    {
        Directory.CreateDirectory(ArchivePaths.Root);
        if (File.Exists(ArchivePaths.ServiceKeyFile))
        {
            byte[] protectedKey = File.ReadAllBytes(ArchivePaths.ServiceKeyFile);
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.LocalMachine);
        }

        byte[] key = CryptoService.CreateRandomKey();
        Save(key);
        return key;
    }

    public static void Save(byte[] key)
    {
        Directory.CreateDirectory(ArchivePaths.Root);
        byte[] protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
        string temp = ArchivePaths.ServiceKeyFile + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, ArchivePaths.ServiceKeyFile, true);
    }

    public static bool Exists() => File.Exists(ArchivePaths.ServiceKeyFile);
}
