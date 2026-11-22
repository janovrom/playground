using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using Microsoft.Extensions.Logging.Abstractions;
using ModularMonolith.Host.Modules;
using ModularMonolith.Platform;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Worker.Hosting;

namespace ModularMonolith.Tests;

public sealed class GatewayModuleTests
{
    [Fact]
    public void SharedDeclarations_RegisterGeneratorCompatibleDescriptors()
    {
        var services = new ServiceCollection();
        services.AddWorkerHttpResilience();
        services.AddSingleton<ModularMonolith.Host.IWorkerClientFactory>(
            new RecordingClientFactory());
        services.AddGeneratedWorkerModules();
        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetServices<HostModuleDescriptor>().ToArray();

        Assert.Equal(ModuleCatalog.All.Order(), descriptors.Select(module => module.Name).Order());
        Assert.Contains(
            descriptors.SelectMany(module => module.ProxyRoutes),
            route => route.PublicPath == "/render/{**path}" && route.DestinationPath == "/render/{**path}");
        Assert.Contains(
            descriptors.SelectMany(module => module.ProxyRoutes),
            route => route.PublicPath == "/images/{**path}" && route.DestinationPath == "/images/{**path}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ImageCache:DatabasePath"] = "/data/cache.db"
            })
            .Build();
        var imageCache = Assert.Single(descriptors, descriptor => descriptor.Name == ModuleCatalog.ImageCache);
        var renderer = Assert.Single(descriptors, descriptor => descriptor.Name == ModuleCatalog.SvgRenderer);
        Assert.Equal(new ModularMonolith.Abstractions.JobLimits(), imageCache.DefaultLimits);
        Assert.Equal(["--ImageCache:DatabasePath=/data/cache.db"], imageCache.GetWorkerArguments(configuration));
        Assert.Equal(new ModularMonolith.Abstractions.JobLimits(), renderer.DefaultLimits);
        Assert.Empty(renderer.GetWorkerArguments(configuration));
    }

    [Fact]
    public void DescriptorValidation_RejectsOverlappingRoutesAndHostRoutes()
    {
        var module = Descriptor("alpha", new WorkerProxyRouteDescriptor("/images/{**path}", "/images/{**path}"));
        var overlapping = Descriptor("beta", new WorkerProxyRouteDescriptor("/images/special", "/special"));

        var overlap = Assert.Throws<InvalidOperationException>(
            () => HostModuleDescriptorValidator.Validate([module, overlapping]));
        Assert.Contains("overlap", overlap.Message);

        var reserved = Descriptor("alpha", new WorkerProxyRouteDescriptor("/health/{**path}", "/health/{**path}"));
        var conflict = Assert.Throws<InvalidOperationException>(
            () => HostModuleDescriptorValidator.Validate([reserved]));
        Assert.Contains("Host route", conflict.Message);
    }

    [Fact]
    public void InternalContext_RoundTripsOnlyConfiguredClaims()
    {
        var codec = new WorkerInternalContext(WorkerSigningKey.Create());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("subject", "user-42"), new Claim("ignored", "secret")],
            "test"));

        var decoded = codec.Decode(codec.Encode(principal, ["subject"]));

        Assert.True(decoded.Identity?.IsAuthenticated);
        Assert.Equal("test", decoded.Identity?.AuthenticationType);
        Assert.Equal("user-42", decoded.FindFirst("subject")?.Value);
        Assert.Null(decoded.FindFirst("ignored"));
    }

    [Fact]
    public void InternalContext_RejectsTampering()
    {
        var codec = new WorkerInternalContext(WorkerSigningKey.Create());
        var value = codec.Encode(new ClaimsPrincipal(new ClaimsIdentity()), []);
        var separator = value.IndexOf('.');
        var tamperedSignature = $"{(value[separator + 1] == 'A' ? 'B' : 'A')}{value[(separator + 2)..]}";
        var tampered = $"{value[..(separator + 1)]}{tamperedSignature}";

        Assert.Throws<WorkerInternalContextException>(() => codec.Decode(tampered));
    }

    [Fact]
    public void InternalContext_RejectsAnotherHostKey()
    {
        var sender = new WorkerInternalContext(WorkerSigningKey.Create());
        var receiver = new WorkerInternalContext(WorkerSigningKey.Create());

        var value = sender.Encode(new ClaimsPrincipal(new ClaimsIdentity()), []);

        Assert.Throws<WorkerInternalContextException>(() => receiver.Decode(value));
    }

    [Fact]
    public void InternalContext_RejectsExpiredContext()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var codec = new WorkerInternalContext(WorkerSigningKey.Create(), time);
        var value = codec.Encode(new ClaimsPrincipal(new ClaimsIdentity()), []);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Throws<WorkerInternalContextException>(() => codec.Decode(value));
    }

    [Fact]
    public void ProxyConfiguration_ChangesWithWorkerReadiness()
    {
        var source = new RoutingSource("alpha");
        var catalog = new HostModuleCatalog([
            Descriptor("alpha", new WorkerProxyRouteDescriptor("/alpha/{**path}", "/internal/{**path}"))
        ]);
        var provider = new WorkerProxyConfigProvider(
            catalog,
            source,
            NullLogger<WorkerProxyConfigProvider>.Instance);
        var previous = provider.GetConfig();

        Assert.Empty(Assert.Single(previous.Clusters).Destinations!);

        source.SetReady(true);

        Assert.True(previous.ChangeToken.HasChanged);
        var current = provider.GetConfig();
        Assert.Single(Assert.Single(current.Clusters).Destinations!);
        Assert.NotEqual(previous.RevisionId, current.RevisionId);
    }

    [Fact]
    public async Task HostContextMiddleware_ReplacesSpoofedHeaderAndAllowListsClaims()
    {
        var key = WorkerSigningKey.Create();
        var codec = new WorkerInternalContext(key);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ForwardedClaimTypes:0"] = "subject"
            })
            .Build();
        var decodedClaims = default(ClaimsPrincipal);
        var middleware = new HostInternalContextMiddleware(
            context =>
            {
                decodedClaims = codec.Decode(context.Request.Headers[WorkerInternalContextConstants.HeaderName]!);
                return Task.CompletedTask;
            },
            codec,
            configuration);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("subject", "user-42"), new Claim("ignored", "secret")],
                "test"))
        };
        httpContext.Request.Headers[WorkerInternalContextConstants.HeaderName] = "spoofed";

        await middleware.InvokeAsync(httpContext);

        Assert.NotNull(decodedClaims);
        Assert.Equal("user-42", decodedClaims.FindFirst("subject")?.Value);
        Assert.Null(decodedClaims.FindFirst("ignored"));
    }

    [Fact]
    public async Task WorkerContextMiddleware_RejectsMissingAndTamperedHeaders()
    {
        var codec = new WorkerInternalContext(WorkerSigningKey.Create());
        var called = false;
        var middleware = new WorkerInternalContextMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, codec);
        var missing = new DefaultHttpContext();
        missing.Request.Path = "/render/svg";

        await middleware.InvokeAsync(missing);

        Assert.Equal(StatusCodes.Status401Unauthorized, missing.Response.StatusCode);
        Assert.False(called);

        var tampered = new DefaultHttpContext();
        tampered.Request.Path = "/render/svg";
        tampered.Request.Headers[WorkerInternalContextConstants.HeaderName] = "invalid";
        await middleware.InvokeAsync(tampered);

        Assert.Equal(StatusCodes.Status401Unauthorized, tampered.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task WorkerContextMiddleware_ExemptsHealthAndRestoresSignedPrincipal()
    {
        var codec = new WorkerInternalContext(WorkerSigningKey.Create());
        var calls = 0;
        var middleware = new WorkerInternalContextMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, codec);
        var health = new DefaultHttpContext();
        health.Request.Path = "/health";

        await middleware.InvokeAsync(health);

        var secured = new DefaultHttpContext();
        secured.Request.Path = "/render/svg";
        secured.Request.Headers[WorkerInternalContextConstants.HeaderName] =
            codec.Encode(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("subject", "user-42")],
                    "test")),
                ["subject"]);
        await middleware.InvokeAsync(secured);

        Assert.Equal(2, calls);
        Assert.Equal("user-42", secured.User.FindFirst("subject")?.Value);
    }

    [Fact]
    public async Task ProxyFailureMiddleware_WritesStableProblemResponse()
    {
        var middleware = new WorkerProxyFailureMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Contains("Worker unavailable", body);
        Assert.Contains("\"status\":503", body);
    }

    private static HostModuleDescriptor Descriptor(string name, params WorkerProxyRouteDescriptor[] routes)
        => new(
            name,
            $"{name}.Worker",
            name,
            new ModularMonolith.Abstractions.JobLimits(),
            routes,
            _ => [],
            _ => { });

    private sealed class RecordingClientFactory : ModularMonolith.Host.IWorkerClientFactory
    {
        public HttpClient Create(string moduleName, string configSection) => new();
    }

    private sealed class RoutingSource(string name) : IWorkerRoutingSource
    {
        private WorkerRoutingSnapshot _snapshot = new(
            name,
            new WorkerEndpoint(UnixSocketTransport.TransportName, "/tmp/alpha.sock"),
            false,
            "stopped");

        public event EventHandler<IReadOnlyList<WorkerRoutingSnapshot>>? TopologyChanged;

        public IReadOnlyList<WorkerRoutingSnapshot> GetRoutingSnapshot() => [_snapshot];

        public void SetReady(bool ready)
        {
            _snapshot = _snapshot with { IsReady = ready, Revision = ready ? "ready" : "stopped" };
            TopologyChanged?.Invoke(this, [_snapshot]);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
