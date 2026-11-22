using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;

namespace ModularMonolith.Tests;

internal static class TestHostModuleCatalog
{
    public static HostModuleCatalog Create(params string[] moduleNames)
    {
        var services = new ServiceCollection();
        services.AddHostModuleCustomization<ImageCacheHostCustomization>();
        services.AddGeneratedWorkerModules();
        using var provider = services.BuildServiceProvider();
        var descriptors = HostModuleDescriptorComposer.Compose(
            provider.GetServices<HostModuleDescriptor>(),
            provider.GetServices<IHostModuleCustomization>());
        return new HostModuleCatalog(moduleNames.Length == 0
            ? descriptors
            : descriptors.Where(descriptor => moduleNames.Contains(descriptor.Name, StringComparer.Ordinal)));
    }
}
