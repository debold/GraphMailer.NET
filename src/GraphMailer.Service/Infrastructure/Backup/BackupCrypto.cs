using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Password-based authenticated encryption for backup containers, built entirely on .NET
/// primitives (no third-party crypto):
///   key  = PBKDF2-HMAC-SHA256(password, random salt, iterations) → 32 bytes
///   data = AES-256-GCM(key, random nonce), the plaintext header authenticated as AAD
///
/// File layout:
///   magic "GMBK" (4) · version (1) · iterations (4, BE) · salt (16) · nonce (12) · tag (16) · ciphertext (…)
/// The first 37 bytes (everything up to and including the nonce) form the header and are
/// passed as associated data, so any header tampering also fails authentication.
/// </summary>
internal static class BackupCrypto
{
    private static readonly byte[] Magic = "GMBK"u8.ToArray();
    private const byte Version = 1;

    // OWASP 2023 guidance for PBKDF2-HMAC-SHA256.
    internal const int DefaultIterations = 600_000;

    private const int SaltLen = 16;
    private const int NonceLen = 12;   // AES-GCM standard nonce
    private const int TagLen = 16;     // AES-GCM full tag
    private const int KeyLen = 32;     // AES-256
    private const int HeaderLen = 4 + 1 + 4 + SaltLen + NonceLen; // 37

    internal static byte[] Encrypt(byte[] plaintext, string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Backup password must not be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);

        var header = new byte[HeaderLen];
        BuildHeader(header, iterations, salt, nonce);

        var key = DeriveKey(password, salt, iterations);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLen];
            using var gcm = new AesGcm(key, TagLen);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, header);

            var output = new byte[HeaderLen + TagLen + ciphertext.Length];
            header.CopyTo(output, 0);
            tag.CopyTo(output, HeaderLen);
            ciphertext.CopyTo(output, HeaderLen + TagLen);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[] Decrypt(byte[] container, string password)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Length < HeaderLen + TagLen)
            throw new BackupFormatException("Backup file is truncated or not a GraphMailer backup.");

        var header = container.AsSpan(0, HeaderLen);
        if (!header[..4].SequenceEqual(Magic))
            throw new BackupFormatException("Unrecognized file format (bad magic) — not a GraphMailer backup.");
        if (header[4] != Version)
            throw new BackupFormatException($"Unsupported backup version {header[4]} (expected {Version}).");

        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.Slice(5, 4));
        var salt = header.Slice(9, SaltLen).ToArray();
        var nonce = header.Slice(9 + SaltLen, NonceLen).ToArray();

        var tag = container.AsSpan(HeaderLen, TagLen).ToArray();
        var ciphertext = container.AsSpan(HeaderLen + TagLen).ToArray();

        var key = DeriveKey(password, salt, iterations);
        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, header.ToArray());
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new BackupDecryptionException(
                "Could not decrypt the backup — wrong password or the file is corrupt.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void BuildHeader(byte[] header, int iterations, byte[] salt, byte[] nonce)
    {
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5, 4), iterations);
        salt.CopyTo(header, 9);
        nonce.CopyTo(header, 9 + SaltLen);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyLen);
}
