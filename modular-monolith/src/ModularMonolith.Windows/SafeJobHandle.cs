using Microsoft.Win32.SafeHandles;

namespace ModularMonolith.Windows;

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeJobHandle(IntPtr handle)
        : this()
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
