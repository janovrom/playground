namespace ModularMonolith.Linux;

public static class CgroupPathResolver
{
    public static string GetCurrentProcessRoot(string cgroupMountPath = "/sys/fs/cgroup", string processCgroupPath = "/proc/self/cgroup")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cgroupMountPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(processCgroupPath);

        foreach (var line in File.ReadLines(processCgroupPath))
        {
            var parts = line.Split([':'], 3, StringSplitOptions.None);
            if (parts.Length == 3 && parts[0] == "0" && parts[1].Length == 0 && parts[2].StartsWith("/", StringComparison.Ordinal))
            {
                return Path.GetFullPath(Path.Combine(cgroupMountPath, parts[2].TrimStart('/')));
            }
        }

        throw new InvalidOperationException($"Could not find the unified cgroup path in '{processCgroupPath}'.");
    }
}
