using System.Runtime.InteropServices;
using ModularMonolith.Abstractions;
using ModularMonolith.Windows;

namespace ModularMonolith.Tests.Windows;

public sealed class WindowsJobLimitMapperTests
{
    [Theory]
    [InlineData(8, 625)]
    [InlineData(1, 5000)]
    public void Map_DefaultLimits_UsesExpectedCpuRate(int processorCount, uint expectedCpuRate)
    {
        var settings = WindowsJobLimitMapper.Map(new JobLimits(), processorCount);

        Assert.Equal(
            new WindowsJobSettings(
                NativeMethods.JobObjectLimitKillOnJobClose | NativeMethods.JobObjectLimitProcessMemory | NativeMethods.JobObjectLimitJobMemory | NativeMethods.JobObjectLimitActiveProcess,
                128UL * 1024 * 1024,
                128UL * 1024 * 1024,
                64,
                NativeMethods.JobObjectCpuRateControlEnable | NativeMethods.JobObjectCpuRateControlHardCap,
                expectedCpuRate),
            settings);
    }

    [Fact]
    public void Map_Unlimited_KeepsOnlyKillOnClose()
    {
        var settings = WindowsJobLimitMapper.Map(JobLimits.Unlimited, processorCount: 8);

        Assert.Equal(new WindowsJobSettings(NativeMethods.JobObjectLimitKillOnJobClose, 0, 0, 0, 0, 0), settings);
    }

    [Fact]
    public void Map_IgnoresMemoryHigh()
    {
        var settings = WindowsJobLimitMapper.Map(
            new JobLimits(
                MemoryMaxBytes: 256L * 1024 * 1024,
                MemoryHighBytes: 32L * 1024 * 1024,
                CpuCores: null,
                MaxProcesses: null),
            processorCount: 8);

        Assert.Equal(
            new WindowsJobSettings(
                NativeMethods.JobObjectLimitKillOnJobClose | NativeMethods.JobObjectLimitProcessMemory | NativeMethods.JobObjectLimitJobMemory,
                256UL * 1024 * 1024,
                256UL * 1024 * 1024,
                0,
                0,
                0),
            settings);
    }

    [Fact]
    public void Map_ClampsCpuRateToMaximum()
    {
        var settings = WindowsJobLimitMapper.Map(
            new JobLimits(CpuCores: 64, MemoryMaxBytes: null, MemoryHighBytes: null, MaxProcesses: null),
            processorCount: 1);

        Assert.Equal(10_000u, settings.CpuRate);
    }

    [Fact]
    public void Map_ClampsCpuRateToMinimum()
    {
        var settings = WindowsJobLimitMapper.Map(
            new JobLimits(CpuCores: 0.000001, MemoryMaxBytes: null, MemoryHighBytes: null, MaxProcesses: null),
            processorCount: 8);

        Assert.Equal(1u, settings.CpuRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Map_Throws_WhenMemoryMaxBytesIsNotPositive(long memoryMaxBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsJobLimitMapper.Map(
            new JobLimits(MemoryMaxBytes: memoryMaxBytes, MemoryHighBytes: null, CpuCores: null, MaxProcesses: null),
            processorCount: 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Map_Throws_WhenMaxProcessesIsNotPositive(int maxProcesses)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsJobLimitMapper.Map(
            new JobLimits(MemoryMaxBytes: null, MemoryHighBytes: null, CpuCores: null, MaxProcesses: maxProcesses),
            processorCount: 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void Map_Throws_WhenCpuCoresIsNotPositive(double cpuCores)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsJobLimitMapper.Map(
            new JobLimits(MemoryMaxBytes: null, MemoryHighBytes: null, CpuCores: cpuCores, MaxProcesses: null),
            processorCount: 8));
    }

    [Fact]
    public void Map_Throws_WhenCpuCoresIsNaN()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsJobLimitMapper.Map(
            new JobLimits(MemoryMaxBytes: null, MemoryHighBytes: null, CpuCores: double.NaN, MaxProcesses: null),
            processorCount: 8));
    }

    [Fact]
    public void StructLayouts_HaveExpectedSizes()
    {
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        Assert.Equal(8, Marshal.SizeOf<NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
    }
}
