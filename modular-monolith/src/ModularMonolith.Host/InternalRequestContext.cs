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

internal sealed class WorkerProxyFailureMiddleware(RequestDelegate next)
{
    /// <summary>Writes a stable problem response when YARP has no available destination.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (context.Response.StatusCode != StatusCodes.Status503ServiceUnavailable
            || context.Response.HasStarted)
        {
            return;
        }

        context.Response.ContentType = "application/problem+json";
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new WorkerProxyProblemDetails(
                "https://www.rfc-editor.org/rfc/rfc9110#name-503-service-unavailable",
                "Worker unavailable",
                StatusCodes.Status503ServiceUnavailable,
                "The worker module is unavailable."),
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
