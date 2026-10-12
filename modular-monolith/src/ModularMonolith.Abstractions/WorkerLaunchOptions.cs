namespace ModularMonolith.Abstractions;

public sealed record WorkerLaunchOptions(
    string ModuleName,
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    JobLimits? Limits = null);
