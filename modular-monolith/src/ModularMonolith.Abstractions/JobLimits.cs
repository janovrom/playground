namespace ModularMonolith.Abstractions;

/// <summary>
/// Platform-neutral resource limits for one worker process. A <see langword="null"/> member means "unlimited".
/// </summary>
/// <param name="MemoryMaxBytes">Hard memory limit (cgroup <c>memory.max</c>, Job Object process/job memory limit).</param>
/// <param name="MemoryHighBytes">Soft reclaim threshold (cgroup <c>memory.high</c>); ignored where the OS has no equivalent.</param>
/// <param name="CpuCores">CPU quota expressed in cores, for example 0.5 for half of one core.</param>
/// <param name="MaxProcesses">Process limit (cgroup <c>pids.max</c> also counts threads; Job Objects count processes only).</param>
public sealed record JobLimits(
    long? MemoryMaxBytes = 128L * 1024 * 1024,
    long? MemoryHighBytes = 96L * 1024 * 1024,
    double? CpuCores = 0.5,
    int? MaxProcesses = 64)
{
    public static JobLimits Unlimited { get; } = new(null, null, null, null);
}
