using System.Security.Cryptography;
using System.Text;

namespace PrintArchive.Core;

/// <summary>
/// Password enrollment / verification. The password wraps the archive master key;
/// it does not merely gate the UI.
/// Format: magic(4) | version(1) | iterations(4 LE) | salt(16) | wrappedKey(PRA1 blob).
/// </summary>
public static class PasswordVault
{
    private const string Magic = "PRAV";
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int DefaultIterations = 600_000;

    public static bool IsEnrolled => File.Exists(ArchivePaths.VaultFile);

    public static void Enroll(string password, byte[]? existingMasterKey = null)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters.", nameof(password));

        ArchivePaths.EnsureCreated();
        byte[] masterKey = existingMasterKey ?? (ServiceKeyStore.Exists()
            ? ServiceKeyStore.LoadOrCreate()
            : CryptoService.CreateRandomKey());

        ServiceKeyStore.Save(masterKey);
        WriteVault(password, masterKey);
        CryptographicOperations.ZeroMemory(masterKey);
    }

    public static byte[] Unlock(string password)
    {
        if (!IsEnrolled) throw new InvalidOperationException("Password has not been enrolled.");
        var (iterations, salt, wrapped) = ReadVault();
        byte[] wrapKey = CryptoService.DerivePasswordKey(password, salt, iterations);
        try
        {
            using var crypto = new CryptoService(wrapKey);
            return crypto.Decrypt(wrapped);
        }
        catch (CryptographicException ex)
        {
            throw new UnauthorizedAccessException("Incorrect password.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    public static bool Verify(string password)
    {
        try
        {
            byte[] key = Unlock(password);
            CryptographicOperations.ZeroMemory(key);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void ChangePassword(string currentPassword, string newPassword)
    {
        byte[] masterKey = Unlock(currentPassword);
        try
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
                throw new ArgumentException("Password must be at least 6 characters.", nameof(newPassword));
            WriteVault(newPassword, masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static void WriteVault(string password, byte[] masterKey)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] wrapKey = CryptoService.DerivePasswordKey(password, salt, DefaultIterations);
        try
        {
            using var crypto = new CryptoService(wrapKey);
            byte[] wrapped = crypto.Encrypt(masterKey);

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes(Magic));
            ms.WriteByte(Version);
            ms.Write(BitConverter.GetBytes(DefaultIterations));
            ms.Write(salt);
            ms.Write(wrapped);

            string temp = ArchivePaths.VaultFile + ".tmp";
            File.WriteAllBytes(temp, ms.ToArray());
            File.Move(temp, ArchivePaths.VaultFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private static (int Iterations, byte[] Salt, byte[] Wrapped) ReadVault()
    {
        byte[] data = File.ReadAllBytes(ArchivePaths.VaultFile);
        if (data.Length < 4 + 1 + 4 + SaltSize + 32 ||
            Encoding.ASCII.GetString(data, 0, 4) != Magic ||
            data[4] != Version)
            throw new InvalidDataException("Corrupt password vault.");

        int iterations = BitConverter.ToInt32(data, 5);
        byte[] salt = data.AsSpan(9, SaltSize).ToArray();
        byte[] wrapped = data.AsSpan(9 + SaltSize).ToArray();
        return (iterations, salt, wrapped);
    }
}
