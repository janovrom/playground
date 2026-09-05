using System.Diagnostics;

namespace ModularMonolith.Linux;

public sealed record CgroupLimits(
    string MemoryMax = "128M",
    string MemoryHigh = "96M",
    string CpuMax = "50000 100000",
    string PidsMax = "64");

public static class CgroupPathResolver
{
    public static string GetCurrentProcessRoot(string cgroupMountPath = "/sys/fs/cgroup", string processCgroupPath = "/proc/self/cgroup")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cgroupMountPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(processCgroupPath);

        foreach (var line in File.ReadLines(processCgroupPath))
        {
            var parts = line.Split([':'], 3, StringSplitOptions.None);
            if (parts.Length == 3 && parts[0] == "0" && parts[1].Length == 0 && parts[2].StartsWith("/", StringComparison.Ordinal))
            {
                return Path.GetFullPath(Path.Combine(cgroupMountPath, parts[2].TrimStart('/')));
            }
        }

        throw new InvalidOperationException($"Could not find the unified cgroup path in '{processCgroupPath}'.");
    }
}

public sealed class CgroupWorkerLauncher : IWorkerLauncher
{
    private static readonly string[] RequiredControllers = ["memory", "cpu", "pids"];
    private readonly string _cgroupRoot;

    public CgroupWorkerLauncher(string? cgroupRoot = null)
    {
        _cgroupRoot = string.IsNullOrWhiteSpace(cgroupRoot)
            ? CgroupPathResolver.GetCurrentProcessRoot()
            : Path.GetFullPath(cgroupRoot);
    }

    public WorkerProcess Launch(WorkerLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureLinux();
        ValidateModuleName(options.ModuleName);

        PrepareControllerRoot();

        var limits = options.Limits ?? new CgroupLimits();
        EnableControllers();

        var cgroupPath = Path.Combine(_cgroupRoot, $"module-{options.ModuleName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cgroupPath);
        WriteLimits(cgroupPath, limits);

        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false
        };

        foreach (var argument in options.Arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start worker '{options.ModuleName}'.");

        try
        {
            File.WriteAllText(Path.Combine(cgroupPath, "cgroup.procs"), $"{process.Id}\n");
            return new WorkerProcess(process);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.Dispose();
            throw;
        }
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

        EnableControllers();
    }

    private static void WriteLimits(string cgroupPath, CgroupLimits limits)
    {
        File.WriteAllText(Path.Combine(cgroupPath, "memory.max"), limits.MemoryMax + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "memory.high"), limits.MemoryHigh + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "cpu.max"), limits.CpuMax + "\n");
        File.WriteAllText(Path.Combine(cgroupPath, "pids.max"), limits.PidsMax + "\n");
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("cgroup worker isolation requires Linux.");
        }
    }

    private static void ValidateModuleName(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || moduleName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Module names may contain only ASCII letters, digits, hyphens, and underscores.", nameof(moduleName));
        }
    }
}
