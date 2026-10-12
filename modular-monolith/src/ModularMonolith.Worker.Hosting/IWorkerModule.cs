using Microsoft.AspNetCore.Builder;

namespace ModularMonolith.Worker.Hosting;

/// <summary>Everything a module contributes to its worker process: services and endpoints.</summary>
public interface IWorkerModule
{
    string Name { get; }

    Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken);

    void MapEndpoints(WebApplication app);
}
