using ModularMonolith.Contracts;
using ModularMonolith.Worker.Hosting;

var builder = WebApplication.CreateBuilder(args);
WorkerHost.Configure(builder);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
WorkerHost.MapHealth(app);

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

app.Run();

static bool IsValid(GridSettings? settings)
    => settings is { Dimension: >= 2 and <= 20 } && Enum.IsDefined(settings.Pattern);

public static class GridImageGenerator
{
    public static string RenderSvg(GridSettings settings)
    {
        const int canvasSize = 300;
        var tileSize = canvasSize / settings.Dimension;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvasSize}\" height=\"{canvasSize}\" viewBox=\"0 0 {canvasSize} {canvasSize}\">");

        for (int row = 0; row < settings.Dimension; row++)
        {
            for (int col = 0; col < settings.Dimension; col++)
            {
                var color = GetTileColor(row, col, settings);
                var x = col * tileSize;
                var y = row * tileSize;

                sb.AppendLine($"  <rect x=\"{x}\" y=\"{y}\" width=\"{tileSize}\" height=\"{tileSize}\" fill=\"{color}\" stroke=\"#0f172a\" stroke-width=\"1\"/>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string GetTileColor(int row, int col, GridSettings settings) => settings.Pattern switch
    {
        GridPattern.Checkerboard => (row + col) % 2 == 0 ? "#6366f1" : "#1e293b",
        GridPattern.Radial => (Math.Abs(row - settings.Dimension / 2) + Math.Abs(col - settings.Dimension / 2)) % 2 == 0 ? "#10b981" : "#0f172a",
        _ => "#ffffff"
    };
}

public static class ViewRenderer
{
    public static string RenderPage(GridSettings settings, string svgContent) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Functional Grid Generator</title>
            <style>
                body { font-family: system-ui, sans-serif; background: #0f172a; color: #f8fafc; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; }
                .card { background: #1e293b; padding: 2rem; border-radius: 1rem; box-shadow: 0 20px 25px -5px rgba(0,0,0,0.5); text-align: center; max-width: 400px; }
                .grid-container { margin: 1.5rem 0; border-radius: 0.5rem; overflow: hidden; display: inline-block; border: 2px solid #334155; }
                .actions { display: flex; gap: 0.75rem; justify-content: center; }
                .btn { padding: 0.75rem 1.25rem; font-weight: 600; border-radius: 0.5rem; text-decoration: none; color: #ffffff; transition: opacity 0.2s; }
                .btn:hover { opacity: 0.85; }
                .btn-indigo { background: #6366f1; }
                .btn-emerald { background: #10b981; }
            </style>
        </head>
        <body>
            <div class="card">
                <h2>Pattern: {{settings.Pattern}}</h2>
                <p>Grid Dimensions: {{settings.Dimension}}x{{settings.Dimension}}</p>

                <div class="grid-container">
                    {{svgContent}}
                </div>

                <div class="actions">
                    <a href="/?pattern=Checkerboard&size={{settings.Dimension}}" class="btn btn-indigo">Checkerboard</a>
                    <a href="/?pattern=Radial&size={{settings.Dimension}}" class="btn btn-emerald">Radial Pattern</a>
                </div>
            </div>
        </body>
        </html>
        """;
}
