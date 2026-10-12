using ModularMonolith.Abstractions;

namespace ModularMonolith.Host.Modules;

/// <summary>Host-side registration for one module: how to launch its worker and how to reach it.</summary>
internal interface IHostModule
{
    string Name { get; }

    string WorkerProject { get; }

    string ConfigSection { get; }

    JobLimits DefaultLimits { get; }

    IEnumerable<string> GetWorkerArguments(IConfiguration configuration);

    void ConfigureServices(IServiceCollection services);
}

internal static class HostModuleServiceCollectionExtensions
{
    public static IServiceCollection AddHostModule<TModule>(this IServiceCollection services)
        where TModule : class, IHostModule, new()
    {
        var module = new TModule();
        services.AddSingleton<IHostModule>(module);
        module.ConfigureServices(services);
        return services;
    }
}
