using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsJob : IJob
{
    private readonly SafeJobHandle _handle;
    private readonly bool _cpuRateControlApplied;
    private int _disposed;

    public WindowsJob(string name, JobLimits limits)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows job objects require Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(limits);

        Name = name;
        var settings = WindowsJobLimitMapper.Map(limits, Environment.ProcessorCount);
        _cpuRateControlApplied = settings.CpuRateControlFlags != 0;

        // Unnamed on purpose: a named job object could be opened (or squatted) by other processes.
        _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, $"Failed to create job object '{name}'.");
        }

        try
        {
            ApplySettings(settings);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public string Name { get; }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();

        NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle, $"Failed to assign process {process.Id} to job '{Name}'.");
    }

    public void Terminate()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            NativeMethods.TerminateJobObject(_handle, 1, $"Failed to terminate job '{Name}'.");
        }
        catch (Win32Exception) when (IsDisposed || IsEmpty())
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handle.Dispose();
    }

    internal bool Contains(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();

        return NativeMethods.IsProcessInJob(process.SafeHandle, _handle, $"Failed to query job membership for process {process.Id} in job '{Name}'.");
    }

    internal WindowsJobSettings QueryAppliedSettings()
    {
        ThrowIfDisposed();

        var extended = NativeMethods.QueryInformationJobObject<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(
            _handle,
            NativeMethods.JobObjectExtendedLimitInformation,
            $"Failed to query extended limits for job '{Name}'.");

        var cpuRateControlFlags = 0u;
        var cpuRate = 0u;
        if (_cpuRateControlApplied)
        {
            var cpu = NativeMethods.QueryInformationJobObject<NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(
                _handle,
                NativeMethods.JobObjectCpuRateControlInformation,
                $"Failed to query CPU rate control for job '{Name}'.");

            cpuRateControlFlags = cpu.ControlFlags;
            cpuRate = cpu.CpuRate;
        }

        return new WindowsJobSettings(
            extended.BasicLimitInformation.LimitFlags,
            (ulong)extended.ProcessMemoryLimit,
            (ulong)extended.JobMemoryLimit,
            extended.BasicLimitInformation.ActiveProcessLimit,
            cpuRateControlFlags,
            cpuRate);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0 || _handle.IsClosed;

    private void ApplySettings(WindowsJobSettings settings)
    {
        var extended = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = settings.LimitFlags,
                ActiveProcessLimit = settings.ActiveProcessLimit
            },
            ProcessMemoryLimit = checked((nuint)settings.ProcessMemoryLimit),
            JobMemoryLimit = checked((nuint)settings.JobMemoryLimit)
        };

        NativeMethods.SetInformationJobObject(
            _handle,
            NativeMethods.JobObjectExtendedLimitInformation,
            ref extended,
            $"Failed to configure extended limits for job '{Name}'.");

        if (!_cpuRateControlApplied)
        {
            return;
        }

        var cpu = new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = settings.CpuRateControlFlags,
            CpuRate = settings.CpuRate
        };

        NativeMethods.SetInformationJobObject(
            _handle,
            NativeMethods.JobObjectCpuRateControlInformation,
            ref cpu,
            $"Failed to configure CPU rate control for job '{Name}'.");
    }

    private bool IsEmpty()
    {
        if (IsDisposed)
        {
            return true;
        }

        try
        {
            var accounting = NativeMethods.QueryInformationJobObject<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(
                _handle,
                NativeMethods.JobObjectBasicAccountingInformation,
                $"Failed to query accounting information for job '{Name}'.");

            return accounting.ActiveProcesses == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(WindowsJob));
        }
    }
}
