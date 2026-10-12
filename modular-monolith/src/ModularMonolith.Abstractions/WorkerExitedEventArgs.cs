namespace ModularMonolith.Abstractions;

public sealed class WorkerExitedEventArgs : EventArgs
{
    public WorkerExitedEventArgs(int exitCode, DateTime exitTime)
    {
        ExitCode = exitCode;
        ExitTime = exitTime;
    }

    public int ExitCode { get; }

    public DateTime ExitTime { get; }
}
