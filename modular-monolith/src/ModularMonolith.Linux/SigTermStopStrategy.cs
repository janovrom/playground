using System.Diagnostics;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Linux;

public sealed class SigTermStopStrategy : IGracefulStopStrategy
{
    public IGracefulStopHandle Prepare(string moduleName, IList<string> arguments) => new Handle();

    private sealed class Handle : IGracefulStopHandle
    {
        private int _processId;

        public void Attach(Process process) => _processId = process.Id;

        public bool TryRequestStop()
            => _processId != 0 && OperatingSystem.IsLinux() && LinuxSignals.Send(_processId, LinuxSignals.SigTerm);

        public void Dispose()
        {
        }
    }
}
