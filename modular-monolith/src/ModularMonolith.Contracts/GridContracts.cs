using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularMonolith.Contracts;

public sealed record GridSettings(int Dimension, GridPattern Pattern);

public enum GridPattern
{
    Checkerboard,
    Radial
}

public static class GridRequestParser
{
    public static GridSettings Parse(string? pattern, int? size)
    {
        var gridPattern = Enum.TryParse<GridPattern>(pattern, true, out var parsedPattern)
            && Enum.IsDefined(parsedPattern)
            ? parsedPattern
            : GridPattern.Checkerboard;

        return new GridSettings(Math.Clamp(size ?? 6, 2, 20), gridPattern);
    }
}

public static class GridCacheKey
{
    public const string Version = "grid-v1";

    public static string Create(GridSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"{Version}-{settings.Pattern.ToString().ToLowerInvariant()}-{settings.Dimension}";
    }
}

public sealed record RenderPageRequest(GridSettings Settings, string SvgContent);

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class ModuleCatalog
{
    public const string SvgRenderer = "svg-renderer";
    public const string ImageCache = "image-cache";

    public static readonly string[] All = [SvgRenderer, ImageCache];
}
