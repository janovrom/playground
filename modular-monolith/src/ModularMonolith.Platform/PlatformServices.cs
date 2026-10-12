using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;
using ModularMonolith.Linux;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Windows;

namespace ModularMonolith.Platform;

/// <summary>Selects the OS-specific implementation of each platform abstraction.</summary>
public static class PlatformServices
{
    public static string Name
        => OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown";

    public static IProcessGuard CreateProcessGuard()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessGuard();
        }

        return OperatingSystem.IsWindows() ? new WindowsProcessGuard() : new NoProcessGuard();
    }

    public static IGracefulStopStrategy CreateStopStrategy()
    {
        if (OperatingSystem.IsLinux())
        {
            return new SigTermStopStrategy();
        }

        return OperatingSystem.IsWindows() ? new NamedEventStopStrategy() : NoGracefulStopStrategy.Instance;
    }

    /// <summary>
    /// Linux uses cgroups unless <c>Job:Enabled</c> is false (development). Windows always uses a Job Object so workers die with the Host;
    /// <c>Job:Enabled=false</c> only drops the resource limits there.
    /// </summary>
    public static IWorkerLauncher CreateLauncher(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue("Job:Enabled", true);
        var stopStrategy = CreateStopStrategy();

        if (OperatingSystem.IsWindows())
        {
            return new JobWorkerLauncher(new WindowsJobFactory(), stopStrategy, applyLimits: enabled);
        }

        if (OperatingSystem.IsLinux() && enabled)
        {
            return new JobWorkerLauncher(new CgroupJobFactory(configuration["Job:Root"]), stopStrategy);
        }

        return new LocalWorkerLauncher(stopStrategy);
    }

    public static IWorkerTransport ResolveTransport(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return WorkerTransports.Resolve(configuration["Workers:Transport"]);
    }

    private sealed class NoProcessGuard : IProcessGuard
    {
        public void EnsureKillOnParentExit()
        {
        }
    }
}
