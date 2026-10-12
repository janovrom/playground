using ModularMonolith.Contracts;

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
