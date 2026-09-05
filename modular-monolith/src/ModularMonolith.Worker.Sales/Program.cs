using ModularMonolith.Contracts;
using ModularMonolith.Linux;

LinuxProcessGuard.EnsureKillOnParentExit();

var request = new ModuleRequest(ModuleCatalog.Sales, "create-order", "customer-42");
var response = ModuleRegistry.Handle(ModuleCatalog.Sales, request.Operation, request.Payload);

Console.WriteLine($"Worker {ModuleCatalog.Sales} pid={Environment.ProcessId}");
Console.WriteLine($"Result: {response.Result} | Payload: {response.Payload}");
