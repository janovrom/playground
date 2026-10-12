using ModularMonolith.Worker.Hosting;
using ModularMonolith.Worker.SvgRenderer;

await WorkerHost.RunAsync(args, new SvgRendererWorkerModule());
