using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration.Json;
using System.Security.Cryptography;

namespace GraphMailer.Service.Infrastructure.Encryption;

/// <summary>
/// JSON configuration provider that transparently decrypts values in the format ENC[...].
/// Values are encrypted with ASP.NET Core Data Protection; keys are stored in the Windows
/// Registry (HKLM\SOFTWARE\GraphMailer\DataProtection) or config/keys/ as fallback.
/// </summary>
internal sealed class EncryptedJsonConfigurationProvider : JsonConfigurationProvider
{
    private const string EncPrefix = "ENC[";
    private const string EncSuffix = "]";

    private readonly IDataProtector _protector;

    public EncryptedJsonConfigurationProvider(
        EncryptedJsonConfigurationSource source,
        IDataProtector protector)
        : base(source)
    {
        _protector = protector;
    }

    public override void Load()
    {
        base.Load();
        DecryptEncryptedValues();
    }

    private void DecryptEncryptedValues()
    {
        var encryptedKeys = Data
            .Where(kv => kv.Value is not null
                      && kv.Value.StartsWith(EncPrefix)
                      && kv.Value.EndsWith(EncSuffix))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in encryptedKeys)
        {
            var raw = Data[key]!;
            var cipherText = raw[EncPrefix.Length..^EncSuffix.Length];

            try
            {
                Data[key] = _protector.Unprotect(cipherText);
            }
            catch (CryptographicException ex)
            {
                if (Source.Optional)
                {
                    // When the file is optional and decryption fails (e.g. key rotation or
                    // running without access to the production key ring), leave the raw
                    // ENC[...] placeholder in place and log a warning. The service can still
                    // start; the affected value will simply be treated as unconfigured.
                    Console.Error.WriteLine(
                        $"[GraphMailer] WARNING: Cannot decrypt config value for key '{key}'. " +
                        "Running without access to the Data Protection key ring? " +
                        $"({ex.GetType().Name}: {ex.Message})");
                    Data[key] = string.Empty;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[GraphMailer] Cannot decrypt configuration value for key '{key}'. " +
                        "The Data Protection key may have changed or been rotated. " +
                        "Re-enter the value via the dashboard to re-encrypt with the current key.",
                        ex);
                }
            }
        }
    }
}
