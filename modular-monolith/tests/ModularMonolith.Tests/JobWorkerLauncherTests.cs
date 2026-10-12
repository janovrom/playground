using System.Diagnostics;
using ModularMonolith.Abstractions;
using ModularMonolith.Linux;

namespace ModularMonolith.Tests;

public sealed class JobWorkerLauncherTests
{
    [Fact]
    public void Launch_AssignsProcessToJobAndReleasesJobWithTheWorker()
    {
        var factory = new FakeJobFactory();
        var launcher = new JobWorkerLauncher(factory);

        var worker = launcher.Launch(TestProcesses.Sleep("fake-module", 30));
        var job = Assert.Single(factory.Jobs);

        Assert.Equal("fake-module", factory.ModuleName);
        Assert.Equal(worker.ProcessId, Assert.Single(job.AssignedProcessIds));
        Assert.False(job.Disposed);

        worker.Dispose();

        Assert.True(job.Disposed);
        Assert.Equal(1, job.TerminateCount);
    }

    [Fact]
    public void Launch_PassesOptionLimitsToTheJob_AndDefaultsWhenMissing()
    {
        var factory = new FakeJobFactory();
        var launcher = new JobWorkerLauncher(factory);
        var custom = new JobLimits(MemoryMaxBytes: 1024, CpuCores: 2, MaxProcesses: 3);

        using (launcher.Launch(TestProcesses.Sleep("custom", 5, custom)))
        {
            Assert.Equal(custom, factory.Limits);
        }

        using (launcher.Launch(TestProcesses.Sleep("default", 5)))
        {
            Assert.Equal(new JobLimits(), factory.Limits);
        }
    }

    [Fact]
    public void Launch_WithLimitsDisabled_CreatesJobWithoutLimits()
    {
        var factory = new FakeJobFactory();
        var launcher = new JobWorkerLauncher(factory, applyLimits: false);

        using var worker = launcher.Launch(TestProcesses.Sleep("unlimited", 5, new JobLimits(MemoryMaxBytes: 1024)));

        Assert.Equal(JobLimits.Unlimited, factory.Limits);
    }

    [Fact]
    public async Task Launch_WhenAssignFails_KillsTheProcessAndDisposesTheJob()
    {
        var factory = new FakeJobFactory { AssignException = new InvalidOperationException("assign failed") };
        var stop = new RecordingStopStrategy();
        var launcher = new JobWorkerLauncher(factory, stop);

        var exception = Assert.Throws<InvalidOperationException>(() => launcher.Launch(TestProcesses.Sleep("broken", 30)));

        Assert.Equal("assign failed", exception.Message);
        var job = Assert.Single(factory.Jobs);
        Assert.True(job.Disposed);
        Assert.True(stop.Handle!.Disposed);
        await WaitUntilGoneAsync(stop.Handle.AttachedProcessId!.Value);
    }

    private static async Task WaitUntilGoneAsync(int processId)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"Process {processId} was still running.");
    }

    [Fact]
    public void Launch_RejectsInvalidModuleNames()
    {
        var launcher = new JobWorkerLauncher(new FakeJobFactory());

        Assert.Throws<ArgumentException>(() => launcher.Launch(TestProcesses.Sleep("../escape", 1)));
    }

    [Fact]
    public async Task StopAsync_UsesGracefulRequestBeforeKilling()
    {
        var stop = new RecordingStopStrategy { Graceful = true };
        var launcher = new JobWorkerLauncher(new FakeJobFactory(), stop);
        using var worker = launcher.Launch(TestProcesses.Sleep("graceful", 30));

        await worker.StopAsync(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, stop.Handle!.RequestCount);
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task StopAsync_KillsWithoutWaitingWhenNoGracefulRequestIsPossible()
    {
        var factory = new FakeJobFactory();
        using var worker = new JobWorkerLauncher(factory).Launch(TestProcesses.Sleep("hard", 30));

        await worker.StopAsync(TimeSpan.FromSeconds(30));

        Assert.True(worker.HasExited);
        Assert.Equal(1, Assert.Single(factory.Jobs).TerminateCount);
    }

    [Fact]
    public void CgroupJobFactory_WritesUnlimitedAsMax()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var cgroupRoot = Path.Combine(Path.GetTempPath(), $"modular-monolith-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cgroupRoot);
        File.WriteAllText(Path.Combine(cgroupRoot, "cgroup.controllers"), "memory cpu pids\n");

        try
        {
            using var job = new CgroupJobFactory(cgroupRoot).Create("unlimited", JobLimits.Unlimited);
            var cgroupPath = ((CgroupJob)job).Path;

            Assert.Equal("max\n", File.ReadAllText(Path.Combine(cgroupPath, "memory.max")));
            Assert.Equal("max\n", File.ReadAllText(Path.Combine(cgroupPath, "memory.high")));
            Assert.Equal("max 100000\n", File.ReadAllText(Path.Combine(cgroupPath, "cpu.max")));
            Assert.Equal("max\n", File.ReadAllText(Path.Combine(cgroupPath, "pids.max")));
        }
        finally
        {
            Directory.Delete(cgroupRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.5, "50000 100000")]
    [InlineData(2.0, "200000 100000")]
    [InlineData(0.0001, "1000 100000")]
    [InlineData(null, "max 100000")]
    public void CgroupJobFactory_FormatsCpuQuotaFromCores(double? cores, string expected)
        => Assert.Equal(expected, CgroupJobFactory.FormatCpuMax(cores));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void CgroupJobFactory_RejectsInvalidCpuCores(double cores)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CgroupJobFactory.FormatCpuMax(cores));

    private sealed class FakeJobFactory : IJobFactory
    {
        public List<FakeJob> Jobs { get; } = [];
        public string? ModuleName { get; private set; }
        public JobLimits? Limits { get; private set; }
        public Exception? AssignException { get; init; }

        public IJob Create(string moduleName, JobLimits limits)
        {
            ModuleName = moduleName;
            Limits = limits;
            var job = new FakeJob(moduleName, AssignException);
            Jobs.Add(job);
            return job;
        }
    }

    private sealed class FakeJob(string name, Exception? assignException) : IJob
    {
        public string Name { get; } = name;
        public List<int> AssignedProcessIds { get; } = [];
        public int TerminateCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Assign(Process process)
        {
            if (assignException is not null)
            {
                throw assignException;
            }

            AssignedProcessIds.Add(process.Id);
        }

        public void Terminate() => TerminateCount++;

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingStopStrategy : IGracefulStopStrategy
    {
        public bool Graceful { get; init; }
        public RecordingStopHandle? Handle { get; private set; }

        public IGracefulStopHandle Prepare(string moduleName, IList<string> arguments)
            => Handle = new RecordingStopHandle(Graceful);
    }

    private sealed class RecordingStopHandle(bool graceful) : IGracefulStopHandle
    {
        public int? AttachedProcessId { get; private set; }
        public int RequestCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Attach(Process process) => AttachedProcessId = process.Id;

        public bool TryRequestStop()
        {
            RequestCount++;
            return graceful;
        }

        public void Dispose() => Disposed = true;
    }
}
