using System.Diagnostics;
using System.Runtime.Versioning;
using ModularMonolith.Abstractions;
using ModularMonolith.Tests;
using ModularMonolith.Windows;

namespace ModularMonolith.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsJobTests
{
    private const string WaitForShutdownEventScript =
        "$name = $args[0].Substring('--Worker:ShutdownEvent='.Length)\r\n" +
        "$event = [System.Threading.EventWaitHandle]::OpenExisting($name)\r\n" +
        "$null = $event.WaitOne()\r\n";

    [WindowsFact]
    public void QueryAppliedSettings_RoundTripsConfiguredLimits()
    {
        var limits = new JobLimits(
            MemoryMaxBytes: 256L * 1024 * 1024,
            MemoryHighBytes: 192L * 1024 * 1024,
            CpuCores: 0.75,
            MaxProcesses: 12);
        var expected = WindowsJobLimitMapper.Map(limits, Environment.ProcessorCount);
        using var job = CreateJob("roundtrip", limits);

        var actual = job.QueryAppliedSettings();

        Assert.Equal(expected, actual);
    }

    [WindowsFact]
    public void Assign_PutsStartedProcessInJob()
    {
        using var process = StartLongRunningProcess();
        using var job = CreateJob("assign");

        job.Assign(process);

        Assert.True(job.Contains(process));
    }

    [WindowsFact]
    public async Task Dispose_KillsAssignedProcess()
    {
        using var process = StartLongRunningProcess();
        var job = CreateJob("dispose");
        job.Assign(process);

        job.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }

    [WindowsFact]
    public async Task Terminate_KillsAssignedProcess()
    {
        using var process = StartLongRunningProcess();
        using var job = CreateJob("terminate");
        job.Assign(process);

        job.Terminate();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }

    [WindowsFact]
    public void Prepare_AppendsShutdownEventArgument_AndSignalsEvent()
    {
        var arguments = new List<string>();
        var strategy = new NamedEventStopStrategy();
        var handle = strategy.Prepare("test-worker", arguments);
        using var process = StartLongRunningProcess();

        try
        {
            var argument = Assert.Single(arguments);
            var prefix = $"--{NamedEventStopStrategy.ConfigurationKey}=";
            Assert.StartsWith(prefix, argument, StringComparison.Ordinal);

            var eventName = argument[prefix.Length..];
            using var openedEvent = EventWaitHandle.OpenExisting(eventName);
            Assert.False(openedEvent.WaitOne(TimeSpan.Zero));

            handle.Attach(process);

            Assert.True(handle.TryRequestStop());
            Assert.True(openedEvent.WaitOne(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            handle.Dispose();
        }
    }

    [WindowsFact]
    public async Task JobWorkerLauncher_StopAsync_UsesNamedEventGracefulShutdown()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wait-for-shutdown-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, WaitForShutdownEventScript);
        try
        {
            var launcher = new JobWorkerLauncher(new WindowsJobFactory(), new NamedEventStopStrategy());
            using var worker = launcher.Launch(new WorkerLaunchOptions(
                "graceful-stop-test",
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                JobLimits.Unlimited));
            await Task.Delay(TimeSpan.FromSeconds(2));

            await worker.StopAsync(TimeSpan.FromSeconds(10));

            Assert.True(worker.HasExited);
            Assert.Equal(0, worker.ExitCode);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static WindowsJob CreateJob(string prefix, JobLimits? limits = null)
        => new($"{prefix}-{Guid.NewGuid():N}", limits ?? JobLimits.Unlimited);

    private static Process StartLongRunningProcess()
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >NUL");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the test process.");
    }
}
