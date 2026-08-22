using GraphMailer.Service.Infrastructure.Encryption;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

/// <summary>
/// Detection of config secrets left in plaintext.
///
/// The runtime accepts a value that is not <c>ENC[...]</c> — that is what makes initial setup
/// possible — but nothing ever encrypts it afterwards, and the integrity check used to look
/// only at values that already were encrypted. A plaintext Graph client secret or SMTP password
/// therefore stayed readable in graphmailer.json, and in every copy or backup of it, with no
/// signal to the operator at all. These tests pin the three keys that carry a secret and the
/// boundaries of what counts as one.
/// </summary>
public sealed class PlaintextSecretDetectionTests
{
    [Fact]
    public void GraphClientSecret_InPlaintext_IsReported()
    {
        var json = """{ "GraphApi": { "TenantId": "abc", "ClientSecret": "s3cr3t-value" } }""";

        SecretIntegrityChecker.FindPlaintextSecrets(json).Should().Equal("GraphApi.ClientSecret");
    }

    [Fact]
    public void UserPasswords_InPlaintext_AreReportedPerUser()
    {
        // The array index matters: the operator has to know which user to fix.
        var json = """
            { "Users": [
                { "Username": "alice", "Password": "hunter2" },
                { "Username": "bob",   "Password": "ENC[whatever]" },
                { "Username": "carol", "Password": "letmein" } ] }
            """;

        SecretIntegrityChecker.FindPlaintextSecrets(json)
            .Should().Equal("Users[0].Password", "Users[2].Password");
    }

    [Fact]
    public void BackupPassword_InPlaintext_IsReported()
        => SecretIntegrityChecker.FindPlaintextSecrets("""{ "Backup": { "Password": "archive-pw" } }""")
            .Should().Equal("Backup.Password");

    [Fact]
    public void EncryptedSecrets_AreNotReported()
    {
        var json = """
            { "GraphApi": { "ClientSecret": "ENC[abc]" },
              "Backup":   { "Password":     "ENC[def]" } }
            """;

        SecretIntegrityChecker.FindPlaintextSecrets(json).Should().BeEmpty();
    }

    [Fact]
    public void EmptySecret_IsNotReported()
    {
        // An unconfigured secret is not a leaked one — reporting it would train operators
        // to ignore the warning.
        var json = """{ "GraphApi": { "ClientSecret": "" }, "Backup": { "Password": "" } }""";

        SecretIntegrityChecker.FindPlaintextSecrets(json).Should().BeEmpty();
    }

    [Fact]
    public void NonSecretKeys_AreNotReported()
    {
        // Only keys that actually carry a secret count; everything else is ordinary config.
        var json = """
            { "Smtp": { "Banner": "GraphMailer", "MaxSizeBytes": 26214400 },
              "Certificate": { "SubjectName": "mail.contoso.com", "Issuer": "CN=Contoso CA" } }
            """;

        SecretIntegrityChecker.FindPlaintextSecrets(json).Should().BeEmpty();
    }

    [Fact]
    public void SecretKeyName_IsMatchedCaseInsensitively()
        => SecretIntegrityChecker.FindPlaintextSecrets("""{ "Backup": { "password": "archive-pw" } }""")
            .Should().Equal("Backup.password");

    [Fact]
    public void MalformedEncMarker_CountsAsPlaintext()
    {
        // "ENC[" without the closing bracket is not an encrypted value — the runtime would
        // hand it to the SMTP client as a literal password, so it must not read as protected.
        SecretIntegrityChecker.FindPlaintextSecrets("""{ "Backup": { "Password": "ENC[truncated" } }""")
            .Should().Equal("Backup.Password");
    }
}
