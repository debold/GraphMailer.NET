using System.Text.RegularExpressions;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Maps the JSON paths in
/// <see cref="GraphMailer.Service.Infrastructure.Config.ConfigDocument.DecryptionFailures"/>
/// (e.g. <c>GraphApi.ClientSecret</c>, <c>Users[0].Password</c>) to the UI elements that
/// display those values, so undecryptable secrets can be flagged inline in the ConfigTool.
/// </summary>
internal static class DecryptionFailureMap
{
    private static readonly Regex UserPassword =
        new(@"^Users\[(\d+)\]\.Password$", RegexOptions.Compiled);

    /// <summary>The failure path of the Graph API client secret.</summary>
    internal const string GraphClientSecret = "GraphApi.ClientSecret";

    /// <summary>
    /// User index for a <c>Users[i].Password</c> path; <see langword="null"/> for any other path.
    /// </summary>
    internal static int? UserPasswordIndex(string path)
    {
        var m = UserPassword.Match(path);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>True when the Graph client secret could not be decrypted.</summary>
    internal static bool HasGraphApiFailure(IEnumerable<string> failures)
        => failures.Any(p => p == GraphClientSecret);

    /// <summary>True when at least one user password could not be decrypted.</summary>
    internal static bool HasUserFailure(IEnumerable<string> failures)
        => failures.Any(p => UserPasswordIndex(p) is not null);
}
