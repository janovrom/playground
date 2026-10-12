using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Abstractions;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Host;

internal sealed record WorkerStatus(string Name, int? ProcessId, string State, int RestartCount);

internal sealed class WorkerManager : IHostedService, IWorkerEndpointResolver
{
    private static readonly TimeSpan MinimumRestartDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromSeconds(30);

    private readonly IWorkerLauncher _workerLauncher;
    private readonly IWorkerTransport _transport;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkerManager> _logger;
    private readonly RequestDrainTracker _requestDrainTracker;
    private readonly Dictionary<string, ManagedWorker> _workers;
    private readonly object _gate = new();
    private readonly List<Task> _supervisors = [];
    private CancellationTokenSource? _supervisorCancellation;
    private IEndpointAllocator? _endpointAllocator;
    private int _stopping;

    public WorkerManager(
        IWorkerLauncher workerLauncher,
        IWorkerTransport transport,
        IEnumerable<IHostModule> modules,
        IConfiguration configuration,
        ILogger<WorkerManager> logger,
        RequestDrainTracker requestDrainTracker)
    {
        _workerLauncher = workerLauncher;
        _transport = transport;
        _configuration = configuration;
        _logger = logger;
        _requestDrainTracker = requestDrainTracker;
        _workers = modules.Select(CreateDefinition).ToDictionary(worker => worker.Name, StringComparer.Ordinal);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _endpointAllocator = _transport.CreateAllocator(_configuration);

        try
        {
            foreach (var worker in _workers.Values)
            {
                worker.Endpoint = _endpointAllocator.Allocate(worker.Name);
                worker.Process = Launch(worker);
                worker.State = "Starting";
            }

            await Task.WhenAll(_workers.Values.Select(worker => WaitUntilReadyAsync(worker, cancellationToken)));
        }
        catch
        {
            foreach (var worker in _workers.Values)
            {
                worker.Process?.Dispose();
                worker.Process = null;
                worker.State = "Stopped";
            }

            ReleaseEndpoints();
            throw;
        }

        foreach (var worker in _workers.Values)
        {
            worker.State = "Running";
            _logger.LogInformation("Worker {WorkerName} is ready with pid {ProcessId}.", worker.Name, worker.Process!.ProcessId);
        }

        _supervisorCancellation = new CancellationTokenSource();
        foreach (var worker in _workers.Values)
        {
            _supervisors.Add(SuperviseAsync(worker, _supervisorCancellation.Token));
        }
    }

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
            worker.Process?.Dispose();
            worker.Process = null;
            worker.State = "Stopped";
        }

        ReleaseEndpoints();
        _supervisorCancellation?.Dispose();
    }

    public IReadOnlyList<WorkerStatus> GetStatus()
    {
        lock (_gate)
        {
            return _workers.Values
                .Select(worker => new WorkerStatus(worker.Name, worker.Process?.ProcessId, worker.State, worker.RestartCount))
                .ToArray();
        }
    }

    public WorkerEndpoint GetEndpoint(string name)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(name, out var worker) && worker.Endpoint is not null
                ? worker.Endpoint
                : throw new InvalidOperationException($"Worker endpoint for '{name}' is not configured.");
        }
    }

    private ManagedWorker CreateDefinition(IHostModule module)
    {
        var section = _configuration.GetSection($"Workers:{module.ConfigSection}");
        var (executable, arguments) = WorkerCommandResolver.Resolve(section, module.WorkerProject, AppContext.BaseDirectory);
        arguments.AddRange(module.GetWorkerArguments(_configuration));

        return new ManagedWorker(module.Name, executable, arguments, ReadLimits(section.GetSection("Limits"), module.DefaultLimits));
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
        return _workerLauncher.Launch(new WorkerLaunchOptions(worker.Name, worker.ExecutablePath, arguments, worker.Limits));
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

            lock (_gate)
            {
                worker.Process = null;
                worker.State = "Restarting";
                worker.RestartCount++;
            }

            RemoveStaleEndpoint(worker);
            process.Dispose();
            var delayBeforeRestart = restartDelay;
            restartDelay = TimeSpan.FromSeconds(Math.Min(restartDelay.TotalSeconds * 2, MaximumRestartDelay.TotalSeconds));
            _logger.LogWarning("Worker {WorkerName} exited with code {ExitCode}; restarting after {Delay}.", worker.Name, exitCode, delayBeforeRestart);
            try
            {
                await Task.Delay(delayBeforeRestart, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
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

                    await WaitUntilReadyAsync(worker, cancellationToken);
                    lock (_gate)
                    {
                        worker.State = "Running";
                    }

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
        public WorkerEndpoint? Endpoint { get; set; }
        public WorkerProcess? Process { get; set; }
        public string State { get; set; } = "Stopped";
        public int RestartCount { get; set; }
    }
}
