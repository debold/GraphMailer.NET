using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Live;

/// <summary>
/// Live tests for the tenant sender directory (User.Read.All): full user sync
/// and the on-demand proxyAddresses lookup against the real test tenant.
/// </summary>
public class SenderDirectoryLiveTests
{
    private static GraphDirectoryGateway BuildGateway()
        => new(
            new GraphClientProvider(NullLogger<GraphClientProvider>.Instance),
            new StaticOptionsMonitor<GraphApiOptions>(
                LiveTestSettings.Current.ToGraphApiOptions()));

    [LiveFact]
    public async Task GetAllUsersAsync_ReturnsTheSenderMailbox()
    {
        var users = await BuildGateway().GetAllUsersAsync(CancellationToken.None);

        users.Should().NotBeEmpty(
            "the full sync must return tenant users (requires User.Read.All)");
        users.SelectMany(u => u.SmtpAddresses)
            .Should().Contain(a => a.Equals(LiveTestSettings.Current.SenderAddress,
                StringComparison.OrdinalIgnoreCase),
                "the configured sender mailbox must be part of the directory");
    }

    [LiveFact]
    public async Task FindBySmtpAddressAsync_KnownAddress_ReturnsUser()
    {
        var user = await BuildGateway().FindBySmtpAddressAsync(
            LiveTestSettings.Current.SenderAddress!, CancellationToken.None);

        user.Should().NotBeNull();
        user!.Id.Should().NotBeNullOrEmpty();
        user.SmtpAddresses.Should().Contain(a => a.Equals(LiveTestSettings.Current.SenderAddress,
            StringComparison.OrdinalIgnoreCase));
    }

    [LiveFact]
    public async Task FindBySmtpAddressAsync_UnknownAddress_ReturnsNull()
    {
        var s = LiveTestSettings.Current;
        var domain = s.SenderAddress![(s.SenderAddress!.IndexOf('@') + 1)..];

        var user = await BuildGateway().FindBySmtpAddressAsync(
            $"graphmailer-live-nonexistent@{domain}", CancellationToken.None);

        user.Should().BeNull("an address that exists nowhere in the tenant must not resolve");
    }
}
