using ModularMonolith.Abstractions;
using ModularMonolith.Linux;

namespace ModularMonolith.Tests;

public class LinuxProcessGuardTests
{
    [Fact]
    public void EnsureKillOnParentExit_ShouldNotThrow_WhenNotRunningOnLinux()
    {
        var exception = Record.Exception(() => new LinuxProcessGuard().EnsureKillOnParentExit());
        Assert.Null(exception);
    }

    [Fact]
    public void OptionNames_ShouldMatchLinuxProcessGuardContract()
    {
        Assert.Equal(1, LinuxProcessGuard.PrSetDeathSig);
        Assert.Equal(9, LinuxProcessGuard.SigKill);
    }

    [Fact]
    public void GetCurrentProcessRoot_ShouldResolveNestedSystemdSlice()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"modular-monolith-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var processCgroupPath = Path.Combine(temporaryDirectory, "cgroup");
        File.WriteAllText(processCgroupPath, "0::/modular.slice/modular-monolith.slice/modular-monolith.service\n");

        try
        {
            var root = CgroupPathResolver.GetCurrentProcessRoot("/sys/fs/cgroup", processCgroupPath);

            Assert.Equal("/sys/fs/cgroup/modular.slice/modular-monolith.slice/modular-monolith.service", root);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Launch_ShouldConfigureDedicatedCgroupAndAssignWorkerProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var cgroupRoot = Path.Combine(Path.GetTempPath(), $"modular-monolith-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cgroupRoot);
        File.WriteAllText(Path.Combine(cgroupRoot, "cgroup.controllers"), "memory cpu pids\n");
        File.WriteAllText(Path.Combine(cgroupRoot, "cgroup.subtree_control"), string.Empty);

        try
        {
            var launcher = new JobWorkerLauncher(new CgroupJobFactory(cgroupRoot), new SigTermStopStrategy());
            using var worker = launcher.Launch(TestProcesses.Sleep("test-worker", 5));
            var cgroupPath = Assert.Single(Directory.GetDirectories(cgroupRoot, "module-test-worker-*"));

            Assert.Equal("+memory +cpu +pids\n", File.ReadAllText(Path.Combine(cgroupRoot, "cgroup.subtree_control")));
            Assert.Equal("134217728\n", File.ReadAllText(Path.Combine(cgroupPath, "memory.max")));
            Assert.Equal("100663296\n", File.ReadAllText(Path.Combine(cgroupPath, "memory.high")));
            Assert.Equal("50000 100000\n", File.ReadAllText(Path.Combine(cgroupPath, "cpu.max")));
            Assert.Equal("64\n", File.ReadAllText(Path.Combine(cgroupPath, "pids.max")));
            Assert.Equal($"{worker.ProcessId}\n", File.ReadAllText(Path.Combine(cgroupPath, "cgroup.procs")));
        }
        finally
        {
            Directory.Delete(cgroupRoot, recursive: true);
        }
    }

    [Fact]
    public void Launch_ShouldStartWorkerWithoutCgroupForLocalDevelopment()
    {
        using var worker = new LocalWorkerLauncher().Launch(TestProcesses.Sleep("test-worker", 5));

        Assert.True(worker.ProcessId > 0);
    }

    [Fact]
    public async Task WorkerProcess_ShouldRaiseExitedWithExitCode()
    {
        using var worker = new LocalWorkerLauncher().Launch(TestProcesses.Exit("test-worker", 7));
        var exited = new TaskCompletionSource<WorkerExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Exited += args => exited.TrySetResult(args);

        var eventArgs = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(7, eventArgs.ExitCode);
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task WorkerProcess_Stop_ShouldTerminateRunningWorker()
    {
        using var worker = new LocalWorkerLauncher().Launch(TestProcesses.Sleep("test-worker", 30));

        worker.Stop();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(worker.HasExited);
    }
}
