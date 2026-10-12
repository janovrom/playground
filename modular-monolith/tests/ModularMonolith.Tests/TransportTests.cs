using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Tests;

public sealed class TransportTests
{
    [Fact]
    public void UnixSocket_AllocatesSocketsInTheConfiguredDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"transport-test-{Guid.NewGuid():N}");
        try
        {
            using var allocator = new UnixSocketTransport().CreateAllocator(Configuration(("Workers:SocketDirectory", directory)));

            var endpoint = allocator.Allocate("svg-renderer");

            Assert.Equal(UnixSocketTransport.TransportName, endpoint.Transport);
            Assert.Equal(Path.Combine(directory, "svg-renderer.sock"), endpoint.Address);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnixSocket_UsesPrivateTemporaryDirectoryWhenNoneIsConfigured_AndRemovesItOnDispose()
    {
        var allocator = new UnixSocketTransport().CreateAllocator(Configuration());
        var directory = Path.GetDirectoryName(allocator.Allocate("image-cache").Address)!;

        Assert.True(Directory.Exists(directory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }

        allocator.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void UnixSocket_CleanupRemovesTheStaleSocketFile()
    {
        var directory = Directory.CreateTempSubdirectory("transport-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "stale.sock");
            File.WriteAllText(path, string.Empty);

            new UnixSocketTransport().Cleanup(new WorkerEndpoint(UnixSocketTransport.TransportName, path));

            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void NamedPipe_AllocatesPerRunPipeNamesPerModule()
    {
        using var first = new NamedPipeTransport().CreateAllocator(Configuration());
        using var second = new NamedPipeTransport().CreateAllocator(Configuration());

        var renderer = first.Allocate("svg-renderer");
        var cache = first.Allocate("image-cache");

        Assert.Equal(NamedPipeTransport.TransportName, renderer.Transport);
        Assert.StartsWith("modular-monolith.", renderer.Address);
        Assert.EndsWith(".svg-renderer", renderer.Address);
        Assert.EndsWith(".image-cache", cache.Address);
        Assert.NotEqual(renderer.Address, second.Allocate("svg-renderer").Address);
        Assert.Equal(renderer.Address, first.Allocate("svg-renderer").Address);
    }

    [Fact]
    public void NamedPipe_HonoursTheConfiguredPrefix()
    {
        using var allocator = new NamedPipeTransport().CreateAllocator(Configuration(("Workers:PipeNamePrefix", "custom")));

        Assert.StartsWith("custom.", allocator.Allocate("svg-renderer").Address);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("")]
    public void Allocators_RejectInvalidModuleNames(string moduleName)
    {
        using var unix = new UnixSocketTransport().CreateAllocator(Configuration());
        using var pipe = new NamedPipeTransport().CreateAllocator(Configuration());

        Assert.Throws<ArgumentException>(() => unix.Allocate(moduleName));
        Assert.Throws<ArgumentException>(() => pipe.Allocate(moduleName));
    }

    [Theory]
    [InlineData("UnixSocket", typeof(UnixSocketTransport))]
    [InlineData("unixsocket", typeof(UnixSocketTransport))]
    [InlineData("NamedPipe", typeof(NamedPipeTransport))]
    [InlineData("namedpipe", typeof(NamedPipeTransport))]
    public void Resolve_SelectsTheNamedTransport(string kind, Type expected)
        => Assert.IsType(expected, WorkerTransports.Resolve(kind));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    public void Resolve_AutoSelectsTheTransportOfThePlatform(string? kind)
    {
        var expected = OperatingSystem.IsWindows() ? typeof(NamedPipeTransport) : typeof(UnixSocketTransport);

        Assert.IsType(expected, WorkerTransports.Resolve(kind));
    }

    [Fact]
    public void Resolve_RejectsUnknownTransports()
        => Assert.Throws<InvalidOperationException>(() => WorkerTransports.Resolve("carrier-pigeon"));

    [Fact]
    public async Task UnixSocket_RoundTripsAnHttpRequest()
        => await AssertRoundTripAsync(new UnixSocketTransport());

    [WindowsFact]
    public async Task NamedPipe_RoundTripsAnHttpRequest()
        => await AssertRoundTripAsync(new NamedPipeTransport());

    private static async Task AssertRoundTripAsync(IWorkerTransport transport)
    {
        using var allocator = transport.CreateAllocator(Configuration());
        var endpoint = allocator.Allocate("round-trip");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => transport.Listen(options, endpoint));
        await using var app = builder.Build();
        app.MapGet("/ping", () => "pong");

        await app.StartAsync();
        try
        {
            using var client = transport.CreateHttpClient(endpoint);

            Assert.Equal("pong", await client.GetStringAsync("/ping"));
        }
        finally
        {
            await app.StopAsync();
            transport.Cleanup(endpoint);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
