using ModularMonolith.Abstractions;

namespace ModularMonolith.Windows;

public sealed class WindowsProcessGuard : IProcessGuard
{
    public void EnsureKillOnParentExit()
    {
        // The Host's job object already has KILL_ON_JOB_CLOSE.
    }
}
