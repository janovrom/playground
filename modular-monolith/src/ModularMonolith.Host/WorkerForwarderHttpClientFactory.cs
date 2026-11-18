using Microsoft.Extensions.Logging;
using ModularMonolith.Platform.Transport;
using Yarp.ReverseProxy.Forwarder;

namespace ModularMonolith.Host;

internal sealed class WorkerForwarderHttpClientFactory(
    ILogger<ForwarderHttpClientFactory> logger,
    IWorkerTransport transport,
    IWorkerEndpointResolver endpoints) : ForwarderHttpClientFactory(logger)
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        handler.ConnectCallback = async (connectContext, cancellationToken) =>
        {
            var transportHandler = (SocketsHttpHandler)transport.CreateClientHandler(endpoints.GetEndpoint(context.ClusterId));
            return await transportHandler.ConnectCallback!(connectContext, cancellationToken);
        };
    }
}
