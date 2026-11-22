using System.Net.Http;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPlatformSettings(PlatformServices.Name);
builder.Host.UseWindowsService(options => options.ServiceName = "ModularMonolith");

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<GridPattern>()));
builder.Services.AddSingleton(PlatformServices.CreateLauncher(builder.Configuration));
builder.Services.AddSingleton(PlatformServices.ResolveTransport(builder.Configuration));
builder.Services.AddSingleton<RequestDrainTracker>();
builder.Services.AddSingleton(WorkerSigningKey.Create());
builder.Services.AddSingleton<WorkerInternalContext>();
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddSingleton<IWorkerRoutingSource>(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddSingleton<IWorkerEndpointResolver>(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddWorkerHttpResilience();
builder.Services.AddSingleton<IWorkerClientFactory, WorkerClientFactory>();
builder.Services.AddHostModuleCustomization<ImageCacheHostCustomization>();
builder.Services.AddGeneratedWorkerModules();
builder.Services.AddSingleton<IHostModuleCatalog>(serviceProvider =>
    new HostModuleCatalog(HostModuleDescriptorComposer.Compose(
        serviceProvider.GetServices<HostModuleDescriptor>(),
        serviceProvider.GetServices<IHostModuleCustomization>())));
builder.Services.AddSingleton<IGridImageProvider, CachedGridImageProvider>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddReverseProxy();
builder.Services.AddSingleton<IForwarderHttpClientFactory, WorkerForwarderHttpClientFactory>();
builder.Services.RemoveAll<IProxyConfigProvider>();
builder.Services.AddSingleton<WorkerProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(serviceProvider => serviceProvider.GetRequiredService<WorkerProxyConfigProvider>());

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<HostInternalContextMiddleware>();
app.UseMiddleware<RequestDrainMiddleware>();
app.UseMiddleware<WorkerProxyFailureMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", platform = PlatformServices.Name, pid = Environment.ProcessId }))
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

app.MapReverseProxy();

PlatformServices.CreateProcessGuard().EnsureKillOnParentExit();

app.Run();
