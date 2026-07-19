namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// Thrown when <c>graphmailer.json</c> contains syntactically invalid JSON,
/// is empty, contains a non-object root, or cannot be read from disk.
/// </summary>
internal sealed class ConfigLoadException : Exception
{
    public ConfigLoadException(string message, Exception? inner = null)
        : base(message, inner) { }
}
