using System.Net.Http;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Linux;

var builder = WebApplication.CreateBuilder(args);
var cgroupRoot = builder.Configuration["Cgroup:Root"];
var cgroupEnabled = builder.Configuration.GetValue("Cgroup:Enabled", true);
IWorkerLauncher workerLauncher = cgroupEnabled
    ? new CgroupWorkerLauncher(cgroupRoot)
    : new LocalWorkerLauncher();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IWorkerLauncher>(workerLauncher);
builder.Services.AddSingleton<RequestDrainTracker>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddSingleton(serviceProvider =>
{
    var manager = serviceProvider.GetRequiredService<WorkerManager>();
    return new RendererClient(UdsHttpClientFactory.Create(manager.GetSocketPath(ModuleCatalog.SvgRenderer)));
});
builder.Services.AddSingleton<IRendererClient>(serviceProvider => serviceProvider.GetRequiredService<RendererClient>());
builder.Services.AddSingleton(serviceProvider =>
{
    var manager = serviceProvider.GetRequiredService<WorkerManager>();
    return new ImageCacheClient(UdsHttpClientFactory.Create(manager.GetSocketPath(ModuleCatalog.ImageCache)));
});
builder.Services.AddSingleton<IImageCacheClient>(serviceProvider => serviceProvider.GetRequiredService<ImageCacheClient>());
builder.Services.AddSingleton<IGridImageProvider, CachedGridImageProvider>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<RequestDrainMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", platform = "linux", pid = Environment.ProcessId }))
    .WithSummary("Check the Host health");

app.MapGet("/modules", (WorkerManager workerManager) => Results.Ok(workerManager.GetStatus()))
    .WithSummary("Get worker process status");

app.MapGet("/", async (string? pattern, int? size, IGridImageProvider provider, HttpContext context, CancellationToken cancellationToken) =>
{
    var settings = GridRequestParser.Parse(pattern, size);

    try
    {
        var image = await provider.GetSvgAsync(settings, cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Cache"] = image.CacheHit ? "HIT" : "MISS";
        var page = await provider.RenderPageAsync(settings, image.Svg, cancellationToken);
        return Results.Content(page, "text/html");
    }
    catch (HttpRequestException)
    {
        return Results.Problem("The rendering service is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("The rendering service timed out.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithSummary("Display a generated grid pattern")
.WithDescription("Uses the image cache before asking the SVG renderer to generate the requested grid.");

app.MapGet("/grid.svg", async (string? pattern, int? size, IGridImageProvider provider, HttpContext context, CancellationToken cancellationToken) =>
{
    var settings = GridRequestParser.Parse(pattern, size);

    try
    {
        var image = await provider.GetSvgAsync(settings, cancellationToken);
        context.Response.Headers["X-Cache"] = image.CacheHit ? "HIT" : "MISS";
        return Results.Content(image.Svg, "image/svg+xml");
    }
    catch (HttpRequestException)
    {
        return Results.Problem("The rendering service is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("The rendering service timed out.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithSummary("Get the grid pattern as SVG");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

LinuxProcessGuard.EnsureKillOnParentExit();

app.Run();
