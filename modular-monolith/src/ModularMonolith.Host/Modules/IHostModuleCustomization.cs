using ModularMonolith.Abstractions;
using ModularMonolith.Platform;

namespace ModularMonolith.Host.Modules;

/// <summary>Customizes generated Host behavior for one worker module.</summary>
internal interface IHostModuleCustomization
{
    /// <summary>Gets the default resource limits when configuration does not override them.</summary>
    JobLimits DefaultLimits => new();

    /// <summary>Returns additional arguments for the worker process.</summary>
    IEnumerable<string> GetWorkerArguments(IConfiguration configuration);

    /// <summary>Registers Host services for the module.</summary>
    void ConfigureServices(IServiceCollection services) { }
}

internal static class HostModuleServiceCollectionExtensions
{
    /// <summary>Registers a generated Host module descriptor and its transport client.</summary>
    public static IServiceCollection AddHostModuleDescriptor(
        this IServiceCollection services,
        HostModuleDescriptor descriptor)
    {
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
