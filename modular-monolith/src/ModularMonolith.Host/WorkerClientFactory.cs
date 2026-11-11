using ModularMonolith.Platform.Transport;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ModularMonolith.Host;

internal interface IWorkerEndpointResolver
{
    /// <summary>Returns the endpoint allocated to a registered worker.</summary>
    WorkerEndpoint GetEndpoint(string name);

    /// <summary>Reports whether a registered worker is ready for requests.</summary>
    bool IsRunning(string name);
}

internal interface IWorkerClientFactory
{
    /// <summary>Creates a configured client for the named worker.</summary>
    HttpClient Create(string moduleName, string configSection);
}

internal sealed class WorkerClientFactory(IHttpClientFactory clients, IConfiguration configuration) : IWorkerClientFactory
{
    /// <summary>Creates a worker client with its configured request timeout.</summary>
    public HttpClient Create(string moduleName, string configSection)
    {
        var timeoutSeconds = configuration.GetValue($"Workers:{configSection}:TimeoutSeconds", 10);
        if (timeoutSeconds <= 0)
        {
            throw new InvalidOperationException($"Workers:{configSection}:TimeoutSeconds must be greater than zero.");
        }

        var client = clients.CreateClient(moduleName);
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return client;
    }
}

internal sealed class WorkerReadinessHandler(
    string moduleName,
    IWorkerEndpointResolver endpoints,
    Action<WorkerReadinessHandler>? onDispose = null) : DelegatingHandler
{
    private int _disposed;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!endpoints.IsRunning(moduleName))
        {
            throw new HttpRequestException($"Worker '{moduleName}' is not ready to receive requests.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose?.Invoke(this);
            }
        }
    }
}

internal sealed class WorkerTransportHandlerRegistry(IWorkerTransport transport, IWorkerEndpointResolver endpoints) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<WorkerReadinessHandler> _handlers = [];
    private bool _disposed;

    /// <summary>Creates and owns a transport handler for a worker until shutdown.</summary>
    public HttpMessageHandler Create(string moduleName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handler = new WorkerReadinessHandler(moduleName, endpoints, Remove)
            {
                InnerHandler = transport.CreateClientHandler(endpoints.GetEndpoint(moduleName))
            };
            _handlers.Add(handler);
            return handler;
        }
    }

    /// <summary>Closes all worker transport handlers owned by the Host.</summary>
    public void Dispose()
    {
        WorkerReadinessHandler[] handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            handlers = [.. _handlers];
            _handlers.Clear();
        }

        foreach (var handler in handlers)
        {
            handler.Dispose();
        }
    }

    private void Remove(WorkerReadinessHandler handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }
}

internal static class WorkerHttpClientServiceCollectionExtensions
{
    /// <summary>Registers worker HTTP resilience and transport handler ownership.</summary>
    public static IServiceCollection AddWorkerHttpResilience(this IServiceCollection services)
    {
        services.AddSingleton<WorkerTransportHandlerRegistry>();
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder => builder.AddResilienceHandler("worker-retry", pipeline =>
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = false
            })));
        return services;
    }
}
