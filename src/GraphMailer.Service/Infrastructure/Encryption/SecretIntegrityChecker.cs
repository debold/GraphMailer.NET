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
    /// Outcome of scanning a config document: how many <c>ENC[...]</c> values it contains and
    /// which of them (by JSON path) cannot be decrypted with the supplied protector.
    /// </summary>
    internal readonly record struct SecretScanResult(int TotalEncrypted, IReadOnlyList<string> Undecryptable);

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
            Walk(root, path: string.Empty, acc);
        return new SecretScanResult(acc.Total, acc.Failures);
    }

    private sealed class Accumulator(IDataProtector protector)
    {
        public IDataProtector Protector { get; } = protector;
        public int Total { get; set; }
        public List<string> Failures { get; } = [];
    }

    private static void Walk(JsonNode node, string path, Accumulator acc)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    if (value is not null)
                        Walk(value, path.Length == 0 ? key : $"{path}.{key}", acc);
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } item)
                        Walk(item, $"{path}[{i}]", acc);
                break;

            case JsonValue val when val.TryGetValue<string>(out var s)
                                 && s.StartsWith(EncPrefix, StringComparison.Ordinal)
                                 && s.EndsWith(EncSuffix, StringComparison.Ordinal):
                acc.Total++;
                var cipher = s[EncPrefix.Length..^EncSuffix.Length];
                try
                {
                    acc.Protector.Unprotect(cipher);
                }
                catch (CryptographicException)
                {
                    acc.Failures.Add(path);
                }
                break;
        }
    }
}
