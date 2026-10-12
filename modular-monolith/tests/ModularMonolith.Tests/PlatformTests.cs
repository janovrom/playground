using Microsoft.Extensions.Configuration;
using ModularMonolith.Abstractions;
using ModularMonolith.Host;
using ModularMonolith.Linux;
using ModularMonolith.Platform;
using ModularMonolith.Platform.Transport;
using ModularMonolith.Windows;

namespace ModularMonolith.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void Name_MatchesTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";

        Assert.Equal(expected, PlatformServices.Name);
    }

    [Fact]
    public void CreateProcessGuard_SelectsThePlatformImplementation()
    {
        var guard = PlatformServices.CreateProcessGuard();

        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxProcessGuard>(guard);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsProcessGuard>(guard);
        }

        Assert.Null(Record.Exception(guard.EnsureKillOnParentExit));
    }

    [Fact]
    public void CreateStopStrategy_SelectsThePlatformImplementation()
    {
        var strategy = PlatformServices.CreateStopStrategy();

        Assert.IsType(
            OperatingSystem.IsLinux() ? typeof(SigTermStopStrategy)
                : OperatingSystem.IsWindows() ? typeof(NamedEventStopStrategy)
                : typeof(NoGracefulStopStrategy),
            strategy);
    }

    [Fact]
    public void CreateLauncher_UsesJobsUnlessDisabledOnLinux()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cgroup-root-{Guid.NewGuid():N}");

        var enabled = PlatformServices.CreateLauncher(Configuration(("Job:Root", root)));
        var disabled = PlatformServices.CreateLauncher(Configuration(("Job:Enabled", "false")));

        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<JobWorkerLauncher>(enabled);
            Assert.IsType<LocalWorkerLauncher>(disabled);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<JobWorkerLauncher>(enabled);
            Assert.IsType<JobWorkerLauncher>(disabled);
        }
        else
        {
            Assert.IsType<LocalWorkerLauncher>(enabled);
            Assert.IsType<LocalWorkerLauncher>(disabled);
        }
    }

    [Fact]
    public void ResolveTransport_ReadsTheConfiguredKind()
    {
        Assert.IsType<NamedPipeTransport>(PlatformServices.ResolveTransport(Configuration(("Workers:Transport", "NamedPipe"))));
        Assert.IsType<UnixSocketTransport>(PlatformServices.ResolveTransport(Configuration(("Workers:Transport", "UnixSocket"))));
    }

    [Fact]
    public void AddPlatformSettings_PlacesThePlatformFileBetweenBaseAndEnvironmentSettings()
    {
        var directory = Directory.CreateTempSubdirectory("platform-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), """{ "A": "base", "B": "base", "C": "base", "D": "base" }""");
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.Development.json"), """{ "B": "development", "C": "development" }""");
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.testos.json"), """{ "A": "platform", "B": "platform", "C": "platform" }""");
            var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory.FullName);
            configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["C"] = "environment" });

            configuration.AddPlatformSettings("testos");

            Assert.Equal("platform", configuration["A"]);
            Assert.Equal("development", configuration["B"]);
            Assert.Equal("environment", configuration["C"]);
            Assert.Equal("base", configuration["D"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddPlatformSettings_IgnoresAMissingPlatformFile()
    {
        var directory = Directory.CreateTempSubdirectory("platform-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), """{ "A": "base" }""");
            var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory.FullName);
            configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

            configuration.AddPlatformSettings("missing");

            Assert.Equal("base", configuration["A"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShippedPlatformSettings_SelectTheTransportAndKeepLinuxPathsOutOfTheBaseFile()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var baseSettings = new ConfigurationBuilder().AddJsonFile(Path.Combine(baseDirectory, "appsettings.json")).Build();
        var linux = new ConfigurationBuilder().AddJsonFile(Path.Combine(baseDirectory, "appsettings.linux.json")).Build();
        var windows = new ConfigurationBuilder().AddJsonFile(Path.Combine(baseDirectory, "appsettings.windows.json")).Build();

        Assert.Null(baseSettings["Workers:SocketDirectory"]);
        Assert.Null(baseSettings["ImageCache:DatabasePath"]);
        Assert.Equal("/run/modular-monolith", linux["Workers:SocketDirectory"]);
        Assert.Equal("/var/lib/modular-monolith/image-cache.db", linux["ImageCache:DatabasePath"]);
        Assert.Equal("NamedPipe", windows["Workers:Transport"]);
        Assert.Null(windows["Workers:SocketDirectory"]);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
