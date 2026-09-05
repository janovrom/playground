namespace ModularMonolith.Linux;

public interface IWorkerLauncher
{
    WorkerProcess Launch(WorkerLaunchOptions options);
}
