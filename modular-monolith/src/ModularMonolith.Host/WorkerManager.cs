using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Abstractions;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Host;

internal sealed record WorkerStatus(string Name, int? ProcessId, string State, int RestartCount);
internal sealed record WorkerRoutingSnapshot(string Name, WorkerEndpoint? Endpoint, bool IsReady, string Revision);

internal interface IWorkerRoutingSource
{
    /// <summary>Occurs when a worker destination or readiness state changes.</summary>
    event EventHandler<IReadOnlyList<WorkerRoutingSnapshot>>? TopologyChanged;

    /// <summary>Returns the current destination and readiness state for all workers.</summary>
    IReadOnlyList<WorkerRoutingSnapshot> GetRoutingSnapshot();
}

internal sealed class WorkerManager : IHostedService, IWorkerEndpointResolver, IWorkerRoutingSource
{
    private static readonly TimeSpan MinimumRestartDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromSeconds(30);

    private readonly IWorkerLauncher _workerLauncher;
    private readonly IWorkerTransport _transport;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkerManager> _logger;
    private readonly RequestDrainTracker _requestDrainTracker;
    private readonly WorkerSigningKey _signingKey;
    private readonly WorkerRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly int _crashThreshold;
    private readonly TimeSpan _crashWindow;
    private readonly Dictionary<string, ManagedWorker> _workers;
    private readonly object _gate = new();
    private readonly List<Task> _supervisors = [];
    private CancellationTokenSource? _supervisorCancellation;
    private IEndpointAllocator? _endpointAllocator;
    private int _stopping;

    /// <summary>Occurs when worker destinations or readiness states change.</summary>
    public event EventHandler<IReadOnlyList<WorkerRoutingSnapshot>>? TopologyChanged;

    internal WorkerSigningKey SigningKey => _signingKey;
    internal WorkerRequestTracker RequestTracker => _requestTracker;

    public WorkerManager(
        IWorkerLauncher workerLauncher,
        IWorkerTransport transport,
        IHostModuleCatalog modules,
        IConfiguration configuration,
        ILogger<WorkerManager> logger,
        RequestDrainTracker requestDrainTracker,
        WorkerSigningKey? signingKey = null,
        WorkerRequestTracker? requestTracker = null,
        TimeProvider? timeProvider = null)
    {
        _workerLauncher = workerLauncher;
        _transport = transport;
        _configuration = configuration;
        _logger = logger;
        _requestDrainTracker = requestDrainTracker;
        _signingKey = signingKey ?? WorkerSigningKey.Create();
        _requestTracker = requestTracker ?? new WorkerRequestTracker(configuration, timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _crashThreshold = configuration.GetValue("Workers:CircuitBreaker:MaxCrashes", 3);
        var crashWindowSeconds = configuration.GetValue("Workers:CircuitBreaker:WindowSeconds", 120);
        if (_crashThreshold <= 0)
        {
            throw new InvalidOperationException("Workers:CircuitBreaker:MaxCrashes must be greater than zero.");
        }

        if (crashWindowSeconds <= 0)
        {
            throw new InvalidOperationException("Workers:CircuitBreaker:WindowSeconds must be greater than zero.");
        }

        _crashWindow = TimeSpan.FromSeconds(crashWindowSeconds);
        _workers = modules.Modules.Select(CreateDefinition).ToDictionary(worker => worker.Name, StringComparer.Ordinal);
    }

    /// <summary>Allocates endpoints, starts every registered worker, and waits for readiness.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _endpointAllocator = _transport.CreateAllocator(_configuration);

        try
        {
            foreach (var worker in _workers.Values)
            {
                worker.Endpoint = _endpointAllocator.Allocate(worker.Name);
                worker.Process = Launch(worker);
                lock (_gate)
                {
                    worker.State = "Starting";
                }
                PublishTopologyChanged();
            }

            await Task.WhenAll(_workers.Values.Select(worker => WaitUntilReadyAsync(worker, cancellationToken)));
        }
        catch
        {
            foreach (var worker in _workers.Values)
            {
                lock (_gate)
                {
                    worker.Process?.Dispose();
                    worker.Process = null;
                    worker.State = "Stopped";
                }
            }

            ReleaseEndpoints();
            PublishTopologyChanged();
            throw;
        }

        foreach (var worker in _workers.Values)
        {
            lock (_gate)
            {
                worker.State = "Running";
            }

            _logger.LogInformation("Worker {WorkerName} is ready with pid {ProcessId}.", worker.Name, worker.Process!.ProcessId);
            PublishTopologyChanged();
        }

        _supervisorCancellation = new CancellationTokenSource();
        foreach (var worker in _workers.Values)
        {
            _supervisors.Add(SuperviseAsync(worker, _supervisorCancellation.Token));
        }
    }

    /// <summary>Drains requests, stops every worker, and releases allocated endpoints.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        var gracePeriod = TimeSpan.FromSeconds(_configuration.GetValue("Workers:ShutdownGracePeriod", 10));
        var stopwatch = Stopwatch.StartNew();
        _requestDrainTracker.BeginDrain();
        _supervisorCancellation?.Cancel();

        try
        {
            await _requestDrainTracker.WaitForDrainAsync(cancellationToken).WaitAsync(gracePeriod, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Host request drain exceeded {GracePeriod}; stopping workers anyway.", gracePeriod);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Host shutdown was cancelled before all requests drained; stopping workers.");
        }
        WorkerProcess[] processes;
        lock (_gate)
        {
            processes = _workers.Values.Select(worker => worker.Process).Where(process => process is not null).Cast<WorkerProcess>().ToArray();
            foreach (var worker in _workers.Values)
            {
                worker.State = "Stopping";
            }
        }
        PublishTopologyChanged();

        var remainingGrace = gracePeriod - stopwatch.Elapsed;
        if (remainingGrace < TimeSpan.Zero)
        {
            remainingGrace = TimeSpan.Zero;
        }

        await Task.WhenAll(processes.Select(async process =>
        {
            try
            {
                await process.StopAsync(remainingGrace, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                process.Stop();
            }
        }));

        foreach (var supervisor in _supervisors)
        {
            try
            {
                await supervisor.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        foreach (var worker in _workers.Values)
        {
            lock (_gate)
            {
                worker.Process?.Dispose();
                worker.Process = null;
                worker.State = "Stopped";
            }
            PublishTopologyChanged();
        }

        ReleaseEndpoints();
        _supervisorCancellation?.Dispose();
    }

    /// <summary>Returns a point-in-time process status for every registered worker.</summary>
    public IReadOnlyList<WorkerStatus> GetStatus()
    {
        lock (_gate)
        {
            return _workers.Values
                .Select(worker => new WorkerStatus(worker.Name, worker.Process?.ProcessId, worker.State, worker.RestartCount))
                .ToArray();
        }
    }

    /// <summary>Returns the current IPC endpoint for one registered worker.</summary>
    public WorkerEndpoint GetEndpoint(string name)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(name, out var worker) && worker.Endpoint is not null
                ? worker.Endpoint
                : throw new InvalidOperationException($"Worker endpoint for '{name}' is not configured.");
        }
    }

    /// <summary>Reports whether one registered worker is ready to receive requests.</summary>
    public bool IsRunning(string name)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(name, out var worker)
                ? worker.State == "Running"
                : throw new InvalidOperationException($"Worker '{name}' is not registered.");
        }
    }

    /// <summary>Resets a registered worker's open circuit and permits its supervisor to restart it.</summary>
    public bool TryResetWorker(string name)
    {
        var reset = false;
        lock (_gate)
        {
            if (!_workers.TryGetValue(name, out var worker))
            {
                return false;
            }

            if (worker.State == "Open")
            {
                worker.CrashTimes.Clear();
                worker.State = "Restarting";
                worker.ResetRequested.TrySetResult();
                reset = true;
            }
        }

        if (reset)
        {
            PublishTopologyChanged();
        }

        return reset;
    }

    /// <summary>Counts a process exit discovered while a request was using the worker.</summary>
    public void ObserveWorkerFailure(string name)
    {
        ManagedWorker? worker;
        WorkerProcess? process;
        lock (_gate)
        {
            if (!_workers.TryGetValue(name, out worker))
            {
                return;
            }

            process = worker.Process;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                RecordUnexpectedExit(worker, process);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Returns the routing state used to rebuild YARP configuration.</summary>
    public IReadOnlyList<WorkerRoutingSnapshot> GetRoutingSnapshot()
    {
        lock (_gate)
        {
            return _workers.Values
                .Select(worker => new WorkerRoutingSnapshot(
                    worker.Name,
                    worker.Endpoint,
                    worker.State == "Running",
                    $"{worker.RestartCount}:{worker.State}:{worker.Endpoint?.Address ?? "none"}"))
                .ToArray();
        }
    }

    private ManagedWorker CreateDefinition(HostModuleDescriptor module)
    {
        var section = _configuration.GetSection($"Workers:{module.ConfigSection}");
        var (executable, arguments) = WorkerCommandResolver.Resolve(section, module.WorkerProject, AppContext.BaseDirectory);
        arguments.AddRange(module.GetWorkerArguments(_configuration));

        return new ManagedWorker(module.Name, executable, arguments, ReadLimits(section.GetSection("Limits"), module.DefaultLimits));
    }

    private void PublishTopologyChanged()
    {
        TopologyChanged?.Invoke(this, GetRoutingSnapshot());
    }

    private static JobLimits ReadLimits(IConfigurationSection section, JobLimits defaults)
    {
        var memoryMaxMegabytes = section.GetValue<long?>("MemoryMaxMb");
        var memoryHighMegabytes = section.GetValue<long?>("MemoryHighMb");
        return defaults with
        {
            MemoryMaxBytes = memoryMaxMegabytes is null ? defaults.MemoryMaxBytes : memoryMaxMegabytes * 1024 * 1024,
            MemoryHighBytes = memoryHighMegabytes is null ? defaults.MemoryHighBytes : memoryHighMegabytes * 1024 * 1024,
            CpuCores = section.GetValue<double?>("CpuCores") ?? defaults.CpuCores,
            MaxProcesses = section.GetValue<int?>("MaxProcesses") ?? defaults.MaxProcesses
        };
    }

    private WorkerProcess Launch(ManagedWorker worker)
    {
        var endpoint = worker.Endpoint!;
        var arguments = worker.Arguments
            .Concat([$"--Worker:Transport={endpoint.Transport}", $"--Worker:Endpoint={endpoint.Address}"])
            .ToArray();
        return _workerLauncher.Launch(new WorkerLaunchOptions(
            worker.Name,
            worker.ExecutablePath,
            arguments,
            worker.Limits,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkerInternalContextConstants.SigningKeyEnvironmentVariable] = _signingKey.ToBase64()
            }));
    }

    private async Task WaitUntilReadyAsync(ManagedWorker worker, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_configuration.GetValue("Workers:StartupTimeoutSeconds", 20));
        var stopwatch = Stopwatch.StartNew();
        using var client = _transport.CreateHttpClient(worker.Endpoint!);

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worker.Process!.HasExited)
            {
                throw new InvalidOperationException($"Worker '{worker.Name}' exited with code {worker.Process.ExitCode} before becoming ready.");
            }

            try
            {
                using var response = await client.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (SocketException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new TimeoutException($"Worker '{worker.Name}' did not become ready within {timeout}.");
    }

    private async Task SuperviseAsync(ManagedWorker worker, CancellationToken cancellationToken)
    {
        var restartDelay = MinimumRestartDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var process = worker.Process;
            if (process is null)
            {
                return;
            }

            var runningDuration = Stopwatch.StartNew();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var exitCode = process.ExitCode;
            if (runningDuration.Elapsed >= TimeSpan.FromMinutes(1))
            {
                restartDelay = MinimumRestartDelay;
            }

            RecordUnexpectedExit(worker, process);
            bool circuitOpen;
            lock (_gate)
            {
                if (ReferenceEquals(worker.Process, process))
                {
                    worker.Process = null;
                }

                circuitOpen = worker.State == "Open";
            }
            PublishTopologyChanged();

            RemoveStaleEndpoint(worker);
            lock (_gate)
            {
                worker.Endpoint = null;
            }
            process.Dispose();

            if (circuitOpen)
            {
                try
                {
                    await worker.ResetRequested.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                lock (_gate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    worker.ResetRequested = NewResetSignal();
                    worker.Endpoint = _endpointAllocator!.Allocate(worker.Name);
                    worker.State = "Restarting";
                }
                restartDelay = MinimumRestartDelay;
                PublishTopologyChanged();
            }
            else
            {
                lock (_gate)
                {
                    worker.Endpoint = _endpointAllocator!.Allocate(worker.Name);
                }
            }

            var delayBeforeRestart = restartDelay;
            restartDelay = TimeSpan.FromSeconds(Math.Min(restartDelay.TotalSeconds * 2, MaximumRestartDelay.TotalSeconds));
            if (!circuitOpen)
            {
                _logger.LogWarning("Worker {WorkerName} exited with code {ExitCode}; restarting after {Delay}.", worker.Name, exitCode, delayBeforeRestart);
                try
                {
                    await Task.Delay(delayBeforeRestart, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    WorkerProcess processToStart;
                    lock (_gate)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        processToStart = Launch(worker);
                        worker.Process = processToStart;
                        worker.State = "Starting";
                    }
                    PublishTopologyChanged();

                    await WaitUntilReadyAsync(worker, cancellationToken);
                    lock (_gate)
                    {
                        worker.State = "Running";
                        worker.ResetRequested = NewResetSignal();
                    }
                    PublishTopologyChanged();

                    _logger.LogInformation("Worker {WorkerName} restarted with pid {ProcessId}.", worker.Name, processToStart.ProcessId);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        worker.Process?.Dispose();
                        worker.Process = null;
                    }

                    return;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or HttpRequestException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    _logger.LogError(exception, "Failed to restart worker {WorkerName}.", worker.Name);
                    lock (_gate)
                    {
                        worker.Process?.Dispose();
                        worker.Process = null;
                        worker.State = "Restarting";
                    }

                    await Task.Delay(restartDelay, cancellationToken);
                    restartDelay = TimeSpan.FromSeconds(Math.Min(restartDelay.TotalSeconds * 2, MaximumRestartDelay.TotalSeconds));
                }
            }
        }
    }

    private void RecordUnexpectedExit(ManagedWorker worker, WorkerProcess process)
    {
        var opened = false;
        var crashCount = 0;
        lock (_gate)
        {
            if (Volatile.Read(ref _stopping) != 0
                || worker.State != "Running"
                || !ReferenceEquals(worker.Process, process)
                || ReferenceEquals(worker.LastCountedProcess, process))
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            while (worker.CrashTimes.TryPeek(out var oldest) && now - oldest > _crashWindow)
            {
                worker.CrashTimes.Dequeue();
            }

            worker.CrashTimes.Enqueue(now);
            worker.RestartCount++;
            _requestTracker.RecordWorkerCrash(worker.Name);
            opened = worker.CrashTimes.Count >= _crashThreshold;
            worker.State = opened ? "Open" : "Restarting";
            worker.LastCountedProcess = process;
            crashCount = worker.CrashTimes.Count;
        }

        if (opened)
        {
            _logger.LogError(
                "Worker {WorkerName} opened its circuit after {CrashCount} crashes within {Window}.",
                worker.Name,
                crashCount,
                _crashWindow);
        }

        PublishTopologyChanged();
    }

    private static TaskCompletionSource NewResetSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseEndpoints()
    {
        _endpointAllocator?.Dispose();
        _endpointAllocator = null;
    }

    private void RemoveStaleEndpoint(ManagedWorker worker)
    {
        if (worker.Endpoint is null)
        {
            return;
        }

        try
        {
            _transport.Cleanup(worker.Endpoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not remove stale endpoint for worker {WorkerName} at {Endpoint}.", worker.Name, worker.Endpoint.Address);
        }
    }

    private sealed class ManagedWorker(string name, string executablePath, IReadOnlyList<string> arguments, JobLimits limits)
    {
        public string Name { get; } = name;
        public string ExecutablePath { get; } = executablePath;
        public IReadOnlyList<string> Arguments { get; } = arguments;
        public JobLimits Limits { get; } = limits;
        public Queue<DateTimeOffset> CrashTimes { get; } = new();
        public WorkerEndpoint? Endpoint { get; set; }
        public WorkerProcess? Process { get; set; }
        public WorkerProcess? LastCountedProcess { get; set; }
        public TaskCompletionSource ResetRequested { get; set; } = NewResetSignal();
        public string State { get; set; } = "Stopped";
        public int RestartCount { get; set; }
    }
}
