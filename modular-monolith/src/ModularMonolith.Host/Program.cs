using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Linux;

var builder = WebApplication.CreateBuilder(args);
var cgroupRoot = builder.Configuration["Cgroup:Root"];
var cgroupEnabled = builder.Configuration.GetValue("Cgroup:Enabled", true);
IWorkerLauncher workerLauncher = cgroupEnabled
    ? new CgroupWorkerLauncher(cgroupRoot)
    : new LocalWorkerLauncher();

builder.Services.AddSingleton<IWorkerLauncher>(workerLauncher);
builder.Services.AddSingleton<WorkerManager>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", platform = "linux", pid = Environment.ProcessId }));

app.MapGet("/modules", () => Results.Ok(ModuleCatalog.All));

app.MapPost("/modules/{moduleName}", (string moduleName, ModuleRequest request) =>
{
    var response = ModuleRegistry.Handle(moduleName, request.Operation, request.Payload);
    return Results.Ok(response);
});

app.MapPost("/modules/{moduleName}/workers", (string moduleName, WorkerManager workerManager) =>
{
    if (!ModuleCatalog.All.Contains(moduleName, StringComparer.Ordinal))
    {
        return Results.NotFound();
    }

    var workerConfiguration = builder.Configuration.GetSection($"Workers:{moduleName}");
    var executablePath = workerConfiguration["ExecutablePath"];
    var arguments = workerConfiguration.GetSection("Arguments").Get<string[]>() ?? [];
    if (string.IsNullOrWhiteSpace(executablePath))
    {
        return Results.Problem($"No worker command is configured for module '{moduleName}'.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {

        var worker = workerManager.LaunchWorker(new WorkerLaunchOptions(moduleName, executablePath, arguments));
        return Results.Accepted($"/modules/{moduleName}/workers/{worker.ProcessId}", new { worker.ProcessId, isolation = cgroupEnabled ? "cgroup" : "none" });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/", () => Results.Ok(new
{
    service = "ModularMonolith.Host",
    platform = "linux",
    note = "The host coordinates child module workers and enforces Linux isolation policies."
}));

LinuxProcessGuard.EnsureKillOnParentExit();

app.Run();
