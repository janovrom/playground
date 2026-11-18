using ModularMonolith.Abstractions;
using ModularMonolith.Platform;

namespace ModularMonolith.Host.Modules;

/// <summary>Host-side registration for one module: how to launch its worker and how to reach it.</summary>
internal interface IHostModule
{
    string Name { get; }

    string WorkerProject { get; }

    string ConfigSection { get; }

    JobLimits DefaultLimits { get; }

    /// <summary>Returns additional arguments for the worker process.</summary>
    IEnumerable<string> GetWorkerArguments(IConfiguration configuration);

    /// <summary>Registers Host services for the module.</summary>
    void ConfigureServices(IServiceCollection services);
}

internal static class HostModuleServiceCollectionExtensions
{
    /// <summary>Registers a Host module and its named worker client.</summary>
    public static IServiceCollection AddHostModule<TModule>(this IServiceCollection services)
        where TModule : class, IHostModule, new()
    {
        var module = new TModule();
        services.AddSingleton<IHostModule>(module);
        services.AddHttpClient(module.Name)
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://localhost"))
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                serviceProvider.GetRequiredService<WorkerTransportHandlerRegistry>().Create(module.Name));
        module.ConfigureServices(services);
        return services;
    }

    /// <summary>Registers a generated Host module descriptor and its transport client.</summary>
    public static IServiceCollection AddHostModuleDescriptor(
        this IServiceCollection services,
        HostModuleDescriptor descriptor,
        IHostModule? module = null)
    {
        if (module is not null)
        {
            services.AddSingleton<IHostModule>(module);
        }

        services.AddSingleton(descriptor);
        services.AddHttpContextAccessor();
        services.AddTransient<WorkerContextPropagationHandler>();
        services.AddHttpClient(descriptor.Name)
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://localhost"))
            .AddHttpMessageHandler<WorkerContextPropagationHandler>()
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                serviceProvider.GetRequiredService<WorkerTransportHandlerRegistry>().Create(descriptor.Name));
        descriptor.ConfigureServices(services);
        return services;
    }
}
