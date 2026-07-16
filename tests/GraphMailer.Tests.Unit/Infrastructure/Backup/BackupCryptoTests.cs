using System.Text;
using GraphMailer.Service.Infrastructure.Backup;

namespace GraphMailer.Tests.Unit.Infrastructure.Backup;

public sealed class BackupCryptoTests
{
    // Low iteration count keeps the tests fast; production uses BackupCrypto.DefaultIterations.
    private const int TestIterations = 1_000;

    private static byte[] Sample(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void EncryptThenDecrypt_SamePassword_RoundTrips()
    {
        var plain = Sample("""{ "GraphApi": { "ClientSecret": "super-secret" } }""");

        var container = BackupCrypto.Encrypt(plain, "correct horse battery staple", TestIterations);
        var result = BackupCrypto.Decrypt(container, "correct horse battery staple");

        result.Should().Equal(plain);
    }

    [Fact]
    public void Decrypt_WrongPassword_ThrowsDecryptionException()
    {
        var container = BackupCrypto.Encrypt(Sample("payload"), "right-password", TestIterations);

        var act = () => BackupCrypto.Decrypt(container, "wrong-password");

        act.Should().Throw<BackupDecryptionException>();
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsDecryptionException()
    {
        var container = BackupCrypto.Encrypt(Sample("payload"), "pw", TestIterations);
        container[^1] ^= 0xFF;   // flip a ciphertext byte

        var act = () => BackupCrypto.Decrypt(container, "pw");

        act.Should().Throw<BackupDecryptionException>();
    }

    [Fact]
    public void Decrypt_TamperedHeader_ThrowsDecryptionException()
    {
        var container = BackupCrypto.Encrypt(Sample("payload"), "pw", TestIterations);
        container[10] ^= 0xFF;   // flip a salt byte inside the authenticated header

        var act = () => BackupCrypto.Decrypt(container, "pw");

        act.Should().Throw<BackupDecryptionException>();
    }

    [Fact]
    public void Decrypt_BadMagic_ThrowsFormatException()
    {
        var container = BackupCrypto.Encrypt(Sample("payload"), "pw", TestIterations);
        container[0] = (byte)'X';   // corrupt the magic

        var act = () => BackupCrypto.Decrypt(container, "pw");

        act.Should().Throw<BackupFormatException>();
    }

    [Fact]
    public void Decrypt_TruncatedFile_ThrowsFormatException()
    {
        var act = () => BackupCrypto.Decrypt(new byte[] { 1, 2, 3 }, "pw");

        act.Should().Throw<BackupFormatException>();
    }

    [Fact]
    public void Encrypt_EmptyPassword_Throws()
    {
        var act = () => BackupCrypto.Encrypt(Sample("payload"), "", TestIterations);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentContainers()
    {
        var plain = Sample("payload");

        var a = BackupCrypto.Encrypt(plain, "pw", TestIterations);
        var b = BackupCrypto.Encrypt(plain, "pw", TestIterations);

        a.Should().NotEqual(b);   // random salt + nonce
    }
}
