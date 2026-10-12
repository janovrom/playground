namespace ModularMonolith.Abstractions;

public interface IProcessGuard
{
    /// <summary>Makes the OS terminate the current process if its parent dies, where the OS needs the child to opt in.</summary>
    void EnsureKillOnParentExit();
}
