using System.Runtime.Versioning;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsJobFactory : IJobFactory
{
    public IJob Create(string moduleName, JobLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ModuleName.Validate(moduleName);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows job objects require Windows.");
        }

        return new WindowsJob($"module-{moduleName}-{Guid.NewGuid():N}", limits);
    }
}
