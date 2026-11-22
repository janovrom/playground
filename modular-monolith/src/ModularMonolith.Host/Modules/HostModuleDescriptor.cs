using ModularMonolith.Abstractions;

namespace ModularMonolith.Host.Modules;

internal sealed record WorkerProxyRouteDescriptor(string PublicPath, string DestinationPath);

internal sealed record HostModuleDescriptor(
    string Name,
    string WorkerProject,
    string ConfigSection,
    JobLimits DefaultLimits,
    IReadOnlyList<WorkerProxyRouteDescriptor> ProxyRoutes,
    Func<IConfiguration, IEnumerable<string>> GetWorkerArguments);

internal static class HostModuleDescriptorComposer
{
    /// <summary>Applies registered Host customizations to generated descriptors.</summary>
    public static IReadOnlyList<HostModuleDescriptor> Compose(
        IEnumerable<HostModuleDescriptor> descriptors,
        IEnumerable<IHostModuleCustomization> customizations)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(customizations);
        var moduleMap = HostModuleDescriptorValidator.Validate(descriptors)
            .ToDictionary(descriptor => descriptor.Name, StringComparer.Ordinal);
        var modules = customizations.ToArray();
        var duplicate = modules.GroupBy(customization => customization.ModuleName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Host customization for module '{duplicate.Key}' is registered more than once.");
        }

        foreach (var customization in modules)
        {
            if (!moduleMap.TryGetValue(customization.ModuleName, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"Host customization refers to unknown module '{customization.ModuleName}'.");
            }

            moduleMap[customization.ModuleName] = descriptor with
            {
                DefaultLimits = customization.CustomizeLimits(descriptor.DefaultLimits),
                GetWorkerArguments = customization.GetWorkerArguments
            };
        }

        return moduleMap.Values.ToArray();
    }
}

internal interface IHostModuleCatalog
{
    IReadOnlyList<HostModuleDescriptor> Modules { get; }
}

internal sealed class HostModuleCatalog : IHostModuleCatalog
{
    /// <summary>Creates a catalog from explicit runtime descriptors.</summary>
    public HostModuleCatalog(IEnumerable<HostModuleDescriptor> modules)
    {
        Modules = HostModuleDescriptorValidator.Validate(modules);
    }

    public IReadOnlyList<HostModuleDescriptor> Modules { get; }
}

internal static class HostModuleDescriptorValidator
{
    /// <summary>Validates module identity and all proxy route conflicts.</summary>
    public static IReadOnlyList<HostModuleDescriptor> Validate(IEnumerable<HostModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var validated = modules.ToArray();
        var duplicateModule = validated
            .GroupBy(module => module.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateModule is not null)
        {
            throw new InvalidOperationException($"Module '{duplicateModule.Key}' is registered more than once.");
        }

        var routes = validated.SelectMany(module => module.ProxyRoutes.Select(route => (Module: module, Route: route))).ToArray();
        ValidateRoutes(routes);
        return validated;
    }

    private static void ValidateRoutes(IReadOnlyList<(HostModuleDescriptor Module, WorkerProxyRouteDescriptor Route)> routes)
    {
        var reserved = new HashSet<string>(["/health", "/modules", "/", "/grid.svg"], StringComparer.OrdinalIgnoreCase);
        foreach (var item in routes)
        {
            if (!item.Route.PublicPath.StartsWith("/", StringComparison.Ordinal)
                || !item.Route.DestinationPath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Proxy route for module '{item.Module.Name}' must use absolute public and destination paths.");
            }

            var publicRoot = item.Route.PublicPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (publicRoot is null
                || reserved.Any(path => string.Equals($"/{publicRoot}", path, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Proxy route '{item.Route.PublicPath}' for module '{item.Module.Name}' conflicts with a Host route.");
            }
        }

        foreach (var duplicate in routes
                     .GroupBy(item => item.Route.PublicPath, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            throw new InvalidOperationException($"Proxy route '{duplicate.Key}' is registered more than once.");
        }

        for (var leftIndex = 0; leftIndex < routes.Count; leftIndex++)
        {
            var left = routes[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < routes.Count; rightIndex++)
            {
                var right = routes[rightIndex];
                if (RoutePatternsOverlap(left.Route.PublicPath, right.Route.PublicPath))
                {
                    throw new InvalidOperationException(
                        $"Proxy routes '{left.Route.PublicPath}' and '{right.Route.PublicPath}' overlap.");
                }
            }
        }
    }

    private static bool RoutePatternsOverlap(string left, string right)
    {
        var leftSegments = Segments(left);
        var rightSegments = Segments(right);
        var sharedLength = Math.Min(leftSegments.Length, rightSegments.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            if (leftSegments[index].StartsWith("{**", StringComparison.Ordinal)
                || rightSegments[index].StartsWith("{**", StringComparison.Ordinal))
            {
                return true;
            }

            var leftParameter = leftSegments[index].StartsWith("{", StringComparison.Ordinal);
            var rightParameter = rightSegments[index].StartsWith("{", StringComparison.Ordinal);
            if (!leftParameter && !rightParameter
                && !leftSegments[index].Equals(rightSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return leftSegments.Length == rightSegments.Length
            || leftSegments.Length > sharedLength && leftSegments[sharedLength].StartsWith("{**", StringComparison.Ordinal)
            || rightSegments.Length > sharedLength && rightSegments[sharedLength].StartsWith("{**", StringComparison.Ordinal);
    }

    private static string[] Segments(string pattern)
        => pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
