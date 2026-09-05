using ModularMonolith.Contracts;
using ModularMonolith.Linux;

LinuxProcessGuard.EnsureKillOnParentExit();

var request = new ModuleRequest(ModuleCatalog.Inventory, "reserve-stock", "sku-900");
var response = ModuleRegistry.Handle(ModuleCatalog.Inventory, request.Operation, request.Payload);

Console.WriteLine($"Worker {ModuleCatalog.Inventory} pid={Environment.ProcessId}");
Console.WriteLine($"Result: {response.Result} | Payload: {response.Payload}");
