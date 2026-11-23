namespace ModularMonolith.Host;

internal static class WorkerCommandResolver
{
    /// <summary>
    /// Configured <c>ExecutablePath</c>/<c>Arguments</c> win. Otherwise the published apphost is preferred
    /// and every platform falls back to <c>dotnet &lt;worker&gt;.dll</c>.
    /// </summary>
    public static (string Executable, List<string> Arguments) Resolve(IConfigurationSection section, string workerProject, string baseDirectory)
    {
        var configuredExecutable = section["ExecutablePath"];
        var arguments = section.GetSection("Arguments").Get<string[]>()?.ToList() ?? [];
        var workerDirectory = Path.Combine(baseDirectory, "workers", workerProject);

        if (string.IsNullOrWhiteSpace(configuredExecutable) && arguments.Count == 0)
        {
            var apphostName = OperatingSystem.IsWindows() ? $"{workerProject}.exe" : workerProject;
            var apphost = Path.Combine(workerDirectory, apphostName);
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
