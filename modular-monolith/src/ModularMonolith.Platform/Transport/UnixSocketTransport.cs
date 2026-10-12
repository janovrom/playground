using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Platform.Transport;

public sealed class UnixSocketTransport : IWorkerTransport
{
    public const string TransportName = "UnixSocket";

    public string Name => TransportName;

    public IEndpointAllocator CreateAllocator(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new Allocator(configuration["Workers:SocketDirectory"]);
    }

    public HttpMessageHandler CreateClientHandler(WorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    public void Listen(KestrelServerOptions options, WorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);

        var socketPath = Path.GetFullPath(endpoint.Address);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        File.Delete(socketPath);
        options.ListenUnixSocket(socketPath);
    }

    public void Cleanup(WorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        File.Delete(endpoint.Address);
    }

    private sealed class Allocator : IEndpointAllocator
    {
        private readonly string _directory;
        private string? _temporaryDirectory;

        public Allocator(string? configuredDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredDirectory))
            {
                _temporaryDirectory = Directory.CreateTempSubdirectory("modular-monolith-").FullName;
                RestrictToCurrentUser(_temporaryDirectory);
                _directory = _temporaryDirectory;
                return;
            }

            _directory = Path.GetFullPath(configuredDirectory);
            Directory.CreateDirectory(_directory);
            RestrictToCurrentUser(_directory);
        }

        public WorkerEndpoint Allocate(string moduleName)
        {
            ModuleName.Validate(moduleName);
            return new WorkerEndpoint(TransportName, Path.Combine(_directory, $"{moduleName}.sock"));
        }

        public void Dispose()
        {
            if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }

            _temporaryDirectory = null;
        }

        private static void RestrictToCurrentUser(string directory)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
