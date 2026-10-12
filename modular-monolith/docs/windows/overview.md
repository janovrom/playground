## System Architecture Overview

This modular monolith architecture couples a single-installer deployment model with OS-level process isolation and resource governance using C#, ASP.NET Core, Windows Job Objects, and high-performance inter-process communication (IPC).

## Implemented Windows Variant

The Windows variant runs the same two worker modules as the Linux variant (`svg-renderer` and `image-cache`, see [the Linux overview](../linux/overview.md#implemented-worker-modules)) with Windows-specific isolation, transport and hosting. The tiered sections below describe the target architecture; this section lists what exists today. Native AOT, MessagePack, the shared-memory tier, the Roslyn `WorkerSourceGenerator` and Tier 4 resilience are not implemented on either platform yet.

### Platform abstractions

| Concern | Abstraction | Linux | Windows |
| --- | --- | --- | --- |
| Resource container | `IJob` / `IJobFactory` + `JobLimits` | `CgroupJob` (cgroup v2) | `WindowsJob` (Job Object) |
| Launching | `JobWorkerLauncher` (one shared launcher) | cgroup per worker | Job Object per worker |
| Orphan protection | `IProcessGuard` | `prctl(PR_SET_PDEATHSIG)` | none needed: `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` |
| Graceful stop | `IGracefulStopStrategy` | `SIGTERM` | named `EventWaitHandle` |
| Transport | `IWorkerTransport` | Unix Domain Sockets | Named Pipes |

`PlatformServices` (`ModularMonolith.Platform`) selects the implementation by OS. `JobLimits` is platform neutral: `MemoryMaxBytes`, `MemoryHighBytes`, `CpuCores` and `MaxProcesses`; a `null` member means unlimited.

### Job Objects (`ModularMonolith.Windows`)

Each worker gets an unnamed Job Object owned by the Host. `WindowsJobLimitMapper` converts `JobLimits` into Win32 settings:

| `JobLimits` | Job Object setting | Difference from cgroups |
| --- | --- | --- |
| always | `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` | Replaces `PR_SET_PDEATHSIG`; workers die when the Host's job handle closes. |
| `MemoryMaxBytes` | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` and `JOB_OBJECT_LIMIT_JOB_MEMORY` | Limits committed memory, not RSS. The shipped Windows settings use 256 MB; validate for your workload. |
| `MemoryHighBytes` | not applicable | Windows has no soft reclaim threshold; the value is ignored. |
| `CpuCores` | `JOB_OBJECT_CPU_RATE_CONTROL_ENABLE` + `HARD_CAP` | The Win32 rate is a share of all logical processors, so `CpuRate = cores / processorCount * 10000` (clamped to 1–10000). `cpu.max` is relative to one core. |
| `MaxProcesses` | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` | Counts processes, whereas `pids.max` also counts threads. |

The worker process is started first and then assigned to the job, the same short window as the Linux `cgroup.procs` write. `PROC_THREAD_ATTRIBUTE_JOB_LIST` could close it later. Nested jobs (a Host that already runs inside a job) are supported on Windows 8 and later.

Per-module limits come from the module's defaults and can be overridden with `Workers:<Module>:Limits:MemoryMaxMb`, `MemoryHighMb`, `CpuCores` and `MaxProcesses`. `Job:Enabled=false` (development) keeps the Job Object for orphan protection but drops all limits.

### Graceful shutdown

Windows has no `SIGTERM`. Before starting a worker the Host creates a named event (`Local\modular-monolith-stop-<module>-<guid>`) and passes its name as `--Worker:ShutdownEvent`. The worker waits on it and calls `IHostApplicationLifetime.StopApplication()`, so in-flight requests drain and the cache checkpoints SQLite exactly as on Linux. After `Workers:ShutdownGracePeriod` (10 seconds by default) the Host terminates the job.

### Transport

`Workers:Transport` selects `Auto` (named pipes on Windows, Unix sockets elsewhere), `NamedPipe` or `UnixSocket`. Named pipes are named `<Workers:PipeNamePrefix>.<per-run id>.<module>` (prefix defaults to `modular-monolith`); the random id keeps two Hosts apart and prevents squatting on a predictable name. Kestrel's named pipe listener and the client both use current-user-only access, the counterpart of `chmod 0600` on a socket. Pipes disappear when the worker exits, so no stale endpoint cleanup is needed. The Host passes `--Worker:Transport` and `--Worker:Endpoint` to each worker.

### Module registration

* Worker side: a module implements `IWorkerModule` (`ConfigureServicesAsync` and `MapEndpoints`); its `Program.cs` is `await WorkerHost.RunAsync(args, new <Module>WorkerModule())`. `WorkerHost` adds the transport listener, `/health`, the process guard and the shutdown listener.
* Host side: a module implements `IHostModule` (name, worker project, config section, default limits, worker arguments, client registration) and is added with `services.AddHostModule<T>()`. `WorkerManager` supervises whatever modules are registered.

Adding a module therefore needs one worker module and one host module; `WorkerManager`, the transport and the launchers stay untouched.

### Hosting and deployment

* The Host calls `UseWindowsService` (service name `ModularMonolith`), so the same binary runs as a console app or a Windows service.
* Platform defaults live in `appsettings.windows.json` / `appsettings.linux.json`, which are loaded directly after `appsettings.json`; environment files, environment variables and the command line still override them.
* `dotnet publish src/ModularMonolith.Host -c Release -r win-x64 --self-contained false` produces the package. Workers are published next to the Host as apphost executables (`workers\<Project>\<Project>.exe`); when no worker command is configured the Host prefers the apphost and falls back to `dotnet <Project>.dll`.
* `scripts\install.ps1` (run elevated) copies the package to `%ProgramFiles%\ModularMonolith`, creates the `ModularMonolith` service under the virtual account `NT SERVICE\ModularMonolith`, grants it read/execute on the install directory and modify on `%ProgramData%\ModularMonolith`, sets the service environment (`ASPNETCORE_URLS`, `ImageCache__DatabasePath`), configures restart-on-failure and starts it. `scripts\run.ps1 start|stop` controls the service; `scripts\uninstall.ps1` removes it (`-RemoveData` also deletes the database).
* Windows has no equivalent of the systemd start limit; the service restarts up to three times after a failure and then stays stopped until it is started again.

### Verification status

The cross-platform unit tests run on any OS. The Windows-only tests (Job Object limits round trip, kill-on-close, named event shutdown, named pipe round trip, supervisor restart) are skipped elsewhere and must be run on a Windows machine, as must `install.ps1`.

---

## Tiered Implementation Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│ TIER 1: HOST & GATEWAY ORCHESTRATOR                                    │
│  ├── YARP Reverse Proxy & Dynamic Route Table                          │
│  ├── Process Spawner & Health Watchdog                                 │
│  ├── Security Edge (Authentication, Token & Claim Propagation)         │
│  └── Central Event Bus Dispatcher (System.Threading.Channels)          │
└──────────────────────────────────┬─────────────────────────────────────┘
                                   │ IPC Channel
                                   │ (gRPC/Named Pipes OR Shared Memory MMF)
                                   ▼
┌────────────────────────────────────────────────────────────────────────┐
│ TIER 2: WORKER RUNTIME & ISOLATION                                     │
│  ├── Windows Job Object Limits (Hard RAM Cap, CPU Rate Throttling)     │
│  ├── Dedicated Module Worker Process (Native AOT Execution)          │
│  └── Graceful Drain & Shutdown Handler (IHostApplicationLifetime)      │
└──────────────────────────────────┬─────────────────────────────────────┘
                                   │ Statically Linked
                                   ▼
┌────────────────────────────────────────────────────────────────────────┐
│ TIER 3: DOMAIN & APPLICATION LOGIC                                     │
│  ├── Pure Handlers & Entities (Zero Infrastructure/Web Dependencies)   │
│  └── Zero-Allocation MessagePack Data Transfer Objects                 │
└────────────────────────────────────────────────────────────────────────┘

```

---

## Tier Details & Specifications

### Tier 1: Host Orchestrator & Gateway Edge

The Host process serves as the sole public entry point, security boundary, and lifecycle manager for all modules.

* **API Gateway & Routing**: Uses YARP to dynamically route incoming HTTP traffic directly to worker endpoints based on module availability.
* **On-Demand Spawning**: Intercepts requests for idle or non-running modules using a thread-safe `ModuleProcessSpawner` that launches dedicated worker binaries.
* **Security Edge**: Authenticates incoming requests once, attaching user claims, tenant context, and W3C trace identifiers to gRPC metadata or binary frame headers.
* **Async Event Dispatcher**: Hosts an in-memory `System.Threading.Channels` pub/sub event bus to route asynchronous inter-module events.

### Tier 2: Process Isolation & Resource Limits

Each module executes inside a dedicated child process bounded by OS-level Win32 kernel controls.

* **Windows Job Objects**:
* Enforces `JOB_OBJECT_LIMIT_PROCESS_MEMORY` to instantly terminate processes exceeding RAM thresholds.
* Enforces `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` to restrict CPU utilization percentages.
* Sets `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` to eliminate orphaned worker processes upon Host termination.


* **Native AOT Execution**: Compiled with `<PublishAot>true</PublishAot>` for instant startup (~10–20ms) and minimal base memory footprint (~15–25MB per worker).
* **Fault Containment**: A crash, native memory leak, or unhandled exception inside a worker only terminates that specific process, leaving the Host and other modules running.

### Tier 3: High-Performance Inter-Process Communication (IPC)

Communication protocols are selected based on latency and throughput requirements.

| Metric / Aspect | Standard Tier: gRPC over Named Pipes | High-Throughput Tier: Memory-Mapped Files (MMF) |
| --- | --- | --- |
| **Transport Layer** | Win32 Named Pipes (`HttpProtocols.Http2`) | Shared Physical RAM (`System.IO.MemoryMappedFiles`) |
| **Average Latency** | ~100 – 300 microseconds | ~1 – 5 microseconds |
| **Synchronization** | Kestrel / HTTP2 Framing | Atomic SPSC Ring Buffer + `EventWaitHandle` |
| **Serialization** | Protobuf / Source-Generated JSON | Zero-Allocation MessagePack (`IBufferWriter<byte>`) |
| **Primary Use Case** | Standard synchronous module endpoints | Sub-millisecond high-frequency IPC & data streaming |

### Tier 4: Resilience & Circuit Breaking

Protects the system against infinite restart-crash loops caused by bad payloads or hardware pressure.

* **Poison Pill Quarantine**: Tracks per-request hash and attempt counters. If a payload causes a worker process crash $N$ times (default: 2), the request is automatically diverted to a Dead-Letter Queue and failed with a `PoisonPillException`.
* **Global Module Circuit Breaker**: Tracks crash frequencies in a rolling time window. If a module crashes $M$ times within $T$ minutes (e.g., 3 crashes in 2 minutes), the circuit opens to suspend automatic respawning and prevent CPU thrashing.
* **Blue-Green Zero-Downtime Swap**: During updates or restarts, the Host boots candidate processes (`vNext`), verifies health probes, atomically shifts YARP routes, and executes graceful request draining (`vOld`) via `IHostApplicationLifetime.StopApplication()`.

---

## Packaging & Build Pipeline Architecture

```
[ App.Installer.csproj ]
       │
       ├── MSBuild Task Batching (for each referenced module)
       │
       ├── App.Worker.csproj -p:TargetModule=Modules.Sales ──► Roslyn Generator ──► App.Worker.Modules.Sales.exe
       │
       └── App.Worker.csproj -p:TargetModule=Modules.Inventory ──► Roslyn Generator ──► App.Worker.Modules.Inventory.exe

```

1. **Source Generation**: A Roslyn Source Generator (`WorkerSourceGenerator`) inspects compile-time properties to generate static dependency injection wiring and route mappings without reflection.
2. **MSBuild Task Batching**: The main installer project executes MSBuild target loops to publish distinct, Native AOT-trimmed binaries for each module.
3. **Single Installer Deliverable**: All generated worker binaries and the Host executable are packaged together in a single distribution zip or installer.