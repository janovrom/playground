using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Platform;

namespace ModularMonolith.Tests;

public sealed class HostModuleTests
{
    /// <summary>Verifies the manager creates one status entry for each registered module.</summary>
    [Fact]
    public void WorkerManager_ManagesExactlyTheRegisteredModules()
    {
        var manager = CreateManager(Configuration(), new RecordingLauncher(), new AlphaModule(), new BetaModule());

        Assert.Equal(["alpha", "beta"], manager.GetStatus().Select(status => status.Name).Order());
        Assert.All(manager.GetStatus(), status => Assert.Equal("Stopped", status.State));
    }

    /// <summary>Verifies worker launches receive resolved arguments, limits, and signing context.</summary>
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
        var signingKey = Assert.Single(launch.Environment!);
        Assert.Equal(WorkerInternalContextConstants.SigningKeyEnvironmentVariable, signingKey.Key);
        Assert.Equal(32, Convert.FromBase64String(signingKey.Value).Length);
        Assert.DoesNotContain(launch.Arguments!, argument => argument.Contains(signingKey.Value, StringComparison.Ordinal));

        var endpointDirectory = Path.GetDirectoryName(endpointArgument["--Worker:Endpoint=".Length..])!;
        Assert.False(Directory.Exists(endpointDirectory), "A failed start must release the temporary endpoint directory.");
    }

    /// <summary>Verifies worker launch options use module defaults when no limits are configured.</summary>
    [Fact]
    public async Task WorkerManager_UsesDefaultLimitsOfTheModuleWhenNothingIsConfigured()
    {
        var launcher = new RecordingLauncher();
        var manager = CreateManager(Configuration(), launcher, new AlphaModule());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(
            new JobLimits(MaxProcesses: 10),
            Assert.Single(launcher.Launches).Limits);
    }

    /// <summary>Verifies endpoints are unavailable until worker startup allocates them.</summary>
    [Fact]
    public void WorkerManager_ReportsAnUnallocatedEndpointBeforeStart()
    {
        var manager = CreateManager(Configuration(), new RecordingLauncher(), new AlphaModule());

        Assert.Throws<InvalidOperationException>(() => manager.GetEndpoint("alpha"));
        Assert.Throws<InvalidOperationException>(() => manager.GetEndpoint("unknown"));
        Assert.False(manager.IsRunning("alpha"));
        Assert.Throws<InvalidOperationException>(() => manager.IsRunning("unknown"));
    }

    /// <summary>Verifies safe worker requests retry transient connection failures twice.</summary>
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

        using var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Retries exceeded the 2-second restart window: {stopwatch.Elapsed}.");
    }

    /// <summary>Verifies unsafe worker methods do not retry failed requests.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task WorkerResilience_DoesNotRetryUnsafeMethods(string method)
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddHttpClient("retry-test")
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new HttpRequestException("Transient connection failure.");
            }));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-test");
        client.BaseAddress = new Uri("http://localhost");
        using var request = new HttpRequestMessage(new HttpMethod(method), "/");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, attempts);
    }

    /// <summary>Verifies safe worker requests stop after the configured retry budget.</summary>
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

    /// <summary>Verifies configured worker timeouts and the ten-second default.</summary>
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
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Configured timeout took too long: {stopwatch.Elapsed}.");

        using var defaultClient = factory.Create("alpha", "Missing");
        Assert.Equal(TimeSpan.FromSeconds(10), defaultClient.Timeout);
    }

    /// <summary>Verifies non-positive worker timeouts are rejected.</summary>
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

    /// <summary>Verifies worker requests are blocked while a worker is unavailable.</summary>
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

    /// <summary>Verifies safe requests retry until worker readiness returns.</summary>
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

        using var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, resolver.ReadinessChecks);
        Assert.Equal(1, inner.RequestCount);
    }

    /// <summary>Verifies module customization registers its services.</summary>
    [Fact]
    public void AddHostModuleCustomization_RegistersTheModuleAndItsServices()
    {
        var services = new ServiceCollection();

        services.AddHostModuleCustomization<AlphaModule>();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("alpha", provider.GetRequiredService<AlphaMarker>().Module);
    }

    /// <summary>Verifies descriptor composition rejects customizations for unknown modules.</summary>
    [Fact]
    public void HostModuleDescriptorComposer_RejectsUnknownCustomization()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HostModuleDescriptorComposer.Compose(
                [Descriptor(new BetaModule())],
                [new AlphaModule()]));

        Assert.Contains("unknown module 'alpha'", exception.Message);
    }

    /// <summary>Verifies descriptor composition rejects duplicate customizations.</summary>
    [Fact]
    public void HostModuleDescriptorComposer_RejectsDuplicateCustomizations()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HostModuleDescriptorComposer.Compose(
                [Descriptor(new AlphaModule())],
                [new AlphaModule(), new AlphaModule()]));

        Assert.Contains("module 'alpha' is registered more than once", exception.Message);
    }

    /// <summary>Verifies generated clients are registered against their declared workers.</summary>
    [Fact]
    public void BuiltInModules_RegisterTheirTypedClientsAgainstTheirOwnWorker()
    {
        var factory = new RecordingClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerClientFactory>(factory);
        services.AddGeneratedWorkerModules();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<RendererClient>(provider.GetRequiredService<IRendererClient>());
        Assert.IsType<ImageCacheClient>(provider.GetRequiredService<IImageCacheClient>());

        Assert.Equal([ModuleCatalog.SvgRenderer, ModuleCatalog.ImageCache], factory.Requested);
        Assert.Equal(ModuleCatalog.All.Order(), provider.GetServices<HostModuleDescriptor>().Select(module => module.Name).Order());
    }

    /// <summary>Verifies generated clients dispose their HTTP handlers with the provider.</summary>
    [Fact]
    public void BuiltInClients_DisposeTheirHttpClientsWithTheServiceProvider()
    {
        var handler = new TrackingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerClientFactory>(new FixedClientFactory(new HttpClient(handler)));
        services.AddGeneratedWorkerModules();

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IRendererClient>();
        }

        Assert.True(handler.Disposed);
    }

    /// <summary>Verifies the image cache customization forwards its database path.</summary>
    [Fact]
    public void ImageCacheModule_PassesTheConfiguredDatabasePathToItsWorker()
    {
        var arguments = new ImageCacheHostCustomization().GetWorkerArguments(
            Configuration(("ImageCache:DatabasePath", "/data/cache.db")));

        Assert.Equal(["--ImageCache:DatabasePath=/data/cache.db"], arguments);
    }

    /// <summary>Verifies the command resolver defaults to the dotnet worker assembly.</summary>
    [Fact]
    public void WorkerCommandResolver_DefaultsToDotnetAndTheWorkerAssembly()
    {
        var (executable, arguments) = WorkerCommandResolver.Resolve(Configuration().GetSection("Workers:Alpha"), "Alpha.Worker", "base");

        Assert.Equal("dotnet", executable);
        Assert.Equal([Path.Combine("base", "workers", "Alpha.Worker", "Alpha.Worker.dll")], arguments);
    }

    /// <summary>Verifies the command resolver prefers explicitly configured commands.</summary>
    [Fact]
    public void WorkerCommandResolver_PrefersConfiguredCommand()
    {
        var section = Configuration(("Workers:Alpha:ExecutablePath", "custom"), ("Workers:Alpha:Arguments:0", "a"), ("Workers:Alpha:Arguments:1", "b"))
            .GetSection("Workers:Alpha");

        var (executable, arguments) = WorkerCommandResolver.Resolve(section, "Alpha.Worker", "base");

        Assert.Equal("custom", executable);
        Assert.Equal(["a", "b"], arguments);
    }

    /// <summary>Verifies the command resolver prefers an available worker apphost.</summary>
    [Fact]
    public void WorkerCommandResolver_PrefersTheApphost()
    {
        var directory = Directory.CreateTempSubdirectory("worker-command-");
        try
        {
            var workerDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "workers", "Alpha.Worker"));
            var apphostName = OperatingSystem.IsWindows() ? "Alpha.Worker.exe" : "Alpha.Worker";
            var apphost = Path.Combine(workerDirectory.FullName, apphostName);
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

    private static WorkerManager CreateManager(IConfiguration configuration, IWorkerLauncher launcher, params AlphaModule[] modules)
        => new(
            launcher,
            new UnixSocketTransport(),
            new HostModuleCatalog(HostModuleDescriptorComposer.Compose(
                modules.Select(Descriptor),
                modules)),
            configuration,
            NullLogger<WorkerManager>.Instance,
            new RequestDrainTracker());

    private static HostModuleDescriptor Descriptor(AlphaModule module)
        => new(
            module.Name,
            module.WorkerProject,
            module.ConfigSection,
            new JobLimits(),
            [],
            _ => []);

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed record AlphaMarker(string Module);

    private class AlphaModule : IHostModuleCustomization
    {
        public virtual string Name => "alpha";
        public string ModuleName => Name;
        public string WorkerProject => "Alpha.Worker";
        public string ConfigSection => "Alpha";

        /// <summary>Applies the test module's worker process limit.</summary>
        public JobLimits CustomizeLimits(JobLimits defaults) => defaults with { MaxProcesses = 10 };

        /// <summary>Returns the test module's worker launch argument.</summary>
        public IEnumerable<string> GetWorkerArguments(IConfiguration configuration) => ["--alpha-arg=1"];

        /// <summary>Registers a marker service for the test module.</summary>
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton(new AlphaMarker(Name));
    }

    private sealed class BetaModule : AlphaModule
    {
        public override string Name => "beta";
    }

    private sealed class RecordingClientFactory : IWorkerClientFactory
    {
        public List<string> Requested { get; } = [];

        /// <summary>Creates a test client and records the worker name.</summary>
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

        /// <summary>Returns a placeholder endpoint for the test worker.</summary>
        public WorkerEndpoint GetEndpoint(string name) => new(UnixSocketTransport.TransportName, "/tmp/test.sock");

        /// <summary>Reports readiness according to the test's configured check count.</summary>
        public bool IsRunning(string name)
        {
            ReadinessChecks++;
            return Running || ReadinessChecks > UnavailableChecks;
        }

        /// <summary>Ignores worker failures because the resolver has no process supervisor.</summary>
        public void ObserveWorkerFailure(string name)
        {
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        /// <summary>Counts requests and invokes the test response callback.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        /// <summary>Returns a successful response for the test request.</summary>
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
        /// <summary>Creates the configured test HTTP client.</summary>
        public HttpClient Create(string moduleName, string configSection) => client;
    }

    // Exits right away so StartAsync fails after the options were recorded.
    private sealed class RecordingLauncher : IWorkerLauncher
    {
        public List<WorkerLaunchOptions> Launches { get; } = [];

        /// <summary>Records launch options and returns a process that exits immediately.</summary>
        public WorkerProcess Launch(WorkerLaunchOptions options)
        {
            Launches.Add(options);
            return new LocalWorkerLauncher().Launch(TestProcesses.Exit(options.ModuleName, 3));
        }
    }
}
