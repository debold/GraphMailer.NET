using GraphMailer.Service;
using GraphMailer.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GraphMailer.Tests.Integration;

public class ServiceLifecycleTests
{
    private static IHost BuildTestHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILogger<Worker>>(NullLogger<Worker>.Instance);
                services.AddSingleton(Substitute.For<IAdminNotificationService>());
                services.AddHostedService<Worker>();
            })
            .Build();

    [Fact]
    public async Task Host_StartsAndStops_WithoutException()
    {
        using var host = BuildTestHost();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Host_StopAsync_CompletesWithinTimeout()
    {
        using var host = BuildTestHost();

        await host.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Record.ExceptionAsync(() => host.StopAsync(cts.Token));

        exception.Should().BeNull();
    }

    [Fact]
    public async Task Host_MultipleStartStop_DoesNotThrow()
    {
        for (int i = 0; i < 3; i++)
        {
            using var host = BuildTestHost();
            await host.StartAsync();
            await host.StopAsync();
        }
    }
}
