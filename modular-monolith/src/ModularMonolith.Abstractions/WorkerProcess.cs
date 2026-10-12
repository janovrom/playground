using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ModularMonolith.Abstractions;

public sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly IJob? _job;
    private readonly IGracefulStopHandle? _stopHandle;
    private int _exitNotified;

    public event Action<WorkerExitedEventArgs>? Exited;

    [MemberNotNullWhen(true, nameof(ExitCode))]
    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    internal WorkerProcess(Process process, IJob? job = null, IGracefulStopHandle? stopHandle = null)
    {
        _process = process;
        _job = job;
        _stopHandle = stopHandle;

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
        if (_process.HasExited)
        {
            return;
        }

        _job?.Terminate();

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process may already be terminating (for example after the job was terminated) and refuse a second kill.
                if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    throw;
                }
            }
        }

        if (!_process.HasExited)
        {
            _process.WaitForExit();
        }
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (_process.HasExited)
        {
            return;
        }

        if (_stopHandle is null || !_stopHandle.TryRequestStop())
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
        _stopHandle?.Dispose();
        _job?.Dispose();
    }
}
