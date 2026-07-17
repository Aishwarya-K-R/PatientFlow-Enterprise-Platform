# Screenshots

Visual proof of the platform running end-to-end. The five below are the
ones featured inline in the [top-level README](../../README.md#-in-action);
this folder is the full gallery.

| File | What it shows |
|---|---|
| [`grafana-mcp-audit.png`](./grafana-mcp-audit.png) | Grafana Loki query for the isolated MCP audit stream (`SourceContext="MCP.Audit"`) — every tool call captured with agent, action, target, duration, success. |
| [`grafana-tempo-trace.png`](./grafana-tempo-trace.png) | Tempo distributed-trace waterfall — one request stitched across gateway, services, Kafka, and downstream consumers via OpenTelemetry propagation. |
| [`grafana-custom-dashboard.png`](./grafana-custom-dashboard.png) | Custom PatientFlow Grafana dashboard checked in as code (`observability/grafana/dashboards/`) — request rate, latency, error rate, business counters. |
| [`claude-desktop-tools.png`](./claude-desktop-tools.png) | Claude Desktop connected to `PMS.Mcp` via the gateway — 4 tools + 3 resources + 1 template discovered and callable with a per-agent API key. |
| [`kafka-retry-dlq.png`](./kafka-retry-dlq.png) | Kafka UI showing main domain topics alongside their `-retry` and `-dlq` companions — the Phase 4 resilience topology in one view. |

## How they map to the phases

| Phase | Screenshot |
|---|---|
| **Phase 4 — Event-driven reliability** | `kafka-retry-dlq.png` |
| **Phase 6 — Observability** | `grafana-tempo-trace.png`, `grafana-custom-dashboard.png` |
| **Phase 8 — MCP + audit** | `grafana-mcp-audit.png`, `claude-desktop-tools.png` |

See [ROADMAP.md](../../ROADMAP.md) for the full 8-phase story.
