using System.Diagnostics;

namespace ModularMonolith.Abstractions;

/// <summary>Starts each worker inside its own <see cref="IJob"/> (cgroup on Linux, Job Object on Windows).</summary>
/// <param name="applyLimits">When <see langword="false"/> the job is still created but every limit is dropped.</param>
public sealed class JobWorkerLauncher(
    IJobFactory jobFactory,
    IGracefulStopStrategy? stopStrategy = null,
    bool applyLimits = true) : IWorkerLauncher
{
    private readonly IJobFactory _jobFactory = jobFactory ?? throw new ArgumentNullException(nameof(jobFactory));
    private readonly IGracefulStopStrategy _stopStrategy = stopStrategy ?? NoGracefulStopStrategy.Instance;

    public WorkerProcess Launch(WorkerLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModuleName.Validate(options.ModuleName);

        var limits = applyLimits ? options.Limits ?? new JobLimits() : JobLimits.Unlimited;
        var arguments = (options.Arguments ?? []).ToList();
        var stopHandle = _stopStrategy.Prepare(options.ModuleName, arguments);
        IJob? job = null;
        Process? process = null;
        try
        {
            job = _jobFactory.Create(options.ModuleName, limits);
            process = WorkerProcessStarter.Start(options, arguments);
            stopHandle.Attach(process);
            job.Assign(process);
            return new WorkerProcess(process, job, stopHandle);
        }
        catch
        {
            WorkerProcessStarter.Discard(process);
            stopHandle.Dispose();
            DisposeQuietly(job);
            throw;
        }
    }

    private static void DisposeQuietly(IJob? job)
    {
        try
        {
            job?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
