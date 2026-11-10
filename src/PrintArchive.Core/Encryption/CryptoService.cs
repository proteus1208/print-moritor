using System.Security.Cryptography;
using System.Text;

namespace PrintArchive.Core;

public sealed class CryptoService : IDisposable
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;
    private bool _disposed;

    public CryptoService(byte[] key)
    {
        if (key.Length != KeySize) throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        _key = key.ToArray();
    }

    public static byte[] CreateRandomKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] DerivePasswordKey(string password, byte[] salt, int iterations = 600_000)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required.", nameof(password));
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        byte[] result = new byte[4 + NonceSize + TagSize + ciphertext.Length];
        Encoding.ASCII.GetBytes("PRA1").CopyTo(result, 0);
        nonce.CopyTo(result, 4);
        tag.CopyTo(result, 4 + NonceSize);
        ciphertext.CopyTo(result, 4 + NonceSize + TagSize);
        return result;
    }

    public byte[] Decrypt(byte[] blob, byte[]? associatedData = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (blob.Length < 4 + NonceSize + TagSize ||
            Encoding.ASCII.GetString(blob, 0, 4) != "PRA1")
            throw new CryptographicException("Invalid PrintArchive encrypted file.");

        var nonce = blob.AsSpan(4, NonceSize);
        var tag = blob.AsSpan(4 + NonceSize, TagSize);
        var ciphertext = blob.AsSpan(4 + NonceSize + TagSize);
        byte[] plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public void EncryptFile(string sourcePath, string destinationPath)
    {
        byte[] encrypted = Encrypt(File.ReadAllBytes(sourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string temp = destinationPath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, destinationPath, true);
    }

    public void DecryptFile(string sourcePath, string destinationPath)
    {
        byte[] plaintext = Decrypt(File.ReadAllBytes(sourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string temp = destinationPath + ".tmp";
        File.WriteAllBytes(temp, plaintext);
        File.Move(temp, destinationPath, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
