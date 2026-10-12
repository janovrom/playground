using System.Diagnostics;

namespace ModularMonolith.Abstractions;

/// <summary>Starts workers without any resource governance; used for local development.</summary>
public sealed class LocalWorkerLauncher(IGracefulStopStrategy? stopStrategy = null) : IWorkerLauncher
{
    private readonly IGracefulStopStrategy _stopStrategy = stopStrategy ?? NoGracefulStopStrategy.Instance;

    public WorkerProcess Launch(WorkerLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = (options.Arguments ?? []).ToList();
        var stopHandle = _stopStrategy.Prepare(options.ModuleName, arguments);
        Process? process = null;
        try
        {
            process = WorkerProcessStarter.Start(options, arguments);
            stopHandle.Attach(process);
            return new WorkerProcess(process, job: null, stopHandle);
        }
        catch
        {
            WorkerProcessStarter.Discard(process);
            stopHandle.Dispose();
            throw;
        }
    }
}

internal static class WorkerProcessStarter
{
    public static Process Start(WorkerLaunchOptions options, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start worker '{options.ModuleName}'.");
    }

    public static void Discard(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
