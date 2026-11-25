using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;
using ModularMonolith.Linux;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Worker.ImageCache;
using ModularMonolith.Worker.SvgRenderer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ModularMonolith.Tests;

public sealed class GridModuleTests
{
    /// <summary>Verifies the SVG renderer exposes GET and POST routes.</summary>
    [Fact]
    public void SvgRenderer_MapsGetAndPostForSvgRendering()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        new SvgRendererWorkerModule().MapEndpoints(app);

        var methods = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "/render/svg")
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(HttpMethods.Get, methods);
        Assert.Contains(HttpMethods.Post, methods);
    }

    /// <summary>Verifies the SVG renderer exposes a GET route for pixel text.</summary>
    [Fact]
    public void SvgRenderer_MapsGetForPixelTextRendering()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        new SvgRendererWorkerModule().MapEndpoints(app);

        var methods = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "/render/text")
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(HttpMethods.Get, methods);
    }

    /// <summary>Verifies grid settings normalize patterns and dimensions.</summary>
    [Theory]
    [InlineData(null, null, 6, GridPattern.Checkerboard)]
    [InlineData("radial", 3, 3, GridPattern.Radial)]
    [InlineData("invalid", 1, 2, GridPattern.Checkerboard)]
    [InlineData("Checkerboard", 99, 20, GridPattern.Checkerboard)]
    [InlineData("100", 6, 6, GridPattern.Checkerboard)]
    public void Parse_NormalizesPatternAndDimension(string? pattern, int? size, int expectedSize, GridPattern expectedPattern)
    {
        Assert.Equal(new GridSettings(expectedSize, expectedPattern), GridRequestParser.Parse(pattern, size));
    }

    /// <summary>Verifies equivalent normalized settings produce the same cache key.</summary>
    [Fact]
    public void CacheKey_IsBasedOnNormalizedSettings()
    {
        var first = GridCacheKey.Create(GridRequestParser.Parse("not-a-pattern", 100));
        var equivalent = GridCacheKey.Create(GridRequestParser.Parse(null, 20));

        Assert.Equal("grid-v1-checkerboard-20", first);
        Assert.Equal(equivalent, first);
        Assert.Equal("grid-v1-radial-6", GridCacheKey.Create(new GridSettings(6, GridPattern.Radial)));
    }

    /// <summary>Verifies checkerboard SVG generation is deterministic.</summary>
    [Fact]
    public void RenderSvg_IsDeterministicAndContainsExpectedTiles()
    {
        var settings = new GridSettings(2, GridPattern.Checkerboard);
        var svg = GridImageGenerator.RenderSvg(settings);

        Assert.Equal(svg, GridImageGenerator.RenderSvg(settings));
        Assert.Equal(4, Count(svg, "<rect "));
        Assert.Contains("fill=\"#6366f1\"", svg);
        Assert.Contains("fill=\"#1e293b\"", svg);
        Assert.Contains("width=\"300\"", svg);
    }

    /// <summary>Verifies radial SVG generation uses concentric rings.</summary>
    [Fact]
    public void RenderSvg_RadialPatternUsesConcentricEuclideanRings()
    {
        var svg = GridImageGenerator.RenderSvg(new GridSettings(7, GridPattern.Radial));

        Assert.Equal("#10b981", FillAt(svg, 3, 3, 7));
        Assert.Equal("#0f172a", FillAt(svg, 3, 2, 7));
        Assert.Equal("#10b981", FillAt(svg, 0, 1, 7));
        Assert.Equal("#0f172a", FillAt(svg, 0, 0, 7));
        Assert.Equal(FillAt(svg, 0, 1, 7), FillAt(svg, 6, 1, 7));
    }

    /// <summary>Verifies pixel-text SVG output uses crisp five-by-seven glyphs.</summary>
    [Fact]
    public void PixelTextSvgGenerator_RendersCrispFiveBySevenPixels()
    {
        var svg = PixelTextSvgGenerator.RenderSvg("A");

        Assert.Equal(svg, PixelTextSvgGenerator.RenderSvg("A"));
        Assert.Equal(svg, PixelTextSvgGenerator.RenderSvg("a"));
        Assert.Contains("width=\"50\"", svg);
        Assert.Contains($"height=\"{PixelTextSvgGenerator.TextHeight}\"", svg);
        Assert.Contains("shape-rendering=\"crispEdges\"", svg);
        Assert.Equal(18, Count(svg, "<rect "));
        Assert.Contains("<rect x=\"10\" y=\"0\" width=\"10\" height=\"10\"", svg);
    }

    /// <summary>Verifies pixel-text SVG dimensions account for spaces.</summary>
    [Fact]
    public void PixelTextSvgGenerator_UsesFixedHeightAndAccountsForSpaces()
    {
        var svg = PixelTextSvgGenerator.RenderSvg("A B");

        Assert.Contains("width=\"170\"", svg);
        Assert.Contains($"height=\"{PixelTextSvgGenerator.TextHeight}\"", svg);
        Assert.Equal(38, Count(svg, "<rect "));
    }

    /// <summary>Verifies pixel-text SVG generation accepts the maximum input length.</summary>
    [Fact]
    public void PixelTextSvgGenerator_AcceptsTheMaximumLength()
    {
        var svg = PixelTextSvgGenerator.RenderSvg(new string('A', PixelTextSvgGenerator.MaxCharacters));

        Assert.Contains("width=\"1190\"", svg);
        Assert.Equal(360, Count(svg, "<rect "));
    }

    /// <summary>Verifies pixel-text SVG generation rejects invalid inputs.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    [InlineData("HELLO-WORLD")]
    public void PixelTextSvgGenerator_RejectsInvalidText(string text)
        => Assert.Throws<ArgumentException>(() => PixelTextSvgGenerator.RenderSvg(text));

    /// <summary>Verifies the pixel-text endpoint validates and renders text.</summary>
    [Theory]
    [InlineData("HELLO2", StatusCodes.Status200OK, "image/svg+xml")]
    [InlineData("HELLO-WORLD", StatusCodes.Status400BadRequest, null)]
    public async Task PixelTextEndpoint_ValidatesAndRendersText(
        string text,
        int expectedStatusCode,
        string? expectedContentType)
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        new SvgRendererWorkerModule().MapEndpoints(app);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/render/text");
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/render/text";
        context.Request.QueryString = QueryString.Create("text", text);
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal(expectedContentType, context.Response.ContentType);
        if (expectedStatusCode == StatusCodes.Status200OK)
        {
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("<svg", body);
        }
    }

    /// <summary>Verifies rendered pages embed SVG and selected grid settings.</summary>
    [Fact]
    public void RenderPage_EmbedsSvgAndUsesTheSelectedSettings()
    {
        var settings = new GridSettings(4, GridPattern.Radial);
        var page = ViewRenderer.RenderPage(settings, "<svg id=\"cached\"></svg>");

        Assert.Contains("<h2>Pattern: Radial</h2>", page);
        Assert.Contains("Grid Dimensions: 4x4", page);
        Assert.Contains("<svg id=\"cached\"></svg>", page);
    }

    /// <summary>Verifies image-cache keys allow only route-safe characters.</summary>
    [Theory]
    [InlineData("valid-key_123.ext", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("a/b", false)]
    [InlineData("contains space", false)]
    public void ImageCacheKey_ValidatesRouteSafeKeys(string key, bool expected)
        => Assert.Equal(expected, ImageCacheKey.IsValid(key));

    /// <summary>Verifies the image store persists and replaces cached content.</summary>
    [Fact]
    public async Task ImageStore_PersistsAndOverwritesCachedContent()
    {
        var directory = Directory.CreateTempSubdirectory("image-cache-test-");
        var database = Path.Combine(directory.FullName, "cache.db");
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString();

        try
        {
            var store = new ImageStore(connectionString);
            await store.InitializeAsync(CancellationToken.None);
            Assert.Null(await store.GetAsync("grid-v1-checkerboard-2", CancellationToken.None));

            await store.PutAsync("grid-v1-checkerboard-2", "image/svg+xml", Encoding.UTF8.GetBytes("first"), CancellationToken.None);
            await store.PutAsync("grid-v1-checkerboard-2", "image/svg+xml", Encoding.UTF8.GetBytes("second"), CancellationToken.None);

            var restartedStore = new ImageStore(connectionString);
            var image = await restartedStore.GetAsync("grid-v1-checkerboard-2", CancellationToken.None);
            Assert.NotNull(image);
            Assert.Equal("image/svg+xml", image.ContentType);
            Assert.Equal("second", Encoding.UTF8.GetString(image.Content));
            await restartedStore.CheckpointAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    /// <summary>Verifies a cache hit skips SVG rendering.</summary>
    [Fact]
    public async Task CachedGridImageProvider_HitSkipsRenderer()
    {
        var cache = new FakeImageCacheClient(Encoding.UTF8.GetBytes("<svg>cached</svg>"));
        var renderer = new FakeRendererClient();
        var provider = new CachedGridImageProvider(cache, renderer, NullLogger<CachedGridImageProvider>.Instance);

        var result = await provider.GetSvgAsync(new GridSettings(2, GridPattern.Checkerboard), CancellationToken.None);

        Assert.True(result.CacheHit);
        Assert.Equal("<svg>cached</svg>", result.Svg);
        Assert.Equal(0, renderer.RenderCount);
        Assert.Equal(0, cache.PutCount);
    }

    /// <summary>Verifies a cache miss renders and stores the SVG.</summary>
    [Fact]
    public async Task CachedGridImageProvider_MissRendersAndStoresImage()
    {
        var cache = new FakeImageCacheClient(null);
        var renderer = new FakeRendererClient { Svg = "<svg>generated</svg>" };
        var provider = new CachedGridImageProvider(cache, renderer, NullLogger<CachedGridImageProvider>.Instance);

        var result = await provider.GetSvgAsync(new GridSettings(2, GridPattern.Radial), CancellationToken.None);

        Assert.False(result.CacheHit);
        Assert.Equal("<svg>generated</svg>", result.Svg);
        Assert.Equal(1, renderer.RenderCount);
        Assert.Equal("grid-v1-radial-2", cache.LastPutKey);
        Assert.Equal("<svg>generated</svg>", Encoding.UTF8.GetString(cache.LastPutContent!));
    }

    /// <summary>Verifies cache failures fall back to SVG rendering.</summary>
    [Fact]
    public async Task CachedGridImageProvider_CacheFailureFallsBackToRenderer()
    {
        var cache = new FakeImageCacheClient(null) { ThrowOnRead = true, ThrowOnWrite = true };
        var renderer = new FakeRendererClient { Svg = "<svg>generated</svg>" };
        var provider = new CachedGridImageProvider(cache, renderer, NullLogger<CachedGridImageProvider>.Instance);

        var result = await provider.GetSvgAsync(new GridSettings(2, GridPattern.Radial), CancellationToken.None);

        Assert.False(result.CacheHit);
        Assert.Equal("<svg>generated</svg>", result.Svg);
        Assert.Equal(1, renderer.RenderCount);
    }

    /// <summary>Verifies request draining waits for active requests and rejects new ones.</summary>
    [Fact]
    public async Task RequestDrainTracker_WaitsForActiveRequestsAndRejectsNewRequests()
    {
        var tracker = new RequestDrainTracker();
        Assert.True(tracker.TryBeginRequest());

        tracker.BeginDrain();
        Assert.False(tracker.TryBeginRequest());
        var drained = tracker.WaitForDrainAsync(CancellationToken.None);
        Assert.False(drained.IsCompleted);

        tracker.EndRequest();
        await drained;
    }

    /// <summary>Verifies a worker exits cleanly after receiving SIGTERM.</summary>
    [LinuxFact]
    public async Task WorkerProcess_StopAsync_AllowsSigtermHandlerToExit()
    {
        using var worker = new LocalWorkerLauncher(new SigTermStopStrategy()).Launch(new WorkerLaunchOptions(
            "sigterm-test",
            "/bin/sh",
            ["-c", "trap 'exit 23' TERM; while :; do sleep 0.1; done"]));

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await worker.StopAsync(TimeSpan.FromSeconds(3));

        Assert.True(worker.HasExited);
        Assert.Equal(23, worker.ExitCode);
    }

    /// <summary>Verifies a worker is terminated when it ignores SIGTERM.</summary>
    [LinuxFact]
    public async Task WorkerProcess_StopAsync_KillsWorkerThatIgnoresSigterm()
    {
        using var worker = new LocalWorkerLauncher(new SigTermStopStrategy()).Launch(new WorkerLaunchOptions(
            "sigterm-ignore-test",
            "/bin/sh",
            ["-c", "trap '' TERM; while :; do sleep 1; done"]));

        await worker.StopAsync(TimeSpan.FromMilliseconds(150));

        Assert.True(worker.HasExited);
    }

    /// <summary>Verifies a crashed worker restarts and its stale socket is removed.</summary>
    [LinuxFact]
    public async Task WorkerManager_CleansSocketAfterCrashAndRestartsAfterSignals()
    {
        var directory = Directory.CreateTempSubdirectory("worker-supervisor-test-");
        var rendererPath = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.SvgRenderer.dll");
        var cachePath = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.ImageCache.dll");
        var databasePath = Path.Combine(directory.FullName, "cache.db");
        var settings = new Dictionary<string, string?>
        {
            ["Workers:SocketDirectory"] = directory.FullName,
            ["Workers:StartupTimeoutSeconds"] = "15",
            ["Workers:ShutdownGracePeriod"] = "5",
            ["Workers:SvgRenderer:Arguments:0"] = rendererPath,
            ["Workers:ImageCache:Arguments:0"] = cachePath,
            ["ImageCache:DatabasePath"] = databasePath
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var transport = new UnixSocketTransport();
        var manager = new WorkerManager(
            new LocalWorkerLauncher(new SigTermStopStrategy()),
            transport,
            TestHostModuleCatalog.Create(),
            configuration,
            NullLogger<WorkerManager>.Instance,
            new RequestDrainTracker());
        using var clients = new WorkerClientTestServices(manager, transport, configuration);

        try
        {
            await manager.StartAsync(CancellationToken.None);
            var renderer = clients.GetRequiredService<IRendererClient>();
            var settingsForRecovery = new GridSettings(2, GridPattern.Checkerboard);
            Assert.Contains("<svg", await renderer.RenderSvgAsync(settingsForRecovery, CancellationToken.None));

            var originalProcessId = Assert.Single(manager.GetStatus(), status => status.Name == "svg-renderer").ProcessId;
            Assert.NotNull(originalProcessId);
            var rendererSocket = Path.Combine(directory.FullName, "svg-renderer.sock");

            var burst = Enumerable.Range(0, 20).Select(async _ =>
            {
                try
                {
                    await renderer.RenderSvgAsync(settingsForRecovery, CancellationToken.None);
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }
            }).ToArray();
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            Assert.True(LinuxSignals.Send(originalProcessId.Value, LinuxSignals.SigKill));
            await Task.WhenAll(burst);

            var crashTimeout = Stopwatch.StartNew();
            var staleSocketRemoved = false;
            while (crashTimeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                var status = Assert.Single(manager.GetStatus(), item => item.Name == "svg-renderer");
                if (status.State == "Restarting" && !File.Exists(rendererSocket))
                {
                    staleSocketRemoved = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            Assert.True(staleSocketRemoved, "Host should remove the stale socket before restarting the crashed worker.");

            var timeout = Stopwatch.StartNew();
            WorkerStatus rendererStatus;
            do
            {
                rendererStatus = Assert.Single(manager.GetStatus(), status => status.Name == "svg-renderer");
                if (rendererStatus.RestartCount > 0 && rendererStatus.ProcessId is not null && rendererStatus.State == "Running")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (timeout.Elapsed < TimeSpan.FromSeconds(15));

            Assert.Equal("Running", rendererStatus.State);
            Assert.Equal(1, rendererStatus.RestartCount);
            Assert.NotEqual(originalProcessId, rendererStatus.ProcessId);
            var recoveredResponses = await Task.WhenAll(
                Enumerable.Range(0, 20)
                    .Select(_ => renderer.RenderSvgAsync(settingsForRecovery, CancellationToken.None)));
            Assert.All(recoveredResponses, svg => Assert.Contains("<svg", svg));

            var restartedProcessId = rendererStatus.ProcessId;
            Assert.NotNull(restartedProcessId);
            Assert.True(LinuxSignals.Send(restartedProcessId.Value, LinuxSignals.SigTerm));

            timeout.Restart();
            do
            {
                rendererStatus = Assert.Single(manager.GetStatus(), status => status.Name == "svg-renderer");
                if (rendererStatus.RestartCount > 1 && rendererStatus.ProcessId is not null && rendererStatus.State == "Running")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (timeout.Elapsed < TimeSpan.FromSeconds(15));

            Assert.Equal("Running", rendererStatus.State);
            Assert.Equal(2, rendererStatus.RestartCount);
            Assert.NotEqual(restartedProcessId, rendererStatus.ProcessId);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies the circuit opens at its crash threshold and reset restarts the worker.</summary>
    [LinuxFact]
    public async Task WorkerManager_OpensCircuitAfterCrashThresholdAndRestartsAfterReset()
    {
        var directory = Directory.CreateTempSubdirectory("worker-circuit-test-");
        var rendererPath = Path.Combine(AppContext.BaseDirectory, "ModularMonolith.Worker.SvgRenderer.dll");
        var settings = new Dictionary<string, string?>
        {
            ["Workers:SocketDirectory"] = directory.FullName,
            ["Workers:StartupTimeoutSeconds"] = "15",
            ["Workers:ShutdownGracePeriod"] = "5",
            ["Workers:CircuitBreaker:MaxCrashes"] = "2",
            ["Workers:CircuitBreaker:WindowSeconds"] = "120",
            ["Workers:SvgRenderer:Arguments:0"] = rendererPath
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var manager = new WorkerManager(
            new LocalWorkerLauncher(new SigTermStopStrategy()),
            new UnixSocketTransport(),
            TestHostModuleCatalog.Create(ModuleCatalog.SvgRenderer),
            configuration,
            NullLogger<WorkerManager>.Instance,
            new RequestDrainTracker());

        try
        {
            await manager.StartAsync(CancellationToken.None);
            Assert.False(manager.TryResetWorker(ModuleCatalog.SvgRenderer));
            await CrashWorkerAsync(manager, restartCount: 1, expectedState: "Running");
            await CrashWorkerAsync(manager, restartCount: 2, expectedState: "Open");

            var openStatus = Assert.Single(manager.GetStatus());
            Assert.Equal("Open", openStatus.State);
            Assert.Null(openStatus.ProcessId);
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.Equal(openStatus, Assert.Single(manager.GetStatus()));

            Assert.True(manager.TryResetWorker(ModuleCatalog.SvgRenderer));
            var restarted = await WaitForWorkerStateAsync(manager, "Running", TimeSpan.FromSeconds(15));
            Assert.Equal(2, restarted.RestartCount);
            Assert.NotNull(restarted.ProcessId);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    private static async Task CrashWorkerAsync(WorkerManager manager, int restartCount, string expectedState)
    {
        var processId = Assert.Single(manager.GetStatus()).ProcessId;
        Assert.NotNull(processId);
        Assert.True(LinuxSignals.Send(processId.Value, LinuxSignals.SigKill));

        var timeout = Stopwatch.StartNew();
        do
        {
            var status = Assert.Single(manager.GetStatus());
            if (status.RestartCount >= restartCount && status.State == expectedState)
            {
                if (expectedState == "Open")
                {
                    Assert.Null(status.ProcessId);
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (timeout.Elapsed < TimeSpan.FromSeconds(15));

        Assert.Fail($"Worker did not reach state '{expectedState}' after crash {restartCount}.");
    }

    private static async Task<WorkerStatus> WaitForWorkerStateAsync(
        WorkerManager manager,
        string expectedState,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            var status = Assert.Single(manager.GetStatus());
            if (status.State == expectedState)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (stopwatch.Elapsed < timeout);

        Assert.Fail($"Worker did not reach state '{expectedState}'.");
        throw new UnreachableException();
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FillAt(string svg, int row, int col, int dimension)
    {
        var tileSize = 300 / dimension;
        var prefix = $"  <rect x=\"{col * tileSize}\" y=\"{row * tileSize}\" ";
        var line = svg.Split(Environment.NewLine).Single(item => item.StartsWith(prefix, StringComparison.Ordinal));
        const string marker = "fill=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return line[start..line.IndexOf('"', start)];
    }

    private sealed class FakeImageCacheClient(byte[]? cached) : IImageCacheClient
    {
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnWrite { get; init; }
        public int PutCount { get; private set; }
        public string? LastPutKey { get; private set; }
        public byte[]? LastPutContent { get; private set; }

        /// <summary>Returns the cached test value or a simulated cache failure.</summary>
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
            => ThrowOnRead
                ? Task.FromException<byte[]?>(new HttpRequestException("cache unavailable"))
                : Task.FromResult(cached);

        /// <summary>Records a cached test value or returns a simulated cache failure.</summary>
        public Task PutAsync(string key, byte[] svg, CancellationToken cancellationToken)
        {
            PutCount++;
            LastPutKey = key;
            LastPutContent = svg;
            return ThrowOnWrite ? Task.FromException(new HttpRequestException("cache unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class FakeRendererClient : IRendererClient
    {
        public string Svg { get; init; } = "<svg />";
        public int RenderCount { get; private set; }

        /// <summary>Returns the configured test SVG and counts render calls.</summary>
        public Task<string> RenderSvgAsync(GridSettings settings, CancellationToken cancellationToken)
        {
            RenderCount++;
            return Task.FromResult(Svg);
        }

        /// <summary>Returns a simple test page containing the requested SVG.</summary>
        public Task<string> RenderPageAsync(RenderPageRequest request, CancellationToken cancellationToken)
            => Task.FromResult($"<html>{request.SvgContent}</html>");
    }
}
