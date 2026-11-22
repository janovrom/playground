using ModularMonolith.Contracts;
using ModularMonolith.Worker.Hosting;

namespace ModularMonolith.Worker.SvgRenderer;

/// <summary>Provides SVG and HTML rendering endpoints for the renderer worker.</summary>
public sealed class SvgRendererWorkerModule : IWorkerModule
{
    /// <summary>Gets the stable renderer worker name.</summary>
    public string Name => ModuleCatalog.SvgRenderer;

    /// <summary>Completes renderer service configuration.</summary>
    public Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Maps rendering endpoints for the worker.</summary>
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/render/svg", (string? pattern, int? size) =>
            RenderSvg(GridRequestParser.Parse(pattern, size)));

        app.MapPost("/render/svg", (GridSettings settings) => RenderSvg(settings));

        app.MapPost("/render/page", (RenderPageRequest request) =>
        {
            if (!IsValid(request.Settings) || string.IsNullOrWhiteSpace(request.SvgContent))
            {
                return Results.BadRequest();
            }

            return Results.Content(ViewRenderer.RenderPage(request.Settings, request.SvgContent), "text/html");
        });
    }

private static IResult RenderSvg(GridSettings? settings)
    {
        if (settings is null || !IsValid(settings))
        {
            return Results.BadRequest();
        }

        return Results.Content(GridImageGenerator.RenderSvg(settings), "image/svg+xml");
    }

    private static bool IsValid(GridSettings? settings)
        => settings is { Dimension: >= 2 and <= 20 } && Enum.IsDefined(settings.Pattern);
}
