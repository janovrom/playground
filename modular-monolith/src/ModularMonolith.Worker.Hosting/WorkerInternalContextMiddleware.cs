using Microsoft.AspNetCore.Http;
using ModularMonolith.Platform;

namespace ModularMonolith.Worker.Hosting;

internal sealed class WorkerInternalContextMiddleware(
    RequestDelegate next,
    WorkerInternalContext contextCodec)
{
    /// <summary>Validates Host-signed internal request context and restores the worker principal.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/health")
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(
                WorkerInternalContextConstants.HeaderName,
                out var header)
            || header.Count != 1)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        try
        {
            context.User = contextCodec.Decode(header[0]!);
        }
        catch (WorkerInternalContextException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
