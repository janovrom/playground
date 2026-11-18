using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host.Modules;

internal sealed class ImageCacheHostModule : IHostModule
{
    /// <summary>Gets the stable worker module name.</summary>
    public string Name => ModuleCatalog.ImageCache;

    /// <summary>Gets the worker project name used to locate its executable.</summary>
    public string WorkerProject => "ModularMonolith.Worker.ImageCache";

    /// <summary>Gets the configuration section used for client options.</summary>
    public string ConfigSection => "ImageCache";

    /// <summary>Gets the default resource limits for the worker.</summary>
    public JobLimits DefaultLimits => new();

    /// <summary>Builds the database path argument passed to the worker.</summary>
    public IEnumerable<string> GetWorkerArguments(IConfiguration configuration)
    {
        var databasePath = configuration["ImageCache:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "modular-monolith",
                "image-cache.db");
        }

        yield return $"--ImageCache:DatabasePath={databasePath}";
    }

    /// <summary>Leaves cache DI registration to the generated client registration.</summary>
    public void ConfigureServices(IServiceCollection services) { }
}
