using System.Diagnostics;
using System.Runtime.Versioning;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Windows;

[SupportedOSPlatform("windows")]
public sealed class NamedEventStopStrategy : IGracefulStopStrategy
{
    public const string ConfigurationKey = "Worker:ShutdownEvent";

    public IGracefulStopHandle Prepare(string moduleName, IList<string> arguments)
    {
        ModuleName.Validate(moduleName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named event graceful stop requires Windows.");
        }

        var name = $@"Local\modular-monolith-stop-{moduleName}-{Guid.NewGuid():N}";
        var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        arguments.Add($"--{ConfigurationKey}={name}");
        return new Handle(stopEvent);
    }

    private sealed class Handle(EventWaitHandle stopEvent) : IGracefulStopHandle
    {
        private readonly EventWaitHandle _stopEvent = stopEvent;
        private Process? _process;
        private int _disposed;

        public void Attach(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            _process = process;
        }

        public bool TryRequestStop()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var process = _process;
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited && _stopEvent.Set();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopEvent.Dispose();
        }
    }
}
