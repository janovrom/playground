namespace ModularMonolith.Host;

internal static class WorkerCommandResolver
{
    /// <summary>
    /// Configured <c>ExecutablePath</c>/<c>Arguments</c> win. Otherwise Windows prefers the published apphost (no <c>dotnet</c> on the service PATH needed)
    /// and every platform falls back to <c>dotnet &lt;worker&gt;.dll</c>.
    /// </summary>
    public static (string Executable, List<string> Arguments) Resolve(IConfigurationSection section, string workerProject, string baseDirectory)
    {
        var configuredExecutable = section["ExecutablePath"];
        var arguments = section.GetSection("Arguments").Get<string[]>()?.ToList() ?? [];
        var workerDirectory = Path.Combine(baseDirectory, "workers", workerProject);

        if (string.IsNullOrWhiteSpace(configuredExecutable) && arguments.Count == 0 && OperatingSystem.IsWindows())
        {
            var apphost = Path.Combine(workerDirectory, $"{workerProject}.exe");
            if (File.Exists(apphost))
            {
                return (apphost, arguments);
            }
        }

        if (arguments.Count == 0)
        {
            arguments.Add(Path.Combine(workerDirectory, $"{workerProject}.dll"));
        }

        return (string.IsNullOrWhiteSpace(configuredExecutable) ? "dotnet" : configuredExecutable, arguments);
    }
}
