using Microsoft.Data.Sqlite;
using ModularMonolith.Contracts;
using ModularMonolith.Worker.Hosting;

namespace ModularMonolith.Worker.ImageCache;

public sealed class ImageCacheWorkerModule : IWorkerModule
{
    private const int MaxContentLength = 1024 * 1024;
    private const string SvgContentType = "image/svg+xml";

    public string Name => ModuleCatalog.ImageCache;

    public async Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
    {
        var databasePath = builder.Configuration["ImageCache:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "modular-monolith",
                "image-cache.db");
        }

        databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        var store = new ImageStore(connectionString);
        await store.InitializeAsync(cancellationToken);
        builder.Services.AddSingleton<IImageStore>(store);
        builder.Services.AddHostedService<ImageStoreShutdownService>();
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/images/{key}", async (string key, IImageStore imageStore, CancellationToken cancellationToken) =>
        {
            if (!ImageCacheKey.IsValid(key))
            {
                return Results.BadRequest();
            }

            var image = await imageStore.GetAsync(key, cancellationToken);
            return image is null
                ? Results.NotFound()
                : Results.File(image.Content, image.ContentType);
        });

        app.MapPut("/images/{key}", async (string key, HttpRequest request, IImageStore imageStore, CancellationToken cancellationToken) =>
        {
            if (!ImageCacheKey.IsValid(key))
            {
                return Results.BadRequest();
            }

            if (request.ContentLength > MaxContentLength)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var contentType = request.ContentType;
            if (!string.Equals(contentType, SvgContentType, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            await using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxContentLength)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            await imageStore.PutAsync(key, SvgContentType, buffer.ToArray(), cancellationToken);
            return Results.NoContent();
        });
    }
}
