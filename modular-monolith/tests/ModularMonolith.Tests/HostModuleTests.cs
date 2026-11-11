using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Tests;

public sealed class HostModuleTests
{
    [Fact]
    public void WorkerManager_ManagesExactlyTheRegisteredModules()
    {
        var manager = CreateManager(Configuration(), new RecordingLauncher(), new AlphaModule(), new BetaModule());

        Assert.Equal(["alpha", "beta"], manager.GetStatus().Select(status => status.Name).Order());
        Assert.All(manager.GetStatus(), status => Assert.Equal("Stopped", status.State));
    }

    [Fact]
    public async Task WorkerManager_LaunchesModulesWithTransportEndpointArgumentsAndLimits()
    {
        var launcher = new RecordingLauncher();
        var configuration = Configuration(
            ("Workers:Alpha:Limits:MemoryMaxMb", "64"),
            ("Workers:Alpha:Limits:CpuCores", "1.5"),
            ("Workers:Alpha:Arguments:0", "alpha.dll"));
        var manager = CreateManager(configuration, launcher, new AlphaModule());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal("alpha", launch.ModuleName);
        Assert.Equal("dotnet", launch.ExecutablePath);
        Assert.Equal("alpha.dll", launch.Arguments![0]);
        Assert.Contains("--alpha-arg=1", launch.Arguments);
        Assert.Contains("--Worker:Transport=UnixSocket", launch.Arguments);
        var endpointArgument = Assert.Single(launch.Arguments, argument => argument.StartsWith("--Worker:Endpoint=", StringComparison.Ordinal));
        Assert.EndsWith("alpha.sock", endpointArgument);
        Assert.Equal(new JobLimits(MemoryMaxBytes: 64L * 1024 * 1024, MemoryHighBytes: 96L * 1024 * 1024, CpuCores: 1.5, MaxProcesses: 10), launch.Limits);

        var endpointDirectory = Path.GetDirectoryName(endpointArgument["--Worker:Endpoint=".Length..])!;
        Assert.False(Directory.Exists(endpointDirectory), "A failed start must release the temporary endpoint directory.");
    }

    [Fact]
    public async Task WorkerManager_UsesDefaultLimitsOfTheModuleWhenNothingIsConfigured()
    {
        var launcher = new RecordingLauncher();
        var manager = CreateManager(Configuration(), launcher, new AlphaModule());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(new AlphaModule().DefaultLimits, Assert.Single(launcher.Launches).Limits);
    }

    [Fact]
    public void WorkerManager_ReportsAnUnallocatedEndpointBeforeStart()
    {
        var manager = CreateManager(Configuration(), new RecordingLauncher(), new AlphaModule());

        Assert.Throws<InvalidOperationException>(() => manager.GetEndpoint("alpha"));
        Assert.Throws<InvalidOperationException>(() => manager.GetEndpoint("unknown"));
        Assert.False(manager.IsRunning("alpha"));
        Assert.Throws<InvalidOperationException>(() => manager.IsRunning("unknown"));
    }

    [Fact]
    public async Task WorkerResilience_RetriesTransientConnectionFailuresTwice()
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("retry-test")
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((_, _) =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                {
                    throw new HttpRequestException("Transient connection failure.");
                }

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-test");
        client.BaseAddress = new Uri("http://localhost");
        var stopwatch = Stopwatch.StartNew();

        using var content = new StringContent("payload");
        using var response = await client.PostAsync("/", content);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Retries exceeded the 2-second restart window: {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task WorkerResilience_StopsAfterTwoRetries()
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("retry-test")
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new HttpRequestException("Worker is restarting.");
            }));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-test");
        client.BaseAddress = new Uri("http://localhost");
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/"));

        Assert.Equal(3, attempts);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Retries exceeded the 2-second restart window: {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task WorkerClientFactory_UsesConfiguredTimeoutAndDefaultsToTenSeconds()
    {
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("alpha")
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }));

        using var provider = services.BuildServiceProvider();
        var configuration = Configuration(("Workers:Alpha:TimeoutSeconds", "1"));
        var factory = new WorkerClientFactory(provider.GetRequiredService<IHttpClientFactory>(), configuration);

        using var configuredClient = factory.Create("alpha", "Alpha");
        Assert.Equal(TimeSpan.FromSeconds(1), configuredClient.Timeout);
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuredClient.GetAsync("http://localhost/"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Configured timeout took too long: {stopwatch.Elapsed}.");

        using var defaultClient = factory.Create("alpha", "Missing");
        Assert.Equal(TimeSpan.FromSeconds(10), defaultClient.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void WorkerClientFactory_RejectsNonPositiveTimeouts(string timeout)
    {
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("alpha");
        using var provider = services.BuildServiceProvider();
        var configuration = Configuration(("Workers:Alpha:TimeoutSeconds", timeout));
        var factory = new WorkerClientFactory(provider.GetRequiredService<IHttpClientFactory>(), configuration);

        Assert.Throws<InvalidOperationException>(() => factory.Create("alpha", "Alpha"));
    }

    [Fact]
    public async Task WorkerReadinessHandler_DoesNotSendWhileWorkerIsUnavailable()
    {
        var resolver = new StubEndpointResolver();
        var inner = new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var client = new HttpClient(new WorkerReadinessHandler("alpha", resolver) { InnerHandler = inner })
        {
            BaseAddress = new Uri("http://localhost")
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/"));
        Assert.Equal(0, inner.RequestCount);

        resolver.Running = true;
        using var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task WorkerResilience_RetriesRequestsUntilWorkerReadinessReturns()
    {
        var resolver = new StubEndpointResolver { UnavailableChecks = 2 };
        var inner = new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("alpha")
            .ConfigurePrimaryHttpMessageHandler(() => new WorkerReadinessHandler("alpha", resolver) { InnerHandler = inner });

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("alpha");
        client.BaseAddress = new Uri("http://localhost");

        using var content = new StringContent("payload");
        using var response = await client.PostAsync("/", content);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, resolver.ReadinessChecks);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public void AddHostModule_RegistersTheModuleAndItsServices()
    {
        var services = new ServiceCollection();

        services.AddHostModule<AlphaModule>();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AlphaModule>(Assert.Single(provider.GetServices<IHostModule>()));
        Assert.Equal("alpha", provider.GetRequiredService<AlphaMarker>().Module);
    }

    [Fact]
    public void BuiltInModules_RegisterTheirTypedClientsAgainstTheirOwnWorker()
    {
        var factory = new RecordingClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerClientFactory>(factory);
        services.AddHostModule<SvgRendererHostModule>();
        services.AddHostModule<ImageCacheHostModule>();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<RendererClient>(provider.GetRequiredService<IRendererClient>());
        Assert.IsType<ImageCacheClient>(provider.GetRequiredService<IImageCacheClient>());

        Assert.Equal([ModuleCatalog.SvgRenderer, ModuleCatalog.ImageCache], factory.Requested);
        Assert.Equal(ModuleCatalog.All.Order(), provider.GetServices<IHostModule>().Select(module => module.Name).Order());
    }

    [Fact]
    public void BuiltInClients_DisposeTheirHttpClientsWithTheServiceProvider()
    {
        var handler = new TrackingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerClientFactory>(new FixedClientFactory(new HttpClient(handler)));
        services.AddHostModule<SvgRendererHostModule>();

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IRendererClient>();
        }

        Assert.True(handler.Disposed);
    }

    [Fact]
    public void ImageCacheModule_PassesTheConfiguredDatabasePathToItsWorker()
    {
        var arguments = new ImageCacheHostModule().GetWorkerArguments(Configuration(("ImageCache:DatabasePath", "/data/cache.db")));

        Assert.Equal(["--ImageCache:DatabasePath=/data/cache.db"], arguments);
        Assert.Empty(new SvgRendererHostModule().GetWorkerArguments(Configuration()));
    }

    [Fact]
    public void WorkerCommandResolver_DefaultsToDotnetAndTheWorkerAssembly()
    {
        var (executable, arguments) = WorkerCommandResolver.Resolve(Configuration().GetSection("Workers:Alpha"), "Alpha.Worker", "base");

        Assert.Equal("dotnet", executable);
        Assert.Equal([Path.Combine("base", "workers", "Alpha.Worker", "Alpha.Worker.dll")], arguments);
    }

    [Fact]
    public void WorkerCommandResolver_PrefersConfiguredCommand()
    {
        var section = Configuration(("Workers:Alpha:ExecutablePath", "custom"), ("Workers:Alpha:Arguments:0", "a"), ("Workers:Alpha:Arguments:1", "b"))
            .GetSection("Workers:Alpha");

        var (executable, arguments) = WorkerCommandResolver.Resolve(section, "Alpha.Worker", "base");

        Assert.Equal("custom", executable);
        Assert.Equal(["a", "b"], arguments);
    }

    [WindowsFact]
    public void WorkerCommandResolver_PrefersTheApphostOnWindows()
    {
        var directory = Directory.CreateTempSubdirectory("worker-command-");
        try
        {
            var workerDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "workers", "Alpha.Worker"));
            var apphost = Path.Combine(workerDirectory.FullName, "Alpha.Worker.exe");
            File.WriteAllText(apphost, string.Empty);

            var (executable, arguments) = WorkerCommandResolver.Resolve(Configuration().GetSection("Workers:Alpha"), "Alpha.Worker", directory.FullName);

            Assert.Equal(apphost, executable);
            Assert.Empty(arguments);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static WorkerManager CreateManager(IConfiguration configuration, IWorkerLauncher launcher, params IHostModule[] modules)
        => new(launcher, new UnixSocketTransport(), modules, configuration, NullLogger<WorkerManager>.Instance, new RequestDrainTracker());

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed record AlphaMarker(string Module);

    private class AlphaModule : IHostModule
    {
        public virtual string Name => "alpha";
        public string WorkerProject => "Alpha.Worker";
        public string ConfigSection => "Alpha";
        public JobLimits DefaultLimits => new(MaxProcesses: 10);
        public IEnumerable<string> GetWorkerArguments(IConfiguration configuration) => ["--alpha-arg=1"];
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton(new AlphaMarker(Name));
    }

    private sealed class BetaModule : AlphaModule
    {
        public override string Name => "beta";
    }

    private sealed class RecordingClientFactory : IWorkerClientFactory
    {
        public List<string> Requested { get; } = [];

        public HttpClient Create(string moduleName, string configSection)
        {
            Requested.Add(moduleName);
            return new HttpClient { BaseAddress = new Uri("http://localhost") };
        }
    }

    private sealed class StubEndpointResolver : IWorkerEndpointResolver
    {
        public bool Running { get; set; }
        public int UnavailableChecks { get; set; } = int.MaxValue;
        public int ReadinessChecks { get; private set; }

        public WorkerEndpoint GetEndpoint(string name) => new(UnixSocketTransport.TransportName, "/tmp/test.sock");

        public bool IsRunning(string name)
        {
            ReadinessChecks++;
            return Running || ReadinessChecks > UnavailableChecks;
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            Disposed = disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class FixedClientFactory(HttpClient client) : IWorkerClientFactory
    {
        public HttpClient Create(string moduleName, string configSection) => client;
    }

    // Exits right away so StartAsync fails after the options were recorded.
    private sealed class RecordingLauncher : IWorkerLauncher
    {
        public List<WorkerLaunchOptions> Launches { get; } = [];

        public WorkerProcess Launch(WorkerLaunchOptions options)
        {
            Launches.Add(options);
            return new LocalWorkerLauncher().Launch(TestProcesses.Exit(options.ModuleName, 3));
        }
    }
}
