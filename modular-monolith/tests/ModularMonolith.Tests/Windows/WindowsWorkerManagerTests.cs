using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Windows;

namespace ModularMonolith.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsWorkerManagerTests
{
    [WindowsFact]
    public async Task WorkerManager_RunsWorkersInJobsOverNamedPipes_RestartsCrashesAndStopsGracefully()
    {
        var directory = Directory.CreateTempSubdirectory("worker-supervisor-test-");
        var transport = new NamedPipeTransport();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workers:StartupTimeoutSeconds"] = "30",
                ["Workers:ShutdownGracePeriod"] = "10",
                ["Workers:SvgRenderer:Arguments:0"] = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.SvgRenderer.dll"),
                ["Workers:SvgRenderer:Limits:MemoryMaxMb"] = "256",
                ["Workers:ImageCache:Arguments:0"] = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.ImageCache.dll"),
                ["Workers:ImageCache:Limits:MemoryMaxMb"] = "256",
                ["ImageCache:DatabasePath"] = Path.Combine(directory.FullName, "cache.db")
            })
            .Build();
        var manager = new WorkerManager(
            new JobWorkerLauncher(new WindowsJobFactory(), new NamedEventStopStrategy()),
            transport,
            [new SvgRendererHostModule(), new ImageCacheHostModule()],
            configuration,
            NullLogger<WorkerManager>.Instance,
            new RequestDrainTracker());
        var stopped = false;

        try
        {
            await manager.StartAsync(CancellationToken.None);

            using (var client = transport.CreateHttpClient(manager.GetEndpoint(ModuleCatalog.SvgRenderer)))
            {
                using var response = await client.GetAsync("/health");
                response.EnsureSuccessStatusCode();
            }

            var originalProcessId = Assert.Single(manager.GetStatus(), status => status.Name == ModuleCatalog.SvgRenderer).ProcessId;
            Assert.NotNull(originalProcessId);
            using (var crashed = Process.GetProcessById(originalProcessId.Value))
            {
                crashed.Kill();
            }

            var timeout = Stopwatch.StartNew();
            WorkerStatus rendererStatus;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                rendererStatus = Assert.Single(manager.GetStatus(), status => status.Name == ModuleCatalog.SvgRenderer);
            }
            while ((rendererStatus.RestartCount < 1 || rendererStatus.State != "Running") && timeout.Elapsed < TimeSpan.FromSeconds(30));

            Assert.Equal("Running", rendererStatus.State);
            Assert.Equal(1, rendererStatus.RestartCount);
            Assert.NotEqual(originalProcessId, rendererStatus.ProcessId);

            var processIds = manager.GetStatus().Select(status => status.ProcessId!.Value).ToArray();
            var stopWatch = Stopwatch.StartNew();
            await manager.StopAsync(CancellationToken.None);
            stopped = true;

            // A forced kill only happens after the 10 second grace period, so finishing sooner proves the shutdown event drained the workers.
            Assert.True(stopWatch.Elapsed < TimeSpan.FromSeconds(9), $"Stopping took {stopWatch.Elapsed}.");
            Assert.All(manager.GetStatus(), status => Assert.Equal("Stopped", status.State));
            Assert.All(processIds, processId => Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId)));
        }
        finally
        {
            if (!stopped)
            {
                await manager.StopAsync(CancellationToken.None);
            }

            directory.Delete(recursive: true);
        }
    }
}
