namespace GraphMailer.Service.Configuration;

/// <summary>
/// Weekly check against the GitHub Releases API for a newer GraphMailer version.
/// Fully opt-in: while disabled the service never contacts github.com. The optional
/// admin e-mail on a new release is gated separately via
/// <see cref="AdminNotificationTypesOptions.UpdateAvailable"/>.
/// </summary>
public sealed class UpdateCheckOptions
{
    public const string SectionName = "UpdateCheck";

    public bool Enabled { get; init; } = false;
}
