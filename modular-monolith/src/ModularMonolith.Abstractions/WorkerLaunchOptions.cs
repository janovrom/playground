namespace ModularMonolith.Abstractions;

/// <summary>Describes the executable, arguments, limits, and private environment for one worker process.</summary>
public sealed record WorkerLaunchOptions(
    string ModuleName,
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    JobLimits? Limits = null,
    IReadOnlyDictionary<string, string>? Environment = null);
