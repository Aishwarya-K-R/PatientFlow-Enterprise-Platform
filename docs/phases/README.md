# Phase Retrospectives

Retros exist for the **early foundation phases** (1–3) where the churn was
highest and the lessons most transferable. Later phases were more mechanical
extensions of decisions already made in the foundation, so they're
documented **inline in [ROADMAP.md](../../ROADMAP.md)** and preserved as
tagged releases you can `git checkout`.

| Phase | Retrospective | Git tag | Branch |
|---|---|---|---|
| **Phase 1** — Foundation cleanup | [phase-0-foundation-cleanup.md](./phase-0-foundation-cleanup.md) | `v0.1-phase-0` | `phase-0-foundation` |
| **Phase 2** — Microservices split | [phase-1-microservices-split.md](./phase-1-microservices-split.md) | `v0.2-phase-1` | `phase-1-microservices` |
| **Phase 3** — Data ownership + Outbox | [phase-2-data-ownership.md](./phase-2-data-ownership.md) | `v0.3-phase-2` | `phase-2-data-ownership` |
| **Phase 4** — Event-driven reliability | [phase-3-event-reliability-retrospective.md](./phase-3-event-reliability-retrospective.md) | `v0.4-phase-3` | `phase-3-event-reliability` |
| **Phase 5** — Cache warming | See [ROADMAP § Phase 5](../../ROADMAP.md#phase-5--cache-warming) | `v0.4.1-cache-warming` | `phase-3.5-cache-warming` |
| **Phase 6** — Observability | See [ROADMAP § Phase 6](../../ROADMAP.md#phase-6--observability) | `v0.5-phase-6` | `phase-6-observability` |
| **Phase 7** — RAG with pgvector + Ollama | See [ROADMAP § Phase 7](../../ROADMAP.md#phase-7--rag-with-pgvector--ollama) | `v0.6-phase-10` | `phase-10-rag` |
| **Phase 8** — MCP server + audit | See [ROADMAP § Phase 8](../../ROADMAP.md#phase-8--mcp-server--audit-logging) | `v0.7-phase-11` | `phase-11-mcp` |

> The historical tag names reflect the original working numbering used during
> development. The **Phase** column reflects the linear 1 → 8 story used
> throughout the current documentation. See [ROADMAP.md](../../ROADMAP.md)
> for the full mapping.
