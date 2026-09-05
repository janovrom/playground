using Microsoft.Extensions.Hosting;
using ModularMonolith.Linux;
using System.Collections.Concurrent;

namespace ModularMonolith.Host;

internal sealed class WorkerManager : IHostedService
{
    private readonly IWorkerLauncher _workerLauncher;
    private readonly ConcurrentDictionary<int, WorkerInfo> _workers = new();
    private int _stopping;

    public WorkerManager(IWorkerLauncher workerLauncher)
    {
        _workerLauncher = workerLauncher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);

        var workers = _workers.Values.ToArray();
        foreach (var worker in workers)
        {
            worker.State = WorkerState.Stopping;
            try
            {
                worker.Process.Stop();
            }
            catch (InvalidOperationException) when (worker.Process.HasExited)
            {
                // The exit callback may race with shutdown.
            }
        }

        foreach (var worker in workers)
        {
            try
            {
                await worker.Process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
                // The exit callback already completed cleanup.
            }
            finally
            {
                worker.State = WorkerState.Stopped;
                worker.Process.Dispose();
            }
        }

        _workers.Clear();
    }

    public WorkerProcess LaunchWorker(WorkerLaunchOptions options)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new InvalidOperationException("The worker manager is stopping.");
        }

        WorkerProcess workerProcess = _workerLauncher.Launch(options);
        var workerInfo = new WorkerInfo(
            ModuleName: options.ModuleName,
            Process: workerProcess,
            State: WorkerState.Running,
            StartTime: DateTime.UtcNow);

        if (!_workers.TryAdd(workerProcess.ProcessId, workerInfo))
        {
            workerProcess.Dispose();
            throw new InvalidOperationException($"Worker process '{workerProcess.ProcessId}' is already registered.");
        }

        workerProcess.Exited += args =>
        {
            if (_workers.TryGetValue(workerProcess.ProcessId, out var current))
            {
                current.ExitTime = args.ExitTime;
                current.ExitCode = args.ExitCode;
                current.State = Volatile.Read(ref _stopping) != 0
                    ? WorkerState.Stopped
                    : args.ExitCode == 0 ? WorkerState.Exited : WorkerState.Failed;
                _workers.TryRemove(workerProcess.ProcessId, out _);
            }

            workerProcess.Dispose();
        };

        return workerProcess;
    }

    private sealed class WorkerInfo(
        string ModuleName,
        WorkerProcess Process,
        WorkerState State,
        DateTime StartTime,
        DateTime? ExitTime = null,
        int? ExitCode = null,
        int RestartCount = 0,
        string? FailureReason = null)
    {
        public string ModuleName { get; } = ModuleName;
        public WorkerProcess Process { get; } = Process;
        public WorkerState State { get; set; } = State;
        public DateTime StartTime { get; } = StartTime;
        public DateTime? ExitTime { get; set; } = ExitTime;
        public int? ExitCode { get; set; } = ExitCode;
        public int RestartCount { get; set; } = RestartCount;
        public string? FailureReason { get; set; } = FailureReason;
    }

    private enum WorkerState
    {
        Starting,
        Running,
        Stopping,
        Stopped,
        Exited,
        Failed,
        Restarting
    }
}