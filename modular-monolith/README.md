# Modular Monolith Playground

A .NET 10 modular monolith combining single-installer deployment with operating-system-level process isolation, resource governance, and inter-process communication (IPC).

## Architecture & Documentation

- [Unified Architecture Overview](docs/architecture/overview.md) — Comprehensive 4-tier model, platform abstractions (`IJob`, `IProcessGuard`, `IWorkerTransport`), domain glossary, status matrix, and platform deployment appendices.
- [Architecture Backlog & Roadmap](docs/backlog/README.md) — Prioritized backlog items for YARP gateway routing (`BL-0002`), transport resilience (`BL-0001`), Roslyn source generation (`BL-0003`), packaging (`BL-0004`), and circuit breaking (`BL-0005`).
- [Running Windows Code & Tests on Linux](docs/research/running-windows-code-and-tests-on-linux.md) — Empirical research on cross-platform test execution and Wine compatibility.

## Quick Start

### Prerequisites
- .NET 10 SDK (`10.0.100` or newer)
- Linux (cgroup v2 support for full isolation) or Windows 10/11 / Windows Server 2016+

### Build & Test
```bash
# Restore and build the solution
dotnet build modular-monolith/ModularMonolith.slnx

# Run all automated tests
dotnet test modular-monolith/ModularMonolith.slnx
```

*Note: Windows-specific tests (exercising Win32 Job Objects and Named Pipes) are automatically skipped when running on Linux.*

### Running Locally (Development Mode)
```bash
# Run the host directly (starts child workers automatically with Job:Enabled=false in dev)
dotnet run --project modular-monolith/src/ModularMonolith.Host
```

Navigate to:
- `http://localhost:5227/` — Rendered grid demo (using image cache and SVG renderer workers)
- `http://localhost:5227/health` — Host health probe
- `http://localhost:5227/modules` — Worker status, PIDs, and supervisor restart counts
