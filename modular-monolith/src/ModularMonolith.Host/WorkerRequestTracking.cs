using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using ModularMonolith.Contracts;
using ModularMonolith.Host.Modules;

namespace ModularMonolith.Host;

internal sealed class WorkerRequestContext(
    string fingerprint,
    string method,
    string path,
    string query,
    string? contentType,
    byte[] body)
{
    public static readonly object ItemKey = new();

    public string Fingerprint { get; } = fingerprint;
    public string Method { get; } = method;
    public string Path { get; } = path;
    public string Query { get; } = query;
    public string? ContentType { get; } = contentType;
    public byte[] Body { get; } = body;
    public string? ProxyModule { get; set; }
    public bool IsPoisoned => Volatile.Read(ref _isPoisoned) != 0;

    private int _isPoisoned;

    /// <summary>Marks this request as correlated with a quarantined worker crash.</summary>
    public void MarkPoisoned() => Interlocked.Exchange(ref _isPoisoned, 1);
}

internal sealed record DeadLetterMetadata(
    Guid Id,
    string Module,
    string Fingerprint,
    string Method,
    string Path,
    string Query,
    string? ContentType,
    int PayloadBytes,
    DateTimeOffset CapturedAt);

internal sealed record DeadLetterPayload(DeadLetterMetadata Metadata, byte[] Body);

internal sealed class WorkerRequestTracker
{
    private const int MaximumRecords = 100;
    private const int MaximumPayloadBytes = 10 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _correlationWindow;
    private readonly Dictionary<string, HashSet<WorkerRequestContext>> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Module, string Fingerprint), DateTimeOffset> _lastCrash = [];
    private readonly LinkedList<DeadLetterPayload> _records = [];
    private readonly HashSet<(string Module, string Fingerprint)> _quarantined = [];
    private int _payloadBytes;

    /// <summary>Creates a tracker with the configured poison-correlation window.</summary>
    public WorkerRequestTracker(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var windowSeconds = configuration.GetValue("Workers:CircuitBreaker:WindowSeconds", 120);
        if (windowSeconds <= 0)
        {
            throw new InvalidOperationException("Workers:CircuitBreaker:WindowSeconds must be greater than zero.");
        }

        _correlationWindow = TimeSpan.FromSeconds(windowSeconds);
    }

    /// <summary>Adds a request to the active set for its worker.</summary>
    public IDisposable Track(string module, WorkerRequestContext request)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(module, out var requests))
            {
                requests = new HashSet<WorkerRequestContext>();
                _active.Add(module, requests);
            }

            requests.Add(request);
        }

        return new RequestLease(this, module, request);
    }

    /// <summary>Reports whether a request fingerprint is quarantined for a worker.</summary>
    public bool IsQuarantined(string module, string fingerprint)
    {
        lock (_gate)
        {
            return _quarantined.Contains((module, fingerprint));
        }
    }

    /// <summary>Associates an unexpected worker exit with its unique active request, when possible.</summary>
    public bool RecordWorkerCrash(string module)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var expired = _lastCrash
                .Where(entry => now >= entry.Value && now - entry.Value > _correlationWindow)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (var expiredKey in expired)
            {
                _lastCrash.Remove(expiredKey);
            }

            if (!_active.TryGetValue(module, out var requests))
            {
                return false;
            }

            var fingerprints = requests
                .Select(request => request.Fingerprint)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (fingerprints.Length != 1)
            {
                return false;
            }

            var request = requests.First(activeRequest => activeRequest.Fingerprint == fingerprints[0]);
            var key = (module, request.Fingerprint);
            if (_lastCrash.TryGetValue(key, out var previous)
                && now >= previous
                && now - previous <= _correlationWindow)
            {
                _lastCrash.Remove(key);
                AddDeadLetter(module, request, now);
                foreach (var activeRequest in requests)
                {
                    if (activeRequest.Fingerprint == request.Fingerprint)
                    {
                        activeRequest.MarkPoisoned();
                    }
                }

                return true;
            }

            _lastCrash[key] = now;
            return false;
        }
    }

    /// <summary>Clears pre-quarantine crash evidence after the worker returns a response.</summary>
    public void RecordWorkerResponse(string module, WorkerRequestContext request)
    {
        lock (_gate)
        {
            _lastCrash.Remove((module, request.Fingerprint));
        }
    }

    /// <summary>Returns dead-letter metadata without exposing stored request bodies.</summary>
    public IReadOnlyList<DeadLetterMetadata> GetDeadLetters(string module)
    {
        lock (_gate)
        {
            return _records
                .Where(record => record.Metadata.Module == module)
                .Select(record => record.Metadata)
                .ToArray();
        }
    }

    /// <summary>Returns a copy of a quarantined request body when its record exists.</summary>
    public DeadLetterPayload? GetDeadLetter(string module, Guid id)
    {
        lock (_gate)
        {
            var record = _records.FirstOrDefault(item =>
                item.Metadata.Module == module && item.Metadata.Id == id);
            return record is null
                ? null
                : new DeadLetterPayload(record.Metadata, record.Body.ToArray());
        }
    }

    private void AddDeadLetter(string module, WorkerRequestContext request, DateTimeOffset now)
    {
        var key = (module, request.Fingerprint);
        if (!_quarantined.Add(key))
        {
            return;
        }

        while (_records.Count >= MaximumRecords || _payloadBytes + request.Body.Length > MaximumPayloadBytes)
        {
            EvictOldest();
        }

        var body = request.Body.ToArray();
        var metadata = new DeadLetterMetadata(
            Guid.NewGuid(),
            module,
            request.Fingerprint,
            request.Method,
            request.Path,
            request.Query,
            request.ContentType,
            body.Length,
            now);
        _records.AddLast(new DeadLetterPayload(metadata, body));
        _payloadBytes += body.Length;
    }

    private void EvictOldest()
    {
        var oldest = _records.First;
        if (oldest is null)
        {
            return;
        }

        _records.RemoveFirst();
        _payloadBytes -= oldest.Value.Body.Length;
        var key = (oldest.Value.Metadata.Module, oldest.Value.Metadata.Fingerprint);
        _quarantined.Remove(key);
        _lastCrash.Remove(key);
    }

    private void EndTracking(string module, WorkerRequestContext request)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(module, out var requests))
            {
                requests.Remove(request);
                if (requests.Count == 0)
                {
                    _active.Remove(module);
                }
            }
        }
    }

    private sealed class RequestLease(WorkerRequestTracker owner, string module, WorkerRequestContext request) : IDisposable
    {
        private int _disposed;

        /// <summary>Removes this request from the worker's active set.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndTracking(module, request);
            }
        }
    }
}

internal sealed class WorkerRequestFingerprintMiddleware
{
    private const int MaximumConfiguredBodyBytes = 1024 * 1024;
    private const int DefaultMaximumBodyBytes = 1024 * 1024;
    private readonly RequestDelegate _next;
    private readonly WorkerRequestTracker _tracker;
    private readonly IReadOnlyList<ProxyRoute> _proxyRoutes;
    private readonly IReadOnlyDictionary<string, int> _bodyLimits;
    private readonly string[] _localWorkerModules;

    /// <summary>Creates request tracking from the registered module routes and body limits.</summary>
    public WorkerRequestFingerprintMiddleware(
        RequestDelegate next,
        IHostModuleCatalog modules,
        IConfiguration configuration,
        WorkerRequestTracker tracker)
    {
        _next = next;
        _tracker = tracker;
        _proxyRoutes = modules.Modules
            .SelectMany(module => module.ProxyRoutes.Select(route => new ProxyRoute(
                module.Name,
                RoutePatternFactory.Parse(route.PublicPath))))
            .ToArray();
        _bodyLimits = modules.Modules.ToDictionary(
            module => module.Name,
            module => ReadBodyLimit(configuration, module.ConfigSection),
            StringComparer.Ordinal);
        _localWorkerModules = modules.Modules
            .Where(module => module.Name is ModuleCatalog.ImageCache or ModuleCatalog.SvgRenderer)
            .Select(module => module.Name)
            .ToArray();
    }

    /// <summary>Buffers, fingerprints, and tracks requests that can invoke worker modules.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var proxyModule = FindProxyModule(context.Request.Path);
        var localRoute = context.Request.Path == "/" || context.Request.Path == "/grid.svg";
        var modules = proxyModule is not null
            ? [proxyModule]
            : localRoute ? _localWorkerModules : [];
        if (modules.Length == 0)
        {
            await _next(context);
            return;
        }

        var maximumBodyBytes = modules.Min(module => _bodyLimits[module]);
        var body = await ReadBodyAsync(context, maximumBodyBytes, context.RequestAborted);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var originalBody = context.Request.Body;
        await using var replayBody = new MemoryStream(body, writable: false);
        context.Request.Body = replayBody;
        var request = CreateRequestContext(context.Request, body);
        context.Items[WorkerRequestContext.ItemKey] = request;

        try
        {
            if (modules.Any(module => _tracker.IsQuarantined(module, request.Fingerprint)))
            {
                await WritePoisonResponseAsync(context);
                return;
            }

            request.ProxyModule = proxyModule;
            if (proxyModule is null)
            {
                await _next(context);
                return;
            }

            using var lease = _tracker.Track(proxyModule, request);
            await _next(context);
        }
        finally
        {
            context.Request.Body = originalBody;
            context.Items.Remove(WorkerRequestContext.ItemKey);
        }
    }

    internal static async Task WritePoisonResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "Poisoned request",
            status = StatusCodes.Status422UnprocessableEntity,
            detail = "This request payload has been quarantined."
        }, cancellationToken: context.RequestAborted);
    }

    private string? FindProxyModule(PathString path)
    {
        foreach (var route in _proxyRoutes)
        {
            if (Matches(route.Pattern, path))
            {
                return route.Module;
            }
        }

        return null;
    }

    private static bool Matches(RoutePattern pattern, PathString path)
    {
        var requestSegments = (path.Value ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = pattern.PathSegments;
        var requestIndex = 0;
        foreach (var segment in patternSegments)
        {
            if (segment.Parts.Count == 1
                && segment.Parts[0] is RoutePatternParameterPart { IsCatchAll: true })
            {
                return true;
            }

            if (requestIndex >= requestSegments.Length)
            {
                return false;
            }

            if (segment.Parts.All(part => part is RoutePatternLiteralPart))
            {
                var literal = string.Concat(segment.Parts.Select(part => ((RoutePatternLiteralPart)part).Content));
                if (!string.Equals(literal, requestSegments[requestIndex], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            requestIndex++;
        }

        return requestIndex == requestSegments.Length;
    }

    private static WorkerRequestContext CreateRequestContext(HttpRequest request, byte[] body)
    {
        var canonicalQuery = string.Join(
            "&",
            request.Query
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .SelectMany(item => item.Value.Count == 0
                    ? [$"{Uri.EscapeDataString(item.Key)}="]
                    : item.Value.Select(value =>
                        $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(value ?? string.Empty)}")));
        var path = request.PathBase.Add(request.Path).ToUriComponent();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, request.Method);
        AppendField(hash, path);
        AppendField(hash, canonicalQuery);
        hash.AppendData(body);
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        return new WorkerRequestContext(
            fingerprint,
            request.Method,
            path,
            canonicalQuery,
            request.ContentType,
            body);
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maximumBodyBytes, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maximumBodyBytes)
        {
            return null;
        }

        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return body.ToArray();
                }

                if (body.Length + read > maximumBodyBytes)
                {
                    return null;
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadBodyLimit(IConfiguration configuration, string configSection)
    {
        var value = configuration.GetValue(
            $"Workers:{configSection}:MaxRequestBodyBytes",
            DefaultMaximumBodyBytes);
        if (value is <= 0 or > MaximumConfiguredBodyBytes)
        {
            throw new InvalidOperationException(
                $"Workers:{configSection}:MaxRequestBodyBytes must be between 1 and {MaximumConfiguredBodyBytes}.");
        }

        return value;
    }

    private sealed record ProxyRoute(string Module, RoutePattern Pattern);
}

internal sealed class PoisonPillException(string module, string fingerprint, Exception? innerException = null)
    : Exception($"Request fingerprint '{fingerprint}' is quarantined for worker '{module}'.", innerException)
{
    public string Module { get; } = module;
    public string Fingerprint { get; } = fingerprint;
}
