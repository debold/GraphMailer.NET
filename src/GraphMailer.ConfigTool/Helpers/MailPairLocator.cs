using System.IO;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>Whichever halves of a stored message were found on disk.</summary>
/// <param name="EmlPath">The message file, or <see langword="null"/> when only the sidecar exists.</param>
/// <param name="MetaPath">The sidecar, or <see langword="null"/> when only the message exists.</param>
internal readonly record struct MailPair(string? EmlPath, string? MetaPath)
{
    internal bool HasMessage => EmlPath is not null;
    internal bool HasMetadata => MetaPath is not null;
}

/// <summary>
/// Finds the other half of a stored message.
///
/// The service writes every message as a pair — <c>{id}.eml</c> next to <c>{id}.meta.json</c>
/// (see <c>MailQueueWriter</c>). The <c>.eml</c> holds the message; the sidecar holds what the
/// message itself cannot say: the SMTP <b>envelope</b> and the client that delivered it.
///
/// Half a pair is a normal thing to find. The two files are written separately, the message is
/// renamed into place first, and a folder may hold an orphan from an interrupted write, a manual
/// copy, or a message exported on its own. Both halves are therefore optional, and the caller
/// decides what it can still do with what it got.
///
/// Pure string logic, and easy to get subtly wrong — <c>Path.ChangeExtension</c> on
/// <c>x.meta.json</c> yields <c>x.meta</c>, not <c>x</c> — so it lives here with tests rather
/// than inline in a dialog.
/// </summary>
internal static class MailPairLocator
{
    private const string EmlSuffix = ".eml";
    private const string MetaSuffix = ".meta.json";

    /// <summary>
    /// The sidecar belonging to a message file, or <see langword="null"/> when the path is not a
    /// <c>.eml</c>. Existence is not checked.
    /// </summary>
    internal static string? MetaPathFor(string? emlPath)
    {
        if (string.IsNullOrWhiteSpace(emlPath)) return null;
        if (!emlPath.EndsWith(EmlSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        return emlPath[..^EmlSuffix.Length] + MetaSuffix;
    }

    /// <summary>The message file belonging to a sidecar, or <see langword="null"/>.</summary>
    internal static string? EmlPathFor(string? metaPath)
    {
        if (string.IsNullOrWhiteSpace(metaPath)) return null;
        if (!metaPath.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        return metaPath[..^MetaSuffix.Length] + EmlSuffix;
    }

    /// <summary>
    /// Resolves whichever half the operator picked into both paths, so the file dialog can accept
    /// either — and so a lone file still yields what it does hold.
    ///
    /// A path that is neither is taken as a message: the dialog's "all files" filter lets any file
    /// be chosen, and whether it parses as mail is the parser's answer to give, not this method's.
    /// </summary>
    internal static MailPair Resolve(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return default;

        if (EmlPathFor(selectedPath) is { } emlFromMeta)
        {
            // A sidecar was picked. Its message may be missing — that is still usable: the
            // envelope is exactly what the sidecar carries.
            return new MailPair(File.Exists(emlFromMeta) ? emlFromMeta : null, selectedPath);
        }

        var meta = MetaPathFor(selectedPath);
        return new MailPair(selectedPath, meta is not null && File.Exists(meta) ? meta : null);
    }
}
