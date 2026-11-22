using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModularMonolith.Abstractions;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace ModularMonolith.Tests;

public sealed class GatewayStreamingTests
{
    [LinuxFact]
    public Task UnixSocket_ReverseProxy_ForwardsFirstResponseChunkBeforeWorkerCompletes()
        => ReverseProxy_ForwardsFirstResponseChunkBeforeWorkerCompletes(new UnixSocketTransport());

    [WindowsFact]
    public Task NamedPipe_ReverseProxy_ForwardsFirstResponseChunkBeforeWorkerCompletes()
        => ReverseProxy_ForwardsFirstResponseChunkBeforeWorkerCompletes(new NamedPipeTransport());

    private static async Task ReverseProxy_ForwardsFirstResponseChunkBeforeWorkerCompletes(
        IWorkerTransport transport)
    {
        var directory = Directory.CreateTempSubdirectory("gateway-streaming-");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedTraceParent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allocator = transport.CreateAllocator(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workers:SocketDirectory"] = directory.FullName
            })
            .Build());
        var endpoint = allocator.Allocate("streamer");

        var workerBuilder = WebApplication.CreateBuilder();
        workerBuilder.Logging.ClearProviders();
        workerBuilder.WebHost.ConfigureKestrel(options => transport.Listen(options, endpoint));
        await using var worker = workerBuilder.Build();
        worker.MapGet("/stream/{**path}", async (HttpContext context) =>
        {
            observedTraceParent.TrySetResult(context.Request.Headers.TraceParent.ToString());
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("first"));
            await context.Response.Body.FlushAsync();
            firstChunkWritten.TrySetResult();
            await release.Task;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("second"));
        });

        var resolver = new RoutingSource(endpoint);
        var catalog = new HostModuleCatalog([
            new HostModuleDescriptor(
                "streamer",
                "streamer",
                "Streamer",
                new JobLimits(),
                [new WorkerProxyRouteDescriptor("/stream/{**path}", "/stream/{**path}")],
                _ => [])
        ]);
        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.Logging.ClearProviders();
        gatewayBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        gatewayBuilder.Services.AddSingleton<IHostModuleCatalog>(catalog);
        gatewayBuilder.Services.AddSingleton<IWorkerRoutingSource>(resolver);
        gatewayBuilder.Services.AddSingleton<IWorkerEndpointResolver>(resolver);
        gatewayBuilder.Services.AddSingleton<IWorkerTransport>(transport);
        gatewayBuilder.Services.AddReverseProxy();
        gatewayBuilder.Services.RemoveAll<IProxyConfigProvider>();
        gatewayBuilder.Services.AddSingleton<IProxyConfigProvider, WorkerProxyConfigProvider>();
        gatewayBuilder.Services.AddSingleton<IForwarderHttpClientFactory, WorkerForwarderHttpClientFactory>();
        await using var gateway = gatewayBuilder.Build();
        gateway.MapReverseProxy();

        try
        {
            await worker.StartAsync();
            await gateway.StartAsync();
            var address = gateway.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/stream/test");
            var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
            request.Headers.TryAddWithoutValidation("traceparent", traceParent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await firstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var body = await response.Content.ReadAsStreamAsync();
            var first = new byte[5];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var count = await body.ReadAsync(first, timeout.Token);

            Assert.Equal(5, count);
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            Assert.False(release.Task.IsCompleted);
            Assert.StartsWith(
                "00-0af7651916cd43dd8448eb211c80319c-",
                await observedTraceParent.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            release.TrySetResult();
            Assert.Equal("second", await new StreamReader(body).ReadToEndAsync());
        }
        finally
        {
            release.TrySetResult();
            await gateway.StopAsync();
            await worker.StopAsync();
            transport.Cleanup(endpoint);
            directory.Delete(recursive: true);
        }
    }

    private sealed class RoutingSource(WorkerEndpoint endpoint) : IWorkerRoutingSource, IWorkerEndpointResolver
    {
        public event EventHandler<IReadOnlyList<WorkerRoutingSnapshot>>? TopologyChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<WorkerRoutingSnapshot> GetRoutingSnapshot()
            => [new("streamer", endpoint, true, "ready")];

        public WorkerEndpoint GetEndpoint(string name)
        {
            Assert.Equal("streamer", name);
            return endpoint;
        }

        public bool IsRunning(string name) => true;
    }
}
