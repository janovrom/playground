using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Linux;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Windows;

namespace ModularMonolith.Tests;

public sealed class WorkerConnectionRecoveryTests
{
    /// <summary>Verifies Unix socket tracking detects a connection held open by a client.</summary>
    [LinuxFact]
    public async Task UnixSocket_TrackingDetectsAnOpenConnection()
        => await AssertTrackingDetectsLeakAsync(new UnixSocketTransport());

    /// <summary>Verifies Named Pipe tracking detects a connection held open by a client.</summary>
    [WindowsFact]
    public async Task NamedPipe_TrackingDetectsAnOpenConnection()
        => await AssertTrackingDetectsLeakAsync(new NamedPipeTransport());

    /// <summary>Verifies in-flight Unix socket requests recover after worker crashes and release connections.</summary>
    [LinuxFact]
    public async Task UnixSocket_InFlightCrashRecoversAndClosesConnections()
        => await AssertRecoveryAsync(new UnixSocketTransport(), new LocalWorkerLauncher(new SigTermStopStrategy()));

    /// <summary>Verifies in-flight Named Pipe requests recover after worker crashes and release connections.</summary>
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task NamedPipe_InFlightCrashRecoversAndClosesConnections()
        => await AssertRecoveryAsync(new NamedPipeTransport(), new JobWorkerLauncher(new WindowsJobFactory(), new NamedEventStopStrategy()));

    private static async Task AssertTrackingDetectsLeakAsync(IWorkerTransport transport)
    {
        var tracked = new TrackedWorkerTransport(transport);
        using var allocator = tracked.CreateAllocator(new ConfigurationBuilder().Build());
        var endpoint = allocator.Allocate("tracking-test");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => tracked.Listen(options, endpoint));
        await using var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        try
        {
            using (var client = tracked.CreateHttpClient(endpoint))
            {
                Assert.Equal("pong", await client.GetStringAsync("/ping"));
                Assert.True(tracked.Opened > 0);
                Assert.True(tracked.Active > 0);
                await Assert.ThrowsAsync<TimeoutException>(() => tracked.WaitForNoConnectionsAsync(TimeSpan.FromMilliseconds(100)));
            }

            await tracked.WaitForNoConnectionsAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, tracked.Active);
        }
        finally
        {
            await app.StopAsync();
            tracked.Cleanup(endpoint);
        }
    }

    private static async Task AssertRecoveryAsync(IWorkerTransport transport, IWorkerLauncher launcher)
    {
        var directory = Directory.CreateTempSubdirectory("worker-connection-test-");
        var settings = new Dictionary<string, string?>
        {
            ["Workers:SocketDirectory"] = directory.FullName,
            ["Workers:StartupTimeoutSeconds"] = "30",
            ["Workers:ShutdownGracePeriod"] = "10",
            ["Workers:SvgRenderer:Arguments:0"] = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.SvgRenderer.dll")
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var tracked = new TrackedWorkerTransport(transport);
        var manager = new WorkerManager(launcher, tracked, [new SvgRendererHostModule()], configuration,
            NullLogger<WorkerManager>.Instance, new RequestDrainTracker());
        var clients = new WorkerClientTestServices(manager, tracked, configuration);

        try
        {
            await manager.StartAsync(CancellationToken.None);
            var renderer = clients.GetRequiredService<IRendererClient>();
            var grid = new GridSettings(2, GridPattern.Checkerboard);
            Assert.Contains("<svg", await renderer.RenderSvgAsync(grid, CancellationToken.None));

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var previous = Assert.Single(manager.GetStatus()).ProcessId;
                Assert.NotNull(previous);
                var writeObserved = tracked.HoldNextWrite();
                var burst = Enumerable.Range(0, 20).Select(_ => RequestDuringCrashAsync(renderer, grid)).ToArray();
                try
                {
                    await writeObserved.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Contains(burst, task => !task.IsCompleted);
                    using var process = Process.GetProcessById(previous.Value);
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    tracked.ReleaseWrite();
                }

                await Task.WhenAll(burst).WaitAsync(TimeSpan.FromSeconds(15));
                var stopwatch = Stopwatch.StartNew();
                WorkerStatus status;
                do
                {
                    status = Assert.Single(manager.GetStatus());
                    if (status.RestartCount >= cycle && status.State == "Running")
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                }
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(30));

                Assert.Equal("Running", status.State);
                Assert.Equal(cycle, status.RestartCount);
                Assert.NotEqual(previous, status.ProcessId);
                var recovered = await Task.WhenAll(Enumerable.Range(0, 20)
                    .Select(_ => renderer.RenderSvgAsync(grid, CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.All(recovered, svg => Assert.Contains("<svg", svg));
            }
        }
        finally
        {
            tracked.ReleaseWrite();
            await manager.StopAsync(CancellationToken.None);
            clients.Dispose();
            directory.Delete(recursive: true);
        }

        Assert.True(tracked.Opened > 0);
        await tracked.WaitForNoConnectionsAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, tracked.Active);
    }

    private static async Task RequestDuringCrashAsync(IRendererClient renderer, GridSettings grid)
    {
        try
        {
            var svg = await renderer.RenderSvgAsync(grid, CancellationToken.None);
            Assert.Contains("<svg", svg);
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }
}
