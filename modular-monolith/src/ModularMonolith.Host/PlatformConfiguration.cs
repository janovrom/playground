using Microsoft.Extensions.Configuration.Json;

namespace ModularMonolith.Host;

internal static class PlatformConfiguration
{
    /// <summary>
    /// Adds <c>appsettings.{platform}.json</c> directly after <c>appsettings.json</c>, so the environment file, environment variables
    /// and the command line still override platform defaults.
    /// </summary>
    public static T AddPlatformSettings<T>(this T configuration, string platformName)
        where T : IConfigurationBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformName);

        var sources = configuration.Sources;
        var baseIndex = -1;
        for (var index = 0; index < sources.Count; index++)
        {
            if (sources[index] is JsonConfigurationSource { Path: "appsettings.json" })
            {
                baseIndex = index;
                break;
            }
        }

        configuration.AddJsonFile($"appsettings.{platformName}.json", optional: true, reloadOnChange: true);
        if (baseIndex >= 0 && baseIndex + 1 < sources.Count - 1)
        {
            var platformSource = sources[^1];
            sources.RemoveAt(sources.Count - 1);
            sources.Insert(baseIndex + 1, platformSource);
        }

        return configuration;
    }
}
