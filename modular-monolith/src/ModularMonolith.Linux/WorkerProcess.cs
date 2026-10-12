using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ModularMonolith.Linux;

public sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly string? _cgroupPath;
    private readonly bool _recursiveCgroupCleanup;
    private int _exitNotified;

    public event Action<WorkerExitedEventArgs>? Exited;

    [MemberNotNullWhen(true, nameof(ExitCode))]
    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    internal WorkerProcess(Process process, string? cgroupPath = null, bool recursiveCgroupCleanup = false)
    {
        _process = process;
        _cgroupPath = cgroupPath;
        _recursiveCgroupCleanup = recursiveCgroupCleanup;

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
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
            }

            if (!_process.HasExited)
            {
                _process.WaitForExit();
            }
        }
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (_process.HasExited)
        {
            return;
        }

        if (!OperatingSystem.IsLinux() || !LinuxSignals.Send(_process.Id, LinuxSignals.SigTerm))
        {
            Stop();
            return;
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(gracePeriod, cancellationToken);
        }
        catch (TimeoutException)
        {
            Stop();
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        Stop();
        _process.Exited -= OnProcessExited;
        _process.Dispose();
        if (_cgroupPath is not null && Directory.Exists(_cgroupPath))
        {
            Directory.Delete(_cgroupPath, _recursiveCgroupCleanup);
        }
    }
}
