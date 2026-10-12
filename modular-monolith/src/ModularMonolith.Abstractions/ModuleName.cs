namespace ModularMonolith.Abstractions;

public static class ModuleName
{
    public static void Validate(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || moduleName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Module names may contain only ASCII letters, digits, hyphens, and underscores.", nameof(moduleName));
        }
    }
}
