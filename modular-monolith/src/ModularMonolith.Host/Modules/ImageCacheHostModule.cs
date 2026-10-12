using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host.Modules;

internal sealed class ImageCacheHostModule : IHostModule
{
    public string Name => ModuleCatalog.ImageCache;

    public string WorkerProject => "ModularMonolith.Worker.ImageCache";

    public string ConfigSection => "ImageCache";

    public JobLimits DefaultLimits => new();

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

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
            new ImageCacheClient(serviceProvider.GetRequiredService<IWorkerClientFactory>().Create(Name)));
        services.AddSingleton<IImageCacheClient>(serviceProvider => serviceProvider.GetRequiredService<ImageCacheClient>());
    }
}
