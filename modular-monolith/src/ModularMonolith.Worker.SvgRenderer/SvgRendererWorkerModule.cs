using ModularMonolith.Contracts;
using ModularMonolith.Worker.Hosting;

namespace ModularMonolith.Worker.SvgRenderer;

public sealed class SvgRendererWorkerModule : IWorkerModule
{
    public string Name => ModuleCatalog.SvgRenderer;

    public Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/render/svg", (GridSettings settings) =>
        {
            if (!IsValid(settings))
            {
                return Results.BadRequest();
            }

            return Results.Content(GridImageGenerator.RenderSvg(settings), "image/svg+xml");
        });

        app.MapPost("/render/page", (RenderPageRequest request) =>
        {
            if (!IsValid(request.Settings) || string.IsNullOrWhiteSpace(request.SvgContent))
            {
                return Results.BadRequest();
            }

            return Results.Content(ViewRenderer.RenderPage(request.Settings, request.SvgContent), "text/html");
        });
    }

    private static bool IsValid(GridSettings? settings)
        => settings is { Dimension: >= 2 and <= 20 } && Enum.IsDefined(settings.Pattern);
}
