using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace ModularMonolith.Platform.Transport;

public interface IWorkerTransport
{
    string Name { get; }

    /// <summary>Host side: creates the per-run allocator that owns any shared resources (for example the socket directory).</summary>
    IEndpointAllocator CreateAllocator(IConfiguration configuration);

    /// <summary>Host side: builds the handler an <see cref="HttpClient"/> uses to reach a worker.</summary>
    HttpMessageHandler CreateClientHandler(WorkerEndpoint endpoint);

    /// <summary>Worker side: binds Kestrel to the endpoint, removing any stale leftovers first.</summary>
    void Listen(KestrelServerOptions options, WorkerEndpoint endpoint);

    /// <summary>Removes endpoint leftovers after the owning worker exited. A no-op where the OS releases them automatically.</summary>
    void Cleanup(WorkerEndpoint endpoint);
}

public interface IEndpointAllocator : IDisposable
{
    WorkerEndpoint Allocate(string moduleName);
}

public static class WorkerTransportExtensions
{
    public static HttpClient CreateHttpClient(this IWorkerTransport transport, WorkerEndpoint endpoint, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(endpoint);

        return new HttpClient(transport.CreateClientHandler(endpoint))
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        };
    }
}
