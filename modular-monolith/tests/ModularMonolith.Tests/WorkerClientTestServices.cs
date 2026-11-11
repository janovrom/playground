using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Tests;

internal sealed class WorkerClientTestServices : IDisposable
{
    private readonly ServiceProvider _provider;

    public WorkerClientTestServices(WorkerManager manager, IWorkerTransport transport, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWorkerTransport>(transport);
        services.AddSingleton<IWorkerEndpointResolver>(manager);
        services.AddWorkerHttpResilience();
        services.AddSingleton<IWorkerClientFactory, WorkerClientFactory>();
        services.AddHostModule<SvgRendererHostModule>();
        services.AddHostModule<ImageCacheHostModule>();
        _provider = services.BuildServiceProvider();
    }

    public T GetRequiredService<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose() => _provider.Dispose();
}
