using System.Security.Cryptography;

namespace PrintArchive.Core;

public static class ServiceKeyStore
{
    private static readonly string KeyPath = Path.Combine(ArchivePaths.Root, "service.key.dpapi");

    // Intended for a Windows service running as LocalSystem. DPAPI LocalMachine allows
    // the service to continue encrypting while the interactive UI is locked.
    public static byte[] LoadOrCreate()
    {
        Directory.CreateDirectory(ArchivePaths.Root);
        if (File.Exists(KeyPath))
        {
            byte[] protectedKey = File.ReadAllBytes(KeyPath);
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.LocalMachine);
        }

        byte[] key = CryptoService.CreateRandomKey();
        byte[] protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(KeyPath, protectedBytes);
        return key;
    }
}
