using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host.Modules;

internal sealed class SvgRendererHostModule : IHostModule
{
    public string Name => ModuleCatalog.SvgRenderer;

    public string WorkerProject => "ModularMonolith.Worker.SvgRenderer";

    public string ConfigSection => "SvgRenderer";

    public JobLimits DefaultLimits => new();

    public IEnumerable<string> GetWorkerArguments(IConfiguration configuration) => [];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
            new RendererClient(serviceProvider.GetRequiredService<IWorkerClientFactory>().Create(Name)));
        services.AddSingleton<IRendererClient>(serviceProvider => serviceProvider.GetRequiredService<RendererClient>());
    }
}
