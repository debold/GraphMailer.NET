using GraphMailer.Service;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

public class WorkerTests
{
    private static Worker Create() =>
        new(NullLogger<Worker>.Instance, Substitute.For<IAdminNotificationService>());

    [Fact]
    public async Task Worker_StopsGracefully_WhenCancelled()
    {
        var worker = Create();

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        // No exception = graceful shutdown
    }

    [Fact]
    public async Task Worker_DoesNotThrow_WhenStoppedImmediately()
    {
        var worker = Create();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        });

        exception.Should().BeNull();
    }

    [Fact]
    public async Task Worker_StopAsync_CompletesWithinTimeout()
    {
        var worker = Create();
        await worker.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(cts.Token);

        // Completes well within the 5-second timeout
    }
}
