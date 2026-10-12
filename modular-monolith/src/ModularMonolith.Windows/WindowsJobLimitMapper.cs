using ModularMonolith.Abstractions;

namespace ModularMonolith.Windows;

public readonly record struct WindowsJobSettings(
    uint LimitFlags,
    ulong ProcessMemoryLimit,
    ulong JobMemoryLimit,
    uint ActiveProcessLimit,
    uint CpuRateControlFlags,
    uint CpuRate);

public static class WindowsJobLimitMapper
{
    public static WindowsJobSettings Map(JobLimits limits, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (processorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount), processorCount, "Processor count must be greater than zero.");
        }

        var limitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        ulong processMemoryLimit = 0;
        ulong jobMemoryLimit = 0;
        uint activeProcessLimit = 0;
        uint cpuRateControlFlags = 0;
        uint cpuRate = 0;

        if (limits.MemoryMaxBytes is long memoryMaxBytes)
        {
            if (memoryMaxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), limits.MemoryMaxBytes, "MemoryMaxBytes must be greater than zero.");
            }

            processMemoryLimit = checked((ulong)memoryMaxBytes);
            jobMemoryLimit = checked((ulong)memoryMaxBytes);
            limitFlags |= NativeMethods.JobObjectLimitProcessMemory | NativeMethods.JobObjectLimitJobMemory;
        }

        if (limits.MaxProcesses is int maxProcesses)
        {
            if (maxProcesses <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxProcesses, "MaxProcesses must be greater than zero.");
            }

            activeProcessLimit = checked((uint)maxProcesses);
            limitFlags |= NativeMethods.JobObjectLimitActiveProcess;
        }

        if (limits.CpuCores is double cpuCores)
        {
            if (double.IsNaN(cpuCores) || cpuCores <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), limits.CpuCores, "CpuCores must be greater than zero.");
            }

            cpuRateControlFlags = NativeMethods.JobObjectCpuRateControlEnable | NativeMethods.JobObjectCpuRateControlHardCap;
            cpuRate = (uint)Math.Clamp(
                Math.Round(cpuCores / processorCount * 10_000d, MidpointRounding.AwayFromZero),
                1d,
                10_000d);
        }

        return new WindowsJobSettings(limitFlags, processMemoryLimit, jobMemoryLimit, activeProcessLimit, cpuRateControlFlags, cpuRate);
    }
}
