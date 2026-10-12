using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Platform;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Worker.Hosting;

public static class WorkerHost
{
    private const string ShutdownEventKey = "Worker:ShutdownEvent";

    public static async Task RunAsync(string[] args, IWorkerModule module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(module);

        var builder = WebApplication.CreateBuilder(args);
        Configure(builder);
        await module.ConfigureServicesAsync(builder, cancellationToken);

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        module.MapEndpoints(app);

        await app.RunAsync(cancellationToken);
    }

    private static void Configure(WebApplicationBuilder builder)
    {
        var address = builder.Configuration["Worker:Endpoint"];
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Worker:Endpoint must be configured.");
        }

        var transport = WorkerTransports.Resolve(builder.Configuration["Worker:Transport"]);
        var endpoint = new WorkerEndpoint(transport.Name, address);

        builder.WebHost.ConfigureKestrel(options => transport.Listen(options, endpoint));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(8));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        PlatformServices.CreateProcessGuard().EnsureKillOnParentExit();

        builder.Services.AddSingleton(new WorkerEndpointCleanup(transport, endpoint));
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WorkerEndpointCleanup>());

        var shutdownEvent = builder.Configuration[ShutdownEventKey];
        if (!string.IsNullOrWhiteSpace(shutdownEvent))
        {
            builder.Services.AddHostedService(serviceProvider => new ShutdownEventListener(
                shutdownEvent,
                serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                serviceProvider.GetRequiredService<ILogger<ShutdownEventListener>>()));
        }
    }

    private sealed class WorkerEndpointCleanup(IWorkerTransport transport, WorkerEndpoint endpoint) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            transport.Cleanup(endpoint);
            return Task.CompletedTask;
        }
    }
}
