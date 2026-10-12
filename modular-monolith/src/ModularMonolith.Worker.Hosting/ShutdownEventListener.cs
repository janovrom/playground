using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModularMonolith.Worker.Hosting;

/// <summary>Windows counterpart of SIGTERM: the Host signals a named event and the worker drains through the host lifetime.</summary>
internal sealed class ShutdownEventListener(string eventName, IHostApplicationLifetime lifetime, ILogger<ShutdownEventListener> logger) : IHostedService, IDisposable
{
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named shutdown events require Windows.");
        }

        _event = EventWaitHandle.OpenExisting(eventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _event,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    logger.LogInformation("Shutdown requested by the Host.");
                    lifetime.StopApplication();
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        _event?.Dispose();
        _event = null;
    }
}
