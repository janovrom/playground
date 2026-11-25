using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Host;

namespace ModularMonolith.Tests;

public sealed class ManagementListenerTests
{
    /// <summary>Verifies the management listener uses its loopback default.</summary>
    [Fact]
    public void ManagementListener_DefaultsToLoopbackOnPort5228()
    {
        var settings = ManagementListenerSettings.Create(Configuration(), "http://127.0.0.1:5227");

        Assert.Equal("127.0.0.1", settings.Address);
        Assert.Equal(5228, settings.Port);
        Assert.Equal("http://127.0.0.1:5228", settings.Url);
    }

    /// <summary>Verifies management and public listeners cannot share a port.</summary>
    [Fact]
    public void ManagementListener_RejectsPublicPortCollision()
    {
        var configuration = Configuration(("Management:Port", "5227"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => ManagementListenerSettings.Create(configuration, "http://127.0.0.1:5227"));

        Assert.Contains("different from every public Host listener port", exception.Message);
    }

    /// <summary>Verifies management and configured Kestrel endpoints cannot share a port.</summary>
    [Fact]
    public void ManagementListener_RejectsConfiguredKestrelEndpointCollision()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ManagementListenerSettings.Create(
                Configuration(),
                "http://127.0.0.1:5227",
                ["http://127.0.0.1:5228"]));

        Assert.Contains("different from every public Host listener port", exception.Message);
    }

    /// <summary>Verifies management routes accept only requests on the configured local port.</summary>
    [Fact]
    public void ManagementListener_OnlyAcceptsRequestsOnItsConfiguredPort()
    {
        var settings = ManagementListenerSettings.Create(Configuration(), null);
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5227;

        Assert.False(settings.IsManagementRequest(context));

        context.Connection.LocalPort = 5228;

        Assert.True(settings.IsManagementRequest(context));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
