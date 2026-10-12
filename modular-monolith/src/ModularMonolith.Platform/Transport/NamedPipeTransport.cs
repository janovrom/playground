using System.IO.Pipes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Platform.Transport;

public sealed class NamedPipeTransport : IWorkerTransport
{
    public const string TransportName = "NamedPipe";
    private const string DefaultPipeNamePrefix = "modular-monolith";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public string Name => TransportName;

    public IEndpointAllocator CreateAllocator(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var prefix = configuration["Workers:PipeNamePrefix"];
        return new Allocator(string.IsNullOrWhiteSpace(prefix) ? DefaultPipeNamePrefix : prefix);
    }

    public HttpMessageHandler CreateClientHandler(WorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                // CurrentUserOnly makes the client verify that the pipe server runs as the same user.
                var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(ConnectTimeout, cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            }
        };
    }

    // Kestrel's named pipe transport restricts access to the current user by default.
    public void Listen(KestrelServerOptions options, WorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        options.ListenNamedPipe(endpoint.Address);
    }

    public void Cleanup(WorkerEndpoint endpoint)
    {
    }

    private sealed class Allocator(string prefix) : IEndpointAllocator
    {
        // A per-run instance id keeps two Hosts on one machine apart and prevents squatting on a predictable pipe name.
        private readonly string _instanceId = Guid.NewGuid().ToString("N")[..12];

        public WorkerEndpoint Allocate(string moduleName)
        {
            ModuleName.Validate(moduleName);
            return new WorkerEndpoint(TransportName, $"{prefix}.{_instanceId}.{moduleName}");
        }

        public void Dispose()
        {
        }
    }
}
