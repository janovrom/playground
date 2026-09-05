using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ModularMonolith.Linux;

public sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private int _exitNotified;

    public event Action<WorkerExitedEventArgs>? Exited;

    [MemberNotNullWhen(true, nameof(ExitCode))]
    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    internal WorkerProcess(Process process)
    {
        _process = process;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (_process.HasExited && Interlocked.Exchange(ref _exitNotified, 1) == 0)
        {
            Exited?.Invoke(new WorkerExitedEventArgs(_process.ExitCode, _process.ExitTime));
        }
    }

    public int ProcessId => _process.Id;

    public void Stop()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        Stop();
        _process.Exited -= OnProcessExited;
        _process.Dispose();
    }
}
