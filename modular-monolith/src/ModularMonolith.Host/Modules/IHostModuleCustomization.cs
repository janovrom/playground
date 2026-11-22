using ModularMonolith.Abstractions;
using ModularMonolith.Platform;

namespace ModularMonolith.Host.Modules;

/// <summary>Customizes generated Host behavior for one worker module.</summary>
internal interface IHostModuleCustomization
{
    /// <summary>Gets the generated module name that this customization extends.</summary>
    string ModuleName { get; }

    /// <summary>Gets the default resource limits when configuration does not override them.</summary>
    JobLimits CustomizeLimits(JobLimits defaults) => defaults;

    /// <summary>Returns additional arguments for the worker process.</summary>
    IEnumerable<string> GetWorkerArguments(IConfiguration configuration);

    /// <summary>Registers Host services for the module.</summary>
    void ConfigureServices(IServiceCollection services) { }
}

internal static class HostModuleServiceCollectionExtensions
{
    /// <summary>Registers and configures one Host customization without involving generated code.</summary>
    public static IServiceCollection AddHostModuleCustomization<TCustomization>(
        this IServiceCollection services)
        where TCustomization : class, IHostModuleCustomization, new()
    {
        var customization = new TCustomization();
        services.AddSingleton<IHostModuleCustomization>(customization);
        customization.ConfigureServices(services);
        return services;
    }

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
        return services;
    }
}
