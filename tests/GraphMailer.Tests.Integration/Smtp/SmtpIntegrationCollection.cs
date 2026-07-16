namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Marks all SMTP integration test classes as belonging to the same
/// collection so that xUnit runs them sequentially instead of in parallel.
///
/// This is required because SmtpTestHost calls Directory.SetCurrentDirectory,
/// which is process-global state. Parallel test execution would cause
/// tests to capture each other's temp directories as the "original" CWD,
/// leading to DirectoryNotFoundException during cleanup.
/// </summary>
[CollectionDefinition("SmtpIntegration", DisableParallelization = true)]
public sealed class SmtpIntegrationCollection;
