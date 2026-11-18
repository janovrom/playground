using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host.Modules;

internal sealed class SvgRendererHostModule : IHostModule
{
    /// <summary>Gets the stable worker module name.</summary>
    public string Name => ModuleCatalog.SvgRenderer;

    /// <summary>Gets the worker project name used to locate its executable.</summary>
    public string WorkerProject => "ModularMonolith.Worker.SvgRenderer";

    /// <summary>Gets the configuration section used for client options.</summary>
    public string ConfigSection => "SvgRenderer";

    /// <summary>Gets the default resource limits for the worker.</summary>
    public JobLimits DefaultLimits => new();

    /// <summary>Returns no additional arguments for the renderer worker.</summary>
    public IEnumerable<string> GetWorkerArguments(IConfiguration configuration) => [];

    /// <summary>Leaves renderer DI registration to the generated client registration.</summary>
    public void ConfigureServices(IServiceCollection services) { }
}
