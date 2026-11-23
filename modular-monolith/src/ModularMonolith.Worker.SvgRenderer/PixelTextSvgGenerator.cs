using System.Text;

namespace ModularMonolith.Worker.SvgRenderer;

/// <summary>Renders supported text as crisp bitmap-style SVG.</summary>
public static class PixelTextSvgGenerator
{
    /// <summary>Gets the maximum number of input characters.</summary>
    public const int MaxCharacters = 20;

    /// <summary>Gets the fixed SVG text height.</summary>
    public const int TextHeight = 70;

    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int CellSize = 10;
    private const int CharacterGap = 1;
    private const string PixelColor = "#6366f1";

    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['B'] = ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
        ['C'] = ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
        ['G'] = ["01111", "10000", "10000", "10111", "10001", "10001", "01111"],
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['I'] = ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
        ['J'] = ["00111", "00010", "00010", "00010", "10010", "10010", "01100"],
        ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['W'] = ["10001", "10001", "10001", "10101", "10101", "11011", "10001"],
        ['X'] = ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['Z'] = ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ['6'] = ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
        [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"]
    };

    /// <summary>Renders valid text as an SVG made from square pixels.</summary>
    /// <exception cref="ArgumentException">The text is empty, too long, or contains an unsupported character.</exception>
    public static string RenderSvg(string text)
    {
        if (!TryNormalize(text, out var normalizedText))
        {
            throw new ArgumentException(
                $"Text must contain 1 to {MaxCharacters} characters from A-Z, 0-9, and space.",
                nameof(text));
        }

        var width = ((GlyphWidth + CharacterGap) * normalizedText.Length - CharacterGap) * CellSize;
        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{TextHeight}\" viewBox=\"0 0 {width} {TextHeight}\" shape-rendering=\"crispEdges\">");

        for (var characterIndex = 0; characterIndex < normalizedText.Length; characterIndex++)
        {
            var glyph = Glyphs[normalizedText[characterIndex]];
            var startX = characterIndex * (GlyphWidth + CharacterGap) * CellSize;

            for (var row = 0; row < GlyphHeight; row++)
            {
                for (var column = 0; column < GlyphWidth; column++)
                {
                    if (glyph[row][column] == '1')
                    {
                        svg.AppendLine(
                            $"  <rect x=\"{startX + column * CellSize}\" y=\"{row * CellSize}\" width=\"{CellSize}\" height=\"{CellSize}\" fill=\"{PixelColor}\"/>");
                    }
                }
            }
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    internal static bool TryRenderSvg(string? text, out string svg)
    {
        if (!TryNormalize(text, out var normalizedText))
        {
            svg = string.Empty;
            return false;
        }

        svg = RenderSvg(normalizedText);
        return true;
    }

    private static bool TryNormalize(string? text, out string normalizedText)
    {
        normalizedText = text?.ToUpperInvariant() ?? string.Empty;
        return normalizedText.Length is > 0 and <= MaxCharacters
            && normalizedText.All(Glyphs.ContainsKey);
    }
}
