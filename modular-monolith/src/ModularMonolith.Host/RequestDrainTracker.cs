namespace ModularMonolith.Host;

internal sealed class RequestDrainTracker
{
    private readonly object _lock = new();
    private TaskCompletionSource _drained = CompletedSource();
    private int _activeRequests;
    private bool _stopping;

    public bool TryBeginRequest()
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return false;
            }

            if (_activeRequests++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return true;
        }
    }

    public void EndRequest()
    {
        lock (_lock)
        {
            if (--_activeRequests == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    public void BeginDrain()
    {
        lock (_lock)
        {
            _stopping = true;
            if (_activeRequests == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken)
        => _drained.Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

internal sealed class RequestDrainMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestDrainTracker tracker)
    {
        if (!tracker.TryBeginRequest())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            tracker.EndRequest();
        }
    }
}
