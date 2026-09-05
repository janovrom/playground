using System.Diagnostics;

namespace ModularMonolith.Linux;

public sealed class LocalWorkerLauncher : IWorkerLauncher
{
    public WorkerProcess Launch(WorkerLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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

        return new WorkerProcess(process);
    }
}