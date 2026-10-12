namespace ModularMonolith.Platform.Transport;

public static class WorkerTransports
{
    public const string Auto = "Auto";

    /// <summary>Resolves a transport by name; <c>Auto</c> or an empty value selects named pipes on Windows and Unix sockets elsewhere.</summary>
    public static IWorkerTransport Resolve(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.Equals(kind, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return OperatingSystem.IsWindows() ? new NamedPipeTransport() : new UnixSocketTransport();
        }

        if (string.Equals(kind, UnixSocketTransport.TransportName, StringComparison.OrdinalIgnoreCase))
        {
            return new UnixSocketTransport();
        }

        if (string.Equals(kind, NamedPipeTransport.TransportName, StringComparison.OrdinalIgnoreCase))
        {
            return new NamedPipeTransport();
        }

        throw new InvalidOperationException($"Unknown worker transport '{kind}'. Expected Auto, {UnixSocketTransport.TransportName} or {NamedPipeTransport.TransportName}.");
    }
}
