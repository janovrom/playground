using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Host;

internal interface IWorkerEndpointResolver
{
    WorkerEndpoint GetEndpoint(string name);
}

internal interface IWorkerClientFactory
{
    HttpClient Create(string moduleName);
}

internal sealed class WorkerClientFactory(IWorkerTransport transport, IWorkerEndpointResolver endpoints) : IWorkerClientFactory
{
    public HttpClient Create(string moduleName) => transport.CreateHttpClient(endpoints.GetEndpoint(moduleName));
}
