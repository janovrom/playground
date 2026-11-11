# Modular Monolith Architecture Backlog & Implementation Roadmap

This backlog formalizes the architectural gaps identified between the target system specifications and the current repository implementation. Each backlog item provides the technical baseline, target design, constraints, and verifiable acceptance criteria.

---

## Backlog Item Summary & Priority Matrix

| Item ID | Status | Title | Target Tier | Priority | Estimated Complexity | Dependencies |
|---|---|---|---|---|---|---|
| [BL-0001](bl-0001.md) | Done | Transport Layer Resilience & Connection Recovery | Tier 3 (Transport) | High | Medium | None |
| [BL-0002](bl-0002.md) | Proposed | YARP Reverse Proxy for Dynamic Gateway Routing | Tier 1 (Host/Edge) | High | Medium | BL-0001 |
| [BL-0003](bl-0003.md) | Proposed | Roslyn Source Generator for Module Onboarding (`WorkerSourceGenerator`) | Packaging & DevEx | High | High | None |
| [BL-0004](bl-0004.md) | Proposed | Unified Packaging Pipeline & Single Distribution Bundle | Packaging & Deploy | Medium | Medium | BL-0003 |
| [BL-0005](bl-0005.md) | Proposed | Circuit Breaking, Poison-Pill Quarantine & Dead-Letter Handling | Tier 4 (Resilience) | Medium | High | BL-0001, BL-0002 |
| [BL-0006](bl-0006.md) | Proposed | High-Throughput Shared Memory IPC (MMF / Ring Buffer) | Tier 3 (Transport) | Low | High | BL-0001 |

---
