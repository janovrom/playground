using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ModularMonolith.Windows;

internal static partial class NativeMethods
{
    internal const int JobObjectBasicAccountingInformation = 1;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const int JobObjectCpuRateControlInformation = 15;

    internal const uint JobObjectLimitActiveProcess = 0x00000008;
    internal const uint JobObjectLimitProcessMemory = 0x00000100;
    internal const uint JobObjectLimitJobMemory = 0x00000200;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    internal const uint JobObjectCpuRateControlEnable = 0x00000001;
    internal const uint JobObjectCpuRateControlHardCap = 0x00000004;

    internal static SafeJobHandle CreateJobObjectW(IntPtr attrs, string? name)
    {
        var handle = CreateJobObjectWCore(attrs, name);
        return new SafeJobHandle(handle);
    }

    internal static unsafe void SetInformationJobObject<T>(SafeJobHandle jobHandle, int jobObjectInfoClass, ref T value, string message)
        where T : unmanaged
    {
        fixed (T* valuePointer = &value)
        {
            if (!SetInformationJobObjectCore(jobHandle, jobObjectInfoClass, valuePointer, (uint)sizeof(T)))
            {
                ThrowLastWin32Exception(message);
            }
        }
    }

    internal static unsafe T QueryInformationJobObject<T>(SafeJobHandle jobHandle, int jobObjectInfoClass, string message)
        where T : unmanaged
    {
        T value = default;
        if (!QueryInformationJobObjectCore(jobHandle, jobObjectInfoClass, &value, (uint)sizeof(T), out _))
        {
            ThrowLastWin32Exception(message);
        }

        return value;
    }

    internal static void AssignProcessToJobObject(SafeJobHandle jobHandle, SafeProcessHandle processHandle, string message)
    {
        if (!AssignProcessToJobObjectCore(jobHandle, processHandle))
        {
            ThrowLastWin32Exception(message);
        }
    }

    internal static void TerminateJobObject(SafeJobHandle jobHandle, uint exitCode, string message)
    {
        if (!TerminateJobObjectCore(jobHandle, exitCode))
        {
            ThrowLastWin32Exception(message);
        }
    }

    internal static bool IsProcessInJob(SafeProcessHandle processHandle, SafeJobHandle jobHandle, string message)
    {
        if (!IsProcessInJobCore(processHandle, jobHandle, out var result))
        {
            ThrowLastWin32Exception(message);
        }

        return result;
    }

    private static void ThrowLastWin32Exception(string message)
        => throw new Win32Exception(Marshal.GetLastPInvokeError(), message);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectWCore(IntPtr attrs, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObjectCore(
        SafeJobHandle jobHandle,
        int jobObjectInfoClass,
        void* jobObjectInfo,
        uint jobObjectInfoLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObjectCore(
        SafeJobHandle jobHandle,
        int jobObjectInfoClass,
        void* jobObjectInfo,
        uint jobObjectInfoLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObjectCore(SafeJobHandle jobHandle, SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObjectCore(SafeJobHandle jobHandle, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJobCore(SafeProcessHandle processHandle, SafeJobHandle jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }
}
