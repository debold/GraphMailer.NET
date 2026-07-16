using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

namespace GraphMailer.Service.Infrastructure.Encryption;

internal static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a JSON configuration file whose sensitive values may be stored as ENC[...].
    /// Values in that format are automatically decrypted using ASP.NET Core Data Protection.
    /// </summary>
    /// <remarks>
    /// <see cref="PhysicalFileProvider.GetFileInfo"/> rejects absolute (rooted) paths and
    /// returns <c>NotFoundFileInfo</c>, so we must root the provider at the file's directory
    /// and pass only the filename as <c>Path</c>.
    /// </remarks>
    internal static IConfigurationBuilder AddEncryptedJsonFile(
        this IConfigurationBuilder builder,
        string path,
        IDataProtector protector,
        bool optional = true,
        bool reloadOnChange = true)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);

        // PhysicalFileProvider's constructor throws DirectoryNotFoundException when the root
        // directory is missing (and ReloadOnChange always builds a file watcher on it). On a
        // fresh install %ProgramData%\GraphMailer\config does not exist yet, so create it —
        // otherwise the service crashes at startup before it can create any of its folders.
        Directory.CreateDirectory(directory);

        return builder.Add(new EncryptedJsonConfigurationSource
        {
            Path = fileName,
            FileProvider = new PhysicalFileProvider(directory),
            Optional = optional,
            ReloadOnChange = reloadOnChange,
            Protector = protector
        });
    }
}
