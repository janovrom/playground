using System.Diagnostics;

namespace ModularMonolith.Abstractions;

/// <summary>Per-OS mechanism that asks a worker to drain and exit before it is killed.</summary>
public interface IGracefulStopStrategy
{
    /// <summary>Called before the process starts; may append arguments the worker needs to receive the stop request.</summary>
    IGracefulStopHandle Prepare(string moduleName, IList<string> arguments);
}

public interface IGracefulStopHandle : IDisposable
{
    void Attach(Process process);

    /// <returns><see langword="false"/> when no graceful request could be delivered and the caller must kill the worker.</returns>
    bool TryRequestStop();
}

public sealed class NoGracefulStopStrategy : IGracefulStopStrategy
{
    public static NoGracefulStopStrategy Instance { get; } = new();

    public IGracefulStopHandle Prepare(string moduleName, IList<string> arguments) => NoGracefulStopHandle.Instance;

    private sealed class NoGracefulStopHandle : IGracefulStopHandle
    {
        public static NoGracefulStopHandle Instance { get; } = new();

        public void Attach(Process process)
        {
        }

        public bool TryRequestStop() => false;

        public void Dispose()
        {
        }
    }
}
