using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Contracts;
using ModularMonolith.Host;
using ModularMonolith.Linux;
using ModularMonolith.Worker.ImageCache;

namespace ModularMonolith.Tests;

public sealed class GridModuleTests
{
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

    [Fact]
    public void CacheKey_IsBasedOnNormalizedSettings()
    {
        var first = GridCacheKey.Create(GridRequestParser.Parse("not-a-pattern", 100));
        var equivalent = GridCacheKey.Create(GridRequestParser.Parse(null, 20));

        Assert.Equal("grid-v1-checkerboard-20", first);
        Assert.Equal(equivalent, first);
        Assert.Equal("grid-v1-radial-6", GridCacheKey.Create(new GridSettings(6, GridPattern.Radial)));
    }

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

    [Fact]
    public void RenderSvg_RadialPatternUsesRadialPalette()
    {
        var svg = GridImageGenerator.RenderSvg(new GridSettings(4, GridPattern.Radial));

        Assert.Contains("fill=\"#10b981\"", svg);
        Assert.Contains("fill=\"#0f172a\"", svg);
    }

    [Fact]
    public void RenderPage_EmbedsSvgAndUsesTheSelectedSettings()
    {
        var settings = new GridSettings(4, GridPattern.Radial);
        var page = ViewRenderer.RenderPage(settings, "<svg id=\"cached\"></svg>");

        Assert.Contains("<h2>Pattern: Radial</h2>", page);
        Assert.Contains("Grid Dimensions: 4x4", page);
        Assert.Contains("<svg id=\"cached\"></svg>", page);
    }

    [Theory]
    [InlineData("valid-key_123.ext", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("a/b", false)]
    [InlineData("contains space", false)]
    public void ImageCacheKey_ValidatesRouteSafeKeys(string key, bool expected)
        => Assert.Equal(expected, ImageCacheKey.IsValid(key));

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

    [Fact]
    public async Task WorkerProcess_StopAsync_AllowsSigtermHandlerToExit()
    {
        using var worker = new LocalWorkerLauncher().Launch(new WorkerLaunchOptions(
            "sigterm-test",
            "/bin/sh",
            ["-c", "trap 'exit 23' TERM; while :; do sleep 0.1; done"]));

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await worker.StopAsync(TimeSpan.FromSeconds(3));

        Assert.True(worker.HasExited);
        Assert.Equal(23, worker.ExitCode);
    }

    [Fact]
    public async Task WorkerProcess_StopAsync_KillsWorkerThatIgnoresSigterm()
    {
        using var worker = new LocalWorkerLauncher().Launch(new WorkerLaunchOptions(
            "sigterm-ignore-test",
            "/bin/sh",
            ["-c", "trap '' TERM; while :; do sleep 1; done"]));

        await worker.StopAsync(TimeSpan.FromMilliseconds(150));

        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task WorkerManager_CleansSocketAfterCrashAndRestartsAfterSignals()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
        var manager = new WorkerManager(
            new LocalWorkerLauncher(),
            configuration,
            NullLogger<WorkerManager>.Instance,
            new RequestDrainTracker());

        try
        {
            await manager.StartAsync(CancellationToken.None);
            var originalProcessId = Assert.Single(manager.GetStatus(), status => status.Name == "svg-renderer").ProcessId;
            Assert.NotNull(originalProcessId);
            var rendererSocket = Path.Combine(directory.FullName, "svg-renderer.sock");
            Assert.True(LinuxSignals.Send(originalProcessId.Value, LinuxSignals.SigKill));

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

    private sealed class FakeImageCacheClient(byte[]? cached) : IImageCacheClient
    {
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnWrite { get; init; }
        public int PutCount { get; private set; }
        public string? LastPutKey { get; private set; }
        public byte[]? LastPutContent { get; private set; }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
            => ThrowOnRead
                ? Task.FromException<byte[]?>(new HttpRequestException("cache unavailable"))
                : Task.FromResult(cached);

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

        public Task<string> RenderSvgAsync(GridSettings settings, CancellationToken cancellationToken)
        {
            RenderCount++;
            return Task.FromResult(Svg);
        }

        public Task<string> RenderPageAsync(GridSettings settings, string svg, CancellationToken cancellationToken)
            => Task.FromResult($"<html>{svg}</html>");
    }
}
