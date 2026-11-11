# Modular Monolith Architecture Backlog & Implementation Roadmap

This backlog formalizes the architectural gaps identified between the target system specifications and the current repository implementation. Each backlog item provides the technical baseline, target design, constraints, and verifiable acceptance criteria.

---

## Backlog Item Summary & Priority Matrix

| Item ID | Status | Title | Target Tier | Priority | Estimated Complexity | Dependencies |
|---|---|---|---|---|---|---|
| [BL-0001](bl-0001.md) | Done | Transport Layer Resilience & Connection Recovery | Tier 3 (Transport) | High | Medium | None |
| BL-0002 | Proposed | YARP Reverse Proxy for Dynamic Gateway Routing | Tier 1 (Host/Edge) | High | Medium | BL-0001 |
| BL-0003 | Proposed | Roslyn Source Generator for Module Onboarding (`WorkerSourceGenerator`) | Packaging & DevEx | High | High | None |
| BL-0004 | Proposed | Unified Packaging Pipeline & Single Distribution Bundle | Packaging & Deploy | Medium | Medium | BL-0003 |
| BL-0005 | Proposed | Circuit Breaking, Poison-Pill Quarantine & Dead-Letter Handling | Tier 4 (Resilience) | Medium | High | BL-0001, BL-0002 |
| BL-0006 | Proposed | High-Throughput Shared Memory IPC (MMF / Ring Buffer) | Tier 3 (Transport) | Low | High | BL-0001 |

Proposed item files are not yet published in this branch.

---
