using ModularMonolith.Abstractions;

namespace ModularMonolith.Linux;

public sealed class CgroupJobFactory : IJobFactory
{
    private const int CpuPeriodMicroseconds = 100_000;
    private const int MinimumCpuQuotaMicroseconds = 1_000;
    private static readonly string[] RequiredControllers = ["memory", "cpu", "pids"];
    private readonly string _cgroupRoot;

    public CgroupJobFactory(string? cgroupRoot = null)
    {
        _cgroupRoot = string.IsNullOrWhiteSpace(cgroupRoot)
            ? CgroupPathResolver.GetCurrentProcessRoot()
            : Path.GetFullPath(cgroupRoot);
    }

    public IJob Create(string moduleName, JobLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        EnsureLinux();
        ModuleName.Validate(moduleName);

        PrepareControllerRoot();
        EnableControllers();

        var name = $"module-{moduleName}-{Guid.NewGuid():N}";
        var cgroupPath = Path.Combine(_cgroupRoot, name);
        Directory.CreateDirectory(cgroupPath);
        WriteLimits(cgroupPath, limits);

        var recursiveCleanup = !_cgroupRoot.StartsWith("/sys/fs/cgroup", StringComparison.Ordinal);
        return new CgroupJob(name, cgroupPath, recursiveCleanup);
    }

    internal static string FormatCpuMax(double? cpuCores)
    {
        if (cpuCores is null)
        {
            return $"max {CpuPeriodMicroseconds}";
        }

        if (double.IsNaN(cpuCores.Value) || cpuCores.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuCores), cpuCores, "CPU cores must be greater than zero.");
        }

        var quota = Math.Max(MinimumCpuQuotaMicroseconds, (long)Math.Round(cpuCores.Value * CpuPeriodMicroseconds));
        return $"{quota} {CpuPeriodMicroseconds}";
    }

    private void EnableControllers()
    {
        var controllersPath = Path.Combine(_cgroupRoot, "cgroup.controllers");
        var available = File.ReadAllText(controllersPath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var missing = RequiredControllers.Except(available).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The delegated cgroup root '{_cgroupRoot}' is missing controllers: {string.Join(", ", missing)}.");
        }

        File.WriteAllText(
            Path.Combine(_cgroupRoot, "cgroup.subtree_control"),
            string.Join(' ', RequiredControllers.Select(controller => $"+{controller}")) + "\n");
    }

    private void PrepareControllerRoot()
    {
        var hostPath = Path.Combine(_cgroupRoot, "host");
        Directory.CreateDirectory(hostPath);

        File.WriteAllText(
            Path.Combine(hostPath, "cgroup.procs"),
            $"{Environment.ProcessId}\n");
    }

    private static void WriteLimits(string cgroupPath, JobLimits limits)
    {
        File.WriteAllText(Path.Combine(cgroupPath, "memory.max"), FormatLimit(limits.MemoryMaxBytes) + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "memory.high"), FormatLimit(limits.MemoryHighBytes) + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "cpu.max"), FormatCpuMax(limits.CpuCores) + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "pids.max"), FormatLimit(limits.MaxProcesses) + "\n");
    }

    private static string FormatLimit(long? value)
        => value is null ? "max" : value.Value > 0
            ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Limits must be greater than zero.");

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("cgroup worker isolation requires Linux.");
        }
    }
}
