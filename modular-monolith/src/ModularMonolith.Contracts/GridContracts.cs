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

/// <summary>Defines a typed Host client for SVG rendering operations.</summary>
[WorkerClient(ModuleCatalog.SvgRenderer)]
public interface IRendererClient
{
    /// <summary>Renders a grid as SVG.</summary>
    [WorkerClientOperation(
        "POST",
        "/render/svg",
        WorkerRequestKind.Json,
        WorkerResponseKind.Text)]
    Task<string> RenderSvgAsync(GridSettings settings, CancellationToken cancellationToken);

    /// <summary>Renders an HTML page from a prepared SVG request.</summary>
    [WorkerClientOperation(
        "POST",
        "/render/page",
        WorkerRequestKind.Json,
        WorkerResponseKind.Text)]
    Task<string> RenderPageAsync(RenderPageRequest request, CancellationToken cancellationToken);
}

/// <summary>Defines a typed Host client for the image cache.</summary>
[WorkerClient(ModuleCatalog.ImageCache)]
public interface IImageCacheClient
{
    /// <summary>Gets an image when the key exists.</summary>
    [WorkerClientOperation(
        "GET",
        "/images/{key}",
        WorkerRequestKind.None,
        WorkerResponseKind.Bytes,
        TreatNotFoundAsNull = true)]
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores an SVG image under the given key.</summary>
    [WorkerClientOperation(
        "PUT",
        "/images/{key}",
        WorkerRequestKind.Bytes,
        WorkerResponseKind.None,
        RequestContentType = "image/svg+xml")]
    Task PutAsync(string key, byte[] svg, CancellationToken cancellationToken);
}

/// <summary>Provides reflection-free JSON metadata for worker contracts.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GridSettings))]
[JsonSerializable(typeof(RenderPageRequest))]
public partial class ContractJsonContext : JsonSerializerContext;

/// <summary>Provides shared JSON options for application endpoints.</summary>
public static class ContractJson
{
    /// <summary>Gets the reflection-free serializer options for shared contracts.</summary>
    public static JsonSerializerOptions Options { get; } = ContractJsonContext.Default.Options;
}

public static class ModuleCatalog
{
    public const string SvgRenderer = "svg-renderer";
    public const string ImageCache = "image-cache";

    public static readonly string[] All = [SvgRenderer, ImageCache];
}
