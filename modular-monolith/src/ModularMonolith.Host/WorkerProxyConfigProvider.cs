using ModularMonolith.Host.Modules;
using Yarp.ReverseProxy.Configuration;

namespace ModularMonolith.Host;

internal sealed class WorkerProxyConfigProvider : IProxyConfigProvider
{
    private readonly IHostModuleCatalog _modules;
    private readonly ILogger<WorkerProxyConfigProvider> _logger;
    private readonly InMemoryConfigProvider _provider;

    public WorkerProxyConfigProvider(
        IHostModuleCatalog modules,
        IWorkerRoutingSource workerManager,
        ILogger<WorkerProxyConfigProvider> logger)
    {
        _modules = modules;
        _logger = logger;
        var initial = CreateConfig(workerManager.GetRoutingSnapshot());
        _provider = new InMemoryConfigProvider(initial.Routes, initial.Clusters, initial.RevisionId);
        _logger.LogDebug(
            "Loaded worker proxy revision {Revision} with {DestinationCount} ready destinations.",
            initial.RevisionId,
            initial.Clusters.Sum(cluster => cluster.Destinations?.Count ?? 0));
        workerManager.TopologyChanged += (_, snapshots) => Update(snapshots);
    }

    /// <summary>Returns the latest worker route and cluster snapshot.</summary>
    public IProxyConfig GetConfig() => _provider.GetConfig();

    private void Update(IReadOnlyList<WorkerRoutingSnapshot> snapshots)
    {
        var next = CreateConfig(snapshots);
        _provider.Update(next.Routes, next.Clusters, next.RevisionId);
        _logger.LogDebug(
            "Updated worker proxy revision {Revision} with {DestinationCount} ready destinations.",
            next.RevisionId,
            next.Clusters.Sum(cluster => cluster.Destinations?.Count ?? 0));
    }

    private ProxySnapshot CreateConfig(IReadOnlyList<WorkerRoutingSnapshot> snapshots)
    {
        var routeDescriptors = _modules.Modules
            .SelectMany(module => module.ProxyRoutes.Select(route => new { Module = module, Route = route }))
            .Select((item, index) => new RouteConfig
            {
                RouteId = $"{item.Module.Name}-{index}",
                ClusterId = item.Module.Name,
                Match = new RouteMatch { Path = item.Route.PublicPath },
                Transforms =
                [
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PathPattern"] = item.Route.DestinationPath
                    }
                ]
            })
            .ToArray();

        var clusters = _modules.Modules
            .Where(module => module.ProxyRoutes.Count > 0)
            .Select(module =>
            {
                var snapshot = snapshots.FirstOrDefault(item => item.Name == module.Name);
                return new ClusterConfig
                {
                    ClusterId = module.Name,
                    Destinations = snapshot?.IsReady == true && snapshot.Endpoint is not null
                        ? new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                        {
                            [module.Name] = new DestinationConfig
                            {
                                Address = "http://worker.local"
                            }
                        }
                        : new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                };
            })
            .ToArray();

        return new ProxySnapshot(routeDescriptors, clusters, Guid.NewGuid().ToString("N"));
    }

    private sealed record ProxySnapshot(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        string RevisionId)
    {
        public IReadOnlyList<RouteConfig> Routes { get; } = routes;
        public IReadOnlyList<ClusterConfig> Clusters { get; } = clusters;
    }
}
