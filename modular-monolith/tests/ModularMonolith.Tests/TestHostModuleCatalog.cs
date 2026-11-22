using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;

namespace ModularMonolith.Tests;

internal static class TestHostModuleCatalog
{
    public static HostModuleCatalog Create(params string[] moduleNames)
    {
        var services = new ServiceCollection();
        services.AddGeneratedWorkerModules();
        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetServices<HostModuleDescriptor>();
        return new HostModuleCatalog(moduleNames.Length == 0
            ? descriptors
            : descriptors.Where(descriptor => moduleNames.Contains(descriptor.Name, StringComparer.Ordinal)));
    }
}
