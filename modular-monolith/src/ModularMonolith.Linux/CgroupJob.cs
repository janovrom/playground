using System.Diagnostics;
using ModularMonolith.Abstractions;

namespace ModularMonolith.Linux;

public sealed class CgroupJob : IJob
{
    private readonly bool _recursiveCleanup;
    private int _disposed;

    internal CgroupJob(string name, string path, bool recursiveCleanup)
    {
        Name = name;
        Path = path;
        _recursiveCleanup = recursiveCleanup;
    }

    public string Name { get; }

    public string Path { get; }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        File.WriteAllText(System.IO.Path.Combine(Path, "cgroup.procs"), $"{process.Id}\n");
    }

    public void Terminate()
    {
        // cgroup.kill needs Linux 5.14+; on older kernels WorkerProcess falls back to killing the process tree.
        var killFile = System.IO.Path.Combine(Path, "cgroup.kill");
        if (!File.Exists(killFile))
        {
            return;
        }

        try
        {
            File.WriteAllText(killFile, "1\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, _recursiveCleanup);
        }
    }
}
