using Microsoft.Data.Sqlite;
using ModularMonolith.Worker.Hosting;
using ModularMonolith.Worker.ImageCache;

const int maxContentLength = 1024 * 1024;
const string svgContentType = "image/svg+xml";
var builder = WebApplication.CreateBuilder(args);
WorkerHost.Configure(builder);

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
await store.InitializeAsync(CancellationToken.None);
builder.Services.AddSingleton<IImageStore>(store);
builder.Services.AddHostedService<ImageStoreShutdownService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
WorkerHost.MapHealth(app);

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

    if (request.ContentLength > maxContentLength)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var contentType = request.ContentType;
    if (!string.Equals(contentType, svgContentType, StringComparison.OrdinalIgnoreCase))
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    await using var buffer = new MemoryStream();
    var chunk = new byte[81920];
    int read;
    while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
    {
        if (buffer.Length + read > maxContentLength)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
    }

    await imageStore.PutAsync(key, svgContentType, buffer.ToArray(), cancellationToken);
    return Results.NoContent();
});

app.Run();

internal sealed class ImageStoreShutdownService(IImageStore store, ILogger<ImageStoreShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.CheckpointAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to checkpoint the image cache database during shutdown.");
            throw;
        }
    }
}
