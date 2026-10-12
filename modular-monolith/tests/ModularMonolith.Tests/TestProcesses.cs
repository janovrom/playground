using ModularMonolith.Abstractions;

namespace ModularMonolith.Tests;

internal static class TestProcesses
{
    public static WorkerLaunchOptions Sleep(string moduleName, int seconds, JobLimits? limits = null)
        => OperatingSystem.IsWindows()
            ? new WorkerLaunchOptions(moduleName, "cmd.exe", ["/c", $"ping -n {seconds + 1} 127.0.0.1 >NUL"], limits)
            : new WorkerLaunchOptions(moduleName, "/bin/sh", ["-c", $"sleep {seconds}"], limits);

    public static WorkerLaunchOptions Exit(string moduleName, int exitCode, JobLimits? limits = null)
        => OperatingSystem.IsWindows()
            ? new WorkerLaunchOptions(moduleName, "cmd.exe", ["/c", $"exit {exitCode}"], limits)
            : new WorkerLaunchOptions(moduleName, "/bin/sh", ["-c", $"exit {exitCode}"], limits);
}
