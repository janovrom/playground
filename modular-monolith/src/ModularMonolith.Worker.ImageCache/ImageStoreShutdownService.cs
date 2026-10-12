using Microsoft.Extensions.Hosting;

namespace ModularMonolith.Worker.ImageCache;

internal sealed class ImageStoreShutdownService(IImageStore store, ILogger<ImageStoreShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.CheckpointAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to checkpoint the image cache database during shutdown.");
            throw;
        }
    }
}
