using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Service.Infrastructure.Encryption;

/// <summary>
/// Scans a raw configuration JSON document for <c>ENC[...]</c> values and verifies that
/// each one can be decrypted with a given protector. Operates on the raw file (not the
/// already-loaded <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>, which
/// has by then blanked undecryptable values), so it sees exactly which secrets are broken.
/// </summary>
internal static class SecretIntegrityChecker
{
    private const string EncPrefix = "ENC[";
    private const string EncSuffix = "]";

    /// <summary>
    /// Config keys that carry a secret and must therefore always be stored as <c>ENC[...]</c>.
    /// Matched on the property name rather than a fixed list of paths, so a new section that
    /// adds a <c>Password</c> is covered the day it appears instead of the day someone
    /// remembers to extend this list. Currently reaches GraphApi.ClientSecret,
    /// Users[n].Password and Backup.Password.
    /// </summary>
    private static readonly string[] SecretKeyNames = ["Password", "ClientSecret"];

    /// <summary>
    /// Outcome of scanning a config document: how many <c>ENC[...]</c> values it contains,
    /// which of them (by JSON path) cannot be decrypted with the supplied protector, and which
    /// secret-bearing keys hold a plaintext value instead of an encrypted one.
    /// </summary>
    internal readonly record struct SecretScanResult(
        int TotalEncrypted,
        IReadOnlyList<string> Undecryptable,
        IReadOnlyList<string> Plaintext);

    /// <summary>
    /// Returns the JSON paths (e.g. <c>GraphApi.ClientSecret</c>, <c>Users[0].Password</c>)
    /// of every <c>ENC[...]</c> value that fails to decrypt with <paramref name="protector"/>.
    /// Empty when all encrypted values decrypt or the document contains none.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The document is not valid JSON.</exception>
    internal static IReadOnlyList<string> FindUndecryptableSecrets(string json, IDataProtector protector)
        => Scan(json, protector).Undecryptable;

    /// <summary>
    /// Scans the document and reports both the total number of <c>ENC[...]</c> values and the
    /// paths of those that cannot be decrypted with <paramref name="protector"/>.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The document is not valid JSON.</exception>
    internal static SecretScanResult Scan(string json, IDataProtector protector)
    {
        var acc = new Accumulator(protector);
        var root = JsonNode.Parse(json);
        if (root is not null)
            Walk(root, path: string.Empty, key: string.Empty, acc);
        return new SecretScanResult(acc.Total, acc.Failures, acc.Plaintext);
    }

    /// <summary>
    /// Returns the JSON paths of secret-bearing keys whose value is stored in plaintext.
    /// Needs no protector — it only asks whether a value is encrypted at all, not whether it
    /// decrypts.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The document is not valid JSON.</exception>
    internal static IReadOnlyList<string> FindPlaintextSecrets(string json)
    {
        var acc = new Accumulator(protector: null);
        var root = JsonNode.Parse(json);
        if (root is not null)
            Walk(root, path: string.Empty, key: string.Empty, acc);
        return acc.Plaintext;
    }

    private sealed class Accumulator(IDataProtector? protector)
    {
        /// <summary>Null when only the plaintext scan is wanted; no decryption is attempted then.</summary>
        public IDataProtector? Protector { get; } = protector;
        public int Total { get; set; }
        public List<string> Failures { get; } = [];
        public List<string> Plaintext { get; } = [];
    }

    private static void Walk(JsonNode node, string path, string key, Accumulator acc)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (childKey, value) in obj)
                    if (value is not null)
                        Walk(value, path.Length == 0 ? childKey : $"{path}.{childKey}", childKey, acc);
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } item)
                        Walk(item, $"{path}[{i}]", key, acc);
                break;

            case JsonValue val when val.TryGetValue<string>(out var s):
                if (s.StartsWith(EncPrefix, StringComparison.Ordinal) &&
                    s.EndsWith(EncSuffix, StringComparison.Ordinal))
                {
                    acc.Total++;
                    if (acc.Protector is { } protector)
                    {
                        var cipher = s[EncPrefix.Length..^EncSuffix.Length];
                        try
                        {
                            protector.Unprotect(cipher);
                        }
                        catch (CryptographicException)
                        {
                            acc.Failures.Add(path);
                        }
                    }
                }
                else if (s.Length > 0 && IsSecretKey(key))
                {
                    // A secret-bearing key holding something that is not ENC[...]. Accepted by
                    // the runtime (plaintext is allowed while a value is first being set up),
                    // but nothing else would ever report it — so it would stay readable in the
                    // file, and in any copy or backup of it, indefinitely.
                    acc.Plaintext.Add(path);
                }
                break;
        }
    }

    private static bool IsSecretKey(string key)
        => SecretKeyNames.Contains(key, StringComparer.OrdinalIgnoreCase);
}
