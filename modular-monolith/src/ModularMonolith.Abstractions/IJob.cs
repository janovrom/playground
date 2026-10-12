using System.Diagnostics;

namespace ModularMonolith.Abstractions;

/// <summary>
/// An OS resource-governance container for one worker: a cgroup on Linux, a Job Object on Windows.
/// Disposing the job releases the OS resources; implementations also guarantee that no worker outlives it where the OS allows.
/// </summary>
public interface IJob : IDisposable
{
    string Name { get; }

    void Assign(Process process);

    /// <summary>Kills every process in the job. Must not throw when the job is already empty.</summary>
    void Terminate();
}

public interface IJobFactory
{
    IJob Create(string moduleName, JobLimits limits);
}
