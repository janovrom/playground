using System.Runtime.InteropServices;

namespace ModularMonolith.Linux;

public static partial class LinuxProcessGuard
{
    public const int PrSetDeathSig = 1;
    public const int SigKill = 9;

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    public static void EnsureKillOnParentExit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = prctl(PrSetDeathSig, SigKill, 0, 0, 0);
        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Failed to set PR_SET_PDEATHSIG. Errno: {errno}");
        }
    }
}
