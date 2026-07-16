using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Builds and reads the plaintext payload of a backup: a small ZIP containing the manifest
/// and the (decrypted) configuration. This payload is what <see cref="BackupCrypto"/> wraps
/// in the password-encrypted container, so the ZIP itself is never written unencrypted.
/// </summary>
internal static class BackupArchive
{
    private const string ManifestEntry = "manifest.json";
    private const string ConfigEntry = "graphmailer.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Packs the manifest and config JSON into ZIP bytes.</summary>
    internal static byte[] Build(BackupManifest manifest, string configJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions));
            WriteEntry(zip, ConfigEntry, configJson);
        }
        return ms.ToArray();
    }

    /// <summary>Reads the manifest and config JSON from ZIP bytes.</summary>
    /// <exception cref="BackupFormatException">The payload is not a valid backup archive.</exception>
    internal static (BackupManifest Manifest, string ConfigJson) Read(byte[] zipBytes)
    {
        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var manifestJson = ReadEntry(zip, ManifestEntry);
            var configJson = ReadEntry(zip, ConfigEntry);

            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson)
                ?? throw new BackupFormatException("Backup manifest is empty or invalid.");

            return (manifest, configJson);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new BackupFormatException("Backup payload is not a valid GraphMailer archive.", ex);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name)
            ?? throw new BackupFormatException($"Backup archive is missing '{name}'.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
