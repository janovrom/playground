using ModularMonolith.Contracts;

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
