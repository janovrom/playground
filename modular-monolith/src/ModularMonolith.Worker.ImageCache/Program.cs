using ModularMonolith.Worker.Hosting;
using ModularMonolith.Worker.ImageCache;

await WorkerHost.RunAsync(args, new ImageCacheWorkerModule());
