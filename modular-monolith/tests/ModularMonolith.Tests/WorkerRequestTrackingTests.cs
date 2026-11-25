using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;
using ModularMonolith.Host;
using ModularMonolith.Host.Modules;

namespace ModularMonolith.Tests;

public sealed class WorkerRequestTrackingTests
{
    /// <summary>Verifies two correlated worker crashes quarantine and store a request.</summary>
    [Fact]
    public void WorkerRequestTracker_QuarantinesFingerprintAfterTwoCorrelatedCrashes()
    {
        var tracker = CreateTracker();
        var first = Request("fingerprint-a", [1, 2, 3]);
        var second = Request("fingerprint-a", [1, 2, 3]);

        using (tracker.Track("alpha", first))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        using (tracker.Track("alpha", second))
        {
            Assert.True(tracker.RecordWorkerCrash("alpha"));
            Assert.True(second.IsPoisoned);
        }

        Assert.True(tracker.IsQuarantined("alpha", first.Fingerprint));
        var record = Assert.Single(tracker.GetDeadLetters("alpha"));
        Assert.Equal(3, record.PayloadBytes);
        Assert.Equal([1, 2, 3], tracker.GetDeadLetter("alpha", record.Id)!.Body);
    }

    /// <summary>Verifies a worker response clears prior crash evidence for its fingerprint.</summary>
    [Fact]
    public void WorkerRequestTracker_ResponseClearsPreQuarantineCrashEvidence()
    {
        var tracker = CreateTracker();
        var first = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", first))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        var successful = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", successful))
        {
            tracker.RecordWorkerResponse("alpha", successful);
        }

        var next = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", next))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        Assert.False(tracker.IsQuarantined("alpha", first.Fingerprint));
    }

    /// <summary>Verifies ambiguous active requests are not attributed to a worker crash.</summary>
    [Fact]
    public void WorkerRequestTracker_DoesNotAttributeAnAmbiguousCrash()
    {
        var tracker = CreateTracker();
        var first = Request("fingerprint-a", [1]);
        var second = Request("fingerprint-b", [2]);
        using (tracker.Track("alpha", first))
        using (tracker.Track("alpha", second))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        var third = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", third))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }
    }

    /// <summary>Verifies crash evidence expires after the correlation window.</summary>
    [Fact]
    public void WorkerRequestTracker_ExpiresCrashEvidenceAfterTheCorrelationWindow()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var tracker = CreateTracker(clock);
        var first = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", first))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        var second = Request("fingerprint-a", [1]);
        using (tracker.Track("alpha", second))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        Assert.False(tracker.IsQuarantined("alpha", first.Fingerprint));
    }

    /// <summary>Verifies FIFO eviction removes the oldest dead letter and quarantine.</summary>
    [Fact]
    public void WorkerRequestTracker_EvictsOldestDeadLetterAndItsQuarantine()
    {
        var tracker = CreateTracker();
        string? firstFingerprint = null;
        string? lastFingerprint = null;

        for (var index = 0; index <= 100; index++)
        {
            var fingerprint = $"fingerprint-{index}";
            var first = Request(fingerprint, [1]);
            using (tracker.Track("alpha", first))
            {
                Assert.False(tracker.RecordWorkerCrash("alpha"));
            }

            var second = Request(fingerprint, [1]);
            using (tracker.Track("alpha", second))
            {
                Assert.True(tracker.RecordWorkerCrash("alpha"));
            }

            firstFingerprint ??= fingerprint;
            lastFingerprint = fingerprint;
        }

        Assert.Equal(100, tracker.GetDeadLetters("alpha").Count);
        Assert.False(tracker.IsQuarantined("alpha", firstFingerprint!));
        Assert.True(tracker.IsQuarantined("alpha", lastFingerprint!));
    }

    /// <summary>Verifies query ordering is canonical and buffered request bodies are replayed.</summary>
    [Fact]
    public async Task FingerprintMiddleware_CanonicalizesQueryAndReplaysBufferedBody()
    {
        var tracker = CreateTracker();
        var fingerprints = new List<string>();
        var middleware = CreateMiddleware(
            tracker,
            ("Workers:Alpha:MaxRequestBodyBytes", "10"),
            async context =>
            {
                var request = Assert.IsType<WorkerRequestContext>(context.Items[WorkerRequestContext.ItemKey]);
                fingerprints.Add(request.Fingerprint);
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                Assert.Equal("payload", await reader.ReadToEndAsync());
            });

        await InvokeFingerprintMiddlewareAsync(middleware, "/alpha/item", "?b=2&a=1", "payload");
        await InvokeFingerprintMiddlewareAsync(middleware, "/alpha/item", "?a=1&b=2", "payload");

        Assert.Equal(fingerprints[0], fingerprints[1]);
    }

    /// <summary>Verifies equivalent escaped paths produce the same request fingerprint.</summary>
    [Fact]
    public async Task FingerprintMiddleware_CanonicalizesEquivalentEscapedPaths()
    {
        var tracker = CreateTracker();
        var fingerprints = new List<string>();
        var middleware = CreateMiddleware(
            tracker,
            ("Workers:Alpha:MaxRequestBodyBytes", "10"),
            context =>
            {
                var request = Assert.IsType<WorkerRequestContext>(context.Items[WorkerRequestContext.ItemKey]);
                fingerprints.Add(request.Fingerprint);
                return Task.CompletedTask;
            });
        var escapedPath = CreateRequest("/alpha/caf%C3%A9", string.Empty, "payload");
        escapedPath.Request.Path = PathString.FromUriComponent("/alpha/caf%C3%A9");
        var unicodePath = CreateRequest("/alpha/café", string.Empty, "payload");

        await middleware.InvokeAsync(escapedPath);
        await middleware.InvokeAsync(unicodePath);

        Assert.Equal(fingerprints[0], fingerprints[1]);
    }

    /// <summary>Verifies oversized request bodies are rejected before worker dispatch.</summary>
    [Fact]
    public async Task FingerprintMiddleware_RejectsRequestAboveConfiguredBodyLimit()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateTracker(),
            ("Workers:Alpha:MaxRequestBodyBytes", "3"),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateRequest("/alpha/item", string.Empty, "large");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    /// <summary>Verifies quarantined proxy requests are rejected before forwarding.</summary>
    [Fact]
    public async Task FingerprintMiddleware_RejectsAQuarantinedProxyRequestBeforeForwarding()
    {
        var tracker = CreateTracker();
        WorkerRequestContext? observed = null;
        var firstMiddleware = CreateMiddleware(
            tracker,
            ("Workers:Alpha:MaxRequestBodyBytes", "20"),
            context =>
            {
                observed = Assert.IsType<WorkerRequestContext>(context.Items[WorkerRequestContext.ItemKey]);
                return Task.CompletedTask;
            });
        await InvokeFingerprintMiddlewareAsync(firstMiddleware, "/alpha/item", "?x=1", "payload");

        using (tracker.Track("alpha", Request(observed!.Fingerprint, Encoding.UTF8.GetBytes("payload"))))
        {
            Assert.False(tracker.RecordWorkerCrash("alpha"));
        }

        using (tracker.Track("alpha", Request(observed.Fingerprint, Encoding.UTF8.GetBytes("payload"))))
        {
            Assert.True(tracker.RecordWorkerCrash("alpha"));
        }

        var nextCalled = false;
        var rejectingMiddleware = CreateMiddleware(
            tracker,
            ("Workers:Alpha:MaxRequestBodyBytes", "20"),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateRequest("/alpha/item", "?x=1", "payload");
        context.Response.Body = new MemoryStream();

        await rejectingMiddleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    private static WorkerRequestTracker CreateTracker(TestTimeProvider? clock = null)
        => new(Configuration(("Workers:CircuitBreaker:WindowSeconds", "120")), clock);

    private static WorkerRequestFingerprintMiddleware CreateMiddleware(
        WorkerRequestTracker tracker,
        (string Key, string Value) limit,
        RequestDelegate next)
        => new(
            next,
            new HostModuleCatalog([
                new HostModuleDescriptor(
                    "alpha",
                    "Alpha.Worker",
                    "Alpha",
                    new JobLimits(),
                    [new WorkerProxyRouteDescriptor("/alpha/{**path}", "/alpha/{**path}")],
                    _ => [])
            ]),
            Configuration(limit),
            tracker);

    private static async Task InvokeFingerprintMiddlewareAsync(
        WorkerRequestFingerprintMiddleware middleware,
        string path,
        string query,
        string body)
        => await middleware.InvokeAsync(CreateRequest(path, query, body));

    private static DefaultHttpContext CreateRequest(string path, string query, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "text/plain";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static WorkerRequestContext Request(string fingerprint, byte[] body)
        => new(fingerprint, "POST", "/alpha/item", "x=1", "text/plain", body);

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        /// <summary>Returns the current test timestamp.</summary>
        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>Advances the test clock by the requested duration.</summary>
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
