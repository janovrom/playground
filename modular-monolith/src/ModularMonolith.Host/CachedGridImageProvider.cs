using System.Text;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host;

internal sealed record GridImageResult(string Svg, bool CacheHit);

internal interface IGridImageProvider
{
    Task<GridImageResult> GetSvgAsync(GridSettings settings, CancellationToken cancellationToken);
    Task<string> RenderPageAsync(GridSettings settings, string svg, CancellationToken cancellationToken);
}

internal sealed class CachedGridImageProvider(
    IImageCacheClient cache,
    IRendererClient renderer,
    ILogger<CachedGridImageProvider> logger) : IGridImageProvider
{
    public async Task<GridImageResult> GetSvgAsync(GridSettings settings, CancellationToken cancellationToken)
    {
        var key = GridCacheKey.Create(settings);
        try
        {
            var cached = await cache.GetAsync(key, cancellationToken);
            if (cached is not null)
            {
                logger.LogInformation("Grid image cache hit for {CacheKey}.", key);
                return new GridImageResult(Encoding.UTF8.GetString(cached), true);
            }
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Grid image cache read failed for {CacheKey}; rendering instead.", key);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Grid image cache read timed out for {CacheKey}; rendering instead.", key);
        }

        logger.LogInformation("Grid image cache miss for {CacheKey}.", key);
        var svg = await renderer.RenderSvgAsync(settings, cancellationToken);
        try
        {
            await cache.PutAsync(key, Encoding.UTF8.GetBytes(svg), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Grid image cache write failed for {CacheKey}.", key);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Grid image cache write timed out for {CacheKey}.", key);
        }

        return new GridImageResult(svg, false);
    }

    public Task<string> RenderPageAsync(GridSettings settings, string svg, CancellationToken cancellationToken)
        => renderer.RenderPageAsync(settings, svg, cancellationToken);
}
