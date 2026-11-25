using System.Net;

namespace ModularMonolith.Host;

internal sealed record ManagementListenerSettings(string Address, int Port, string Url)
{
    internal static ManagementListenerSettings Create(
        IConfiguration configuration,
        string? publicUrls,
        IEnumerable<string>? configuredEndpointUrls = null)
    {
        var address = configuration.GetValue("Management:ListenAddress", "127.0.0.1");
        var port = configuration.GetValue("Management:Port", 5228);
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            throw new InvalidOperationException("Management:ListenAddress must be an IP address.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Management:Port must be between 1 and 65535.");
        }

        var publicPorts = (publicUrls?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Concat(configuredEndpointUrls ?? [])
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : -1);
        if (publicPorts.Contains(port))
        {
            throw new InvalidOperationException("Management:Port must be different from every public Host listener port.");
        }

        var url = new UriBuilder(Uri.UriSchemeHttp, ipAddress.ToString(), port)
            .Uri.GetLeftPart(UriPartial.Authority);
        return new ManagementListenerSettings(ipAddress.ToString(), port, url);
    }

    internal bool IsManagementRequest(HttpContext context)
        => context.Connection.LocalPort == Port;
}
