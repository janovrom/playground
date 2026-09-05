## Linux System Architecture Overview

This Linux variant adapts the modular monolith architecture for POSIX environments, substituting Win32 kernel structures with **cgroups**, **`prctl` process lineage management**, **Unix Domain Sockets (UDS)**, and POSIX shared memory (`/dev/shm`).

---

## Tiered Implementation Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│ TIER 1: HOST & GATEWAY ORCHESTRATOR                                    │
│  ├── YARP Reverse Proxy over Unix Domain Sockets (/var/run/app/*.sock) │
│  ├── Cgroup Controller & Watchdog Spawner                              │
│  ├── Security Edge (Authentication, Token & Claim Propagation)         │
│  └── Central Event Bus Dispatcher (System.Threading.Channels)          │
└──────────────────────────────────┬─────────────────────────────────────┘
                                   │ IPC Channel
                                   │ (gRPC / UDS  OR  POSIX /dev/shm MMF)
                                   ▼
┌────────────────────────────────────────────────────────────────────────┐
│ TIER 2: WORKER RUNTIME & LINUX ISOLATION                               │
│  ├── Cgroup Limits (memory.max, cpu.max, pids.max)                     │
│  ├── Parent Death Signal Enforcement (prctl PR_SET_PDEATHSIG -> SIGKILL)│
│  ├── Dedicated Module Worker Process (Linux ELF Native AOT)           │
│  └── Graceful Signal Handling (SIGTERM / SIGINT via Lifetime)          │
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

## Tier Details & Linux Specifications

### Tier 1: Host Orchestrator & Gateway Edge

The Host process runs as an unprivileged or managed Linux daemon, orchestrating child processes and dynamic IPC routes.

* **UDS Routing via YARP**: YARP routes traffic to worker instances listening on Linux file sockets (e.g., `unix:///var/run/app/modules-sales.sock`), eliminating TCP loopback overhead and port allocation conflicts.
* **Cgroup Controller**: Operates directly on the cgroup hierarchy under `/sys/fs/cgroup/app.slice/` or delegates slice creation via `systemd` DBus API (`systemd-run --transient`).
* **Security Edge**: Authenticates HTTP/gRPC ingress once, embedding JWT/claims and OpenTelemetry baggage context into binary stream headers over UDS/MMF.
* **Async Event Dispatcher**: Employs an in-memory `System.Threading.Channels` bus inside the Host for high-throughput event distribution across worker processes.

### Tier 2: Linux Process Isolation & Resource Limits (`cgroups`)

Each worker module is launched within a dedicated cgroup subtree and isolated against orphan execution using native Linux kernel signals.

* **Cgroup Control Knobs**:
* `memory.max`: Hard limit (e.g., `128M`). Exceeding this triggers the Linux Out-Of-Memory (OOM) killer directly on the worker PID without impacting the Host process.
* `memory.high`: Throttle limit (e.g., `96M`). Triggers synchronous page reclaim before OOM action occurs.
* `cpu.max`: Restricts quota per period (e.g., `50000 100000` caps CPU consumption at 50% of a single CPU core).
* `pids.max`: Prevents process/thread exhaustion (e.g., capped at `64` threads per worker).


* **Parent Death Signal (`PR_SET_PDEATHSIG`)**:
* Every worker process calls `prctl(PR_SET_PDEATHSIG, SIGKILL)` immediately upon startup via P/Invoke.
* If the Host process crashes or receives `SIGKILL`, the Linux kernel automatically sends `SIGKILL` to all child workers, preventing orphaned process state.


* **Native AOT Execution**: Compiled as Linux ELF binaries (`linux-x64` / `linux-arm64`) targeting glibc or musl for instant cold starts (~10ms) and low RSS baseline (~15MB).

### Tier 3: High-Performance Inter-Process Communication (IPC)

| Metric / Aspect | Standard Tier: gRPC over Unix Domain Sockets | High-Throughput Tier: Shared Memory (`/dev/shm`) |
| --- | --- | --- |
| **Transport Layer** | POSIX Unix Domain Sockets (`SocketsHttpHandler`) | Shared RAM backing file (`/dev/shm` or `memfd_create`) |
| **Average Latency** | ~20 – 60 microseconds | ~1 – 3 microseconds |
| **Synchronization** | Linux Epoll / Kestrel Socket Engine | Futex / Lock-free Atomic Ring Buffer + `EventWaitHandle` |
| **Serialization** | Protobuf / Source-Generated JSON | Zero-Allocation MessagePack (`IBufferWriter<byte>`) |
| **File Permissions** | Socket file permissions (`chmod 0600`) | POSIX memory permissions (`shm_open` + `fchmod`) |

### Tier 4: Resilience & Circuit Breaking

Protects system stability against worker crash loops triggered by corrupt payloads or kernel OOM events.

* **Poison Pill Quarantine**: Tracks payload hash and execution attempt counters. If a payload causes a worker crash $N$ times (default: 2 attempts), the payload is quarantined to a Dead-Letter Queue and returned as an error.
* **Global Module Circuit Breaker**: Evaluates crash frequencies within a rolling window. If a module crashes $M$ times in $T$ minutes (e.g., 3 crashes in 2 minutes), the module transitions to `Open` state, halting auto-respawn to prevent CPU thrashing.
* **Signal-Aware Lifecycle**: Worker processes register shutdown hooks on `SIGTERM` and `SIGINT` via `IHostApplicationLifetime` to allow in-flight socket requests to drain gracefully before exit.

---

## Linux Process Isolation Helper (`LinuxProcessGuard.cs`)

Native P/Invoke helper used by child workers to guarantee process tree cleanup:

```csharp
using System.Runtime.InteropServices;

public static partial class LinuxProcessGuard
{
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGKILL = 9;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    /// <summary>
    /// Guarantees that the Linux kernel kills this child worker if the Host parent process dies.
    /// Call this at worker entrypoint before starting host services.
    /// </summary>
    public static void EnsureKillOnParentExit()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            int result = prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"Failed to set PR_SET_PDEATHSIG. Errno: {errno}");
            }
        }
    }
}

```