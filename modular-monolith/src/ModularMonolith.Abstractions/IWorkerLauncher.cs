namespace ModularMonolith.Abstractions;

public interface IWorkerLauncher
{
    WorkerProcess Launch(WorkerLaunchOptions options);
}
