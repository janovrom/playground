using ModularMonolith.Contracts;

namespace ModularMonolith.Worker.SvgRenderer;

/// <summary>Renders discrete grid patterns as SVG.</summary>
public static class GridImageGenerator
{
    /// <summary>Renders a grid with checkerboard or concentric radial bands.</summary>
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

    private static string GetTileColor(int row, int col, GridSettings settings)
    {
        if (settings.Pattern == GridPattern.Checkerboard)
        {
            return (row + col) % 2 == 0 ? "#6366f1" : "#1e293b";
        }

        if (settings.Pattern != GridPattern.Radial)
        {
            return "#ffffff";
        }

        var center = (settings.Dimension - 1) / 2d;
        var rowOffset = row - center;
        var colOffset = col - center;
        var ring = (int)Math.Ceiling(Math.Sqrt(rowOffset * rowOffset + colOffset * colOffset));
        return ring % 2 == 0 ? "#10b981" : "#0f172a";
    }
}