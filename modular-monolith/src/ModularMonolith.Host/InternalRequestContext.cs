using System.Text.Json;
using System.Text.Json.Serialization;
using ModularMonolith.Platform;

namespace ModularMonolith.Host;

internal sealed class HostInternalContextMiddleware(
    RequestDelegate next,
    WorkerInternalContext contextCodec,
    IConfiguration configuration)
{
    /// <summary>Replaces externally supplied internal context with Host-signed request context.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        context.Request.Headers.Remove(WorkerInternalContextConstants.HeaderName);
        var allowedClaimTypes = configuration
            .GetSection("Gateway:ForwardedClaimTypes")
            .Get<string[]>() ?? [];
        context.Request.Headers[WorkerInternalContextConstants.HeaderName] =
            contextCodec.Encode(context.User, allowedClaimTypes);
        await next(context);
    }
}

internal sealed class WorkerProxyFailureMiddleware(
    RequestDelegate next,
    WorkerManager? workerManager = null,
    WorkerRequestTracker? requestTracker = null)
{
    /// <summary>Writes a stable problem response when YARP has no available destination.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        var request = context.Items.TryGetValue(WorkerRequestContext.ItemKey, out var item)
            ? item as WorkerRequestContext
            : null;
        if (request?.ProxyModule is { } module && workerManager is not null && requestTracker is not null)
        {
            var forwardingFailed = context.GetForwarderErrorFeature() is not null;
            workerManager.ObserveWorkerFailure(module);
            if (request.IsPoisoned || requestTracker.IsQuarantined(module, request.Fingerprint))
            {
                if (!context.Response.HasStarted)
                {
                    await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "Poisoned request", "This request payload has been quarantined.");
                }

                return;
            }

            var workerRunning = workerManager.IsRunning(module);
            if (!forwardingFailed && workerRunning)
            {
                requestTracker.RecordWorkerResponse(module, request);
            }
            else if ((forwardingFailed || !workerRunning)
                && !context.Response.HasStarted
                && context.Response.StatusCode is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }

        if (context.Response.StatusCode != StatusCodes.Status503ServiceUnavailable || context.Response.HasStarted)
        {
            return;
        }

        await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Worker unavailable", "The worker module is unavailable.");
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new WorkerProxyProblemDetails(
                statusCode == StatusCodes.Status503ServiceUnavailable
                    ? "https://www.rfc-editor.org/rfc/rfc9110#name-503-service-unavailable"
                    : "https://www.rfc-editor.org/rfc/rfc9110#name-422-unprocessable-content",
                title,
                statusCode,
                detail),
            HostJsonContext.Default.WorkerProxyProblemDetails);
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}

internal sealed record WorkerProxyProblemDetails(
    string Type,
    string Title,
    int Status,
    string Detail);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkerProxyProblemDetails))]
internal partial class HostJsonContext : JsonSerializerContext;

internal sealed class WorkerContextPropagationHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var value = httpContextAccessor.HttpContext?.Request.Headers[WorkerInternalContextConstants.HeaderName].ToString();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("An internal worker request requires Host request context.");
        }

        request.Headers.Remove(WorkerInternalContextConstants.HeaderName);
        request.Headers.TryAddWithoutValidation(WorkerInternalContextConstants.HeaderName, value);
        return base.SendAsync(request, cancellationToken);
    }
}
