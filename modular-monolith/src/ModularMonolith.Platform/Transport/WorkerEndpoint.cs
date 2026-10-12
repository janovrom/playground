namespace ModularMonolith.Platform.Transport;

/// <summary>Where a worker listens. <see cref="Address"/> is a socket path for Unix sockets and a pipe name for named pipes.</summary>
public sealed record WorkerEndpoint(string Transport, string Address);
