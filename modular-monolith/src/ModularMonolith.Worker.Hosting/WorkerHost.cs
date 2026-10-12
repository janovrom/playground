using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Linux;

namespace ModularMonolith.Worker.Hosting;

public static class WorkerHost
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var socketPath = builder.Configuration["Worker:SocketPath"];
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new InvalidOperationException("Worker:SocketPath must be configured.");
        }

        var fullSocketPath = Path.GetFullPath(socketPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullSocketPath)!);
        File.Delete(fullSocketPath);

        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(fullSocketPath));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(8));

        LinuxProcessGuard.EnsureKillOnParentExit();

        builder.Services.AddSingleton(new WorkerSocketCleanup(fullSocketPath));
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WorkerSocketCleanup>());
    }

    public static void MapHealth(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    }

    private sealed class WorkerSocketCleanup(string socketPath) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            File.Delete(socketPath);
            return Task.CompletedTask;
        }
    }
}
