using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Contracts;
using ModularMonolith.Platform;
using ModularMonolith.Platform.Transport;
using System.Security.Claims;

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
        services.AddGeneratedWorkerModules();
        var codec = new WorkerInternalContext(manager.SigningKey);
        services.AddHttpClient(ModuleCatalog.SvgRenderer)
            .AddHttpMessageHandler(() => new FixedWorkerContextHandler(codec));
        services.AddHttpClient(ModuleCatalog.ImageCache)
            .AddHttpMessageHandler(() => new FixedWorkerContextHandler(codec));
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        var httpContext = _provider.GetRequiredService<IHttpContextAccessor>().HttpContext!;
        httpContext.Request.Headers[WorkerInternalContextConstants.HeaderName] =
            codec.Encode(new ClaimsPrincipal(new ClaimsIdentity()), []);
    }

    public T GetRequiredService<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose() => _provider.Dispose();

    private sealed class FixedWorkerContextHandler(WorkerInternalContext codec) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Remove(WorkerInternalContextConstants.HeaderName);
            request.Headers.TryAddWithoutValidation(
                WorkerInternalContextConstants.HeaderName,
                codec.Encode(new ClaimsPrincipal(new ClaimsIdentity()), []));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
