namespace ModularMonolith.Contracts;

public sealed record ModuleRequest(string ModuleName, string Operation, string Payload);

public sealed record ModuleResponse(string ModuleName, string Operation, string Result, string Payload);

public static class ModuleCatalog
{
    public const string Sales = "sales";
    public const string Inventory = "inventory";

    public static readonly string[] All = [Sales, Inventory];
}

public static class ModuleRegistry
{
    public static ModuleResponse Handle(string moduleName, string operation, string payload)
    {
        var normalized = moduleName.Trim();
        return normalized switch
        {
            ModuleCatalog.Sales => new ModuleResponse(ModuleCatalog.Sales, operation, "processed", $"Sales handled: {payload}"),
            ModuleCatalog.Inventory => new ModuleResponse(ModuleCatalog.Inventory, operation, "processed", $"Inventory handled: {payload}"),
            _ => throw new InvalidOperationException($"Unknown module: {moduleName}")
        };
    }
}
