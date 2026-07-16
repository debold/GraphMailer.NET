namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>The file is not a recognizable GraphMailer backup (bad magic/version/truncated).</summary>
internal sealed class BackupFormatException : Exception
{
    public BackupFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The backup could not be decrypted — wrong password or the file was tampered with
/// (AES-GCM authentication failed). Carries no key material.
/// </summary>
internal sealed class BackupDecryptionException : Exception
{
    public BackupDecryptionException(string message, Exception? inner = null) : base(message, inner) { }
}
