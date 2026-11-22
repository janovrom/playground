using ModularMonolith.Contracts;

namespace ModularMonolith.Host.Modules;

internal sealed class ImageCacheHostCustomization : IHostModuleCustomization
{
    /// <summary>Gets the module key for generated image-cache descriptors.</summary>
    public string ModuleName => ModuleCatalog.ImageCache;

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

}
