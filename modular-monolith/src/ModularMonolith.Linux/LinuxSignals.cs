using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ModularMonolith.Linux;

public static partial class LinuxSignals
{
    public const int SigKill = 9;
    public const int SigTerm = 15;
    private const int NoSuchProcess = 3;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int processId, int signal);

    public static bool Send(int processId, int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("POSIX signals are supported only on Linux.");
        }

        if (kill(processId, signal) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == NoSuchProcess)
        {
            return false;
        }

        throw new Win32Exception(error, $"Failed to send signal {signal} to process {processId}.");
    }
}
