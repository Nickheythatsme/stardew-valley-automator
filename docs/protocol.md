# Protocol

The mod listens on an ephemeral IPv4 loopback port and writes `.runtime/endpoint.json` containing the port, per-launch token, PID, and protocol version. One UTF-8 JSON object is sent per line. Lines are capped at 1 MiB; plans are capped at 32 KiB.

The first request must be `hello` with the token. Supported methods are `hello`, `ping`, `observe`, `execute_plan`, `get_execution`, and `cancel_execution`. Long-running work returns an execution ID immediately and publishes ordered events while the client polls or consumes notifications.

`request_id` and `plan_id` are idempotency keys. The mod caches the latest 256 request responses and maps repeated plan IDs to their original execution.

Canonical schemas live in `schemas/`. Protocol and action schema version `1.0` use strict additional-property rejection.
