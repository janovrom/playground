## System Architecture Overview

This modular monolith architecture couples a single-installer deployment model with OS-level process isolation and resource governance using C#, ASP.NET Core, Windows Job Objects, and high-performance inter-process communication (IPC).

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