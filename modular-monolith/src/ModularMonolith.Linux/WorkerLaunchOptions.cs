namespace ModularMonolith.Linux;

public sealed record WorkerLaunchOptions(
    string ModuleName,
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    CgroupLimits? Limits = null);
