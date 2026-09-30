# Architecture and contributor guides

UnityLeanMcp connects a .NET MCP host to an Editor-only Unity package over project-bound loopback TCP. The public tools are `unity_refresh`, `unity_eval`, `unity_test`, and `unity_coverage`. Execution tools start Unity as needed and wait for busy work; coverage inspects existing measurements without refreshing. Static invocation, inspection, and occasional visual capture use eval; automated runtime verification uses PlayMode tests. Separate status, stop, logs, capture, and interactive PlayMode tools are intentionally absent from the public catalog.

## System invariants

- The Editor owns mutation admission across all clients. `UnityCommandGate` stores ownership in `SessionState` and publishes managed snapshots to workers; there is no disk ownership journal or tool-level semaphore queue.
- Each mutation has a client-generated ID. Retry the same ID after an ambiguous acknowledgement. Correlated terminal files establish completion; shared history and generic readiness replies do not.
- Persist the exact terminal payload before publication, and retain ownership while publication retries. `SessionState` survives managed reload, not process exit; do not promise crash-proof or indefinite exactly-once delivery.
- Unity-dependent work runs on the main thread. Worker polling, early rejection, and teardown must remain usable while that thread is busy or the domain is unloading.
- Eval and tests cross their own correlated refresh barrier before consuming compiled code. Compiler diagnostics survive no-op refreshes; runtime/import errors are scoped to the current refresh window.
- Validate the intended project on every socket command. A port, PID, or lockfile alone is not permission to mutate or terminate a particular Editor.
- Preserve developer-owned Editor state: ordinary tests do not change profiling settings, refresh does not clear the Console, and startup compiler errors do not justify killing the Editor.

## Find the relevant topic

Read the guide or section affected by the task. Each guide keeps design decisions and learned pitfalls together; cross-boundary changes may need more than one.

| Guide | Changes it covers | Common failure modes |
| --- | --- | --- |
| [Operation lifecycle](operation-lifecycle.md) | Admission, polling, cancellation, reload, bootstrap, compiler diagnostics | [Lost acknowledgements, stuck ownership, and cancellation races](operation-lifecycle.md#completion-and-cancellation-races); [initialization/thread affinity](operation-lifecycle.md#initialization-order-and-thread-affinity); [premature refresh success](operation-lifecycle.md#refresh-observations-are-asynchronous) |
| [Evaluation, tests, and coverage](evaluation-tests-coverage.md) | Snippet compilation/execution, Test Runner callbacks, coverage, output | [Roslyn and source-position pitfalls](evaluation-tests-coverage.md#learned-pitfalls); [duplicate callbacks](evaluation-tests-coverage.md#test-runner-callback-ownership); [coverage persistence assumptions](evaluation-tests-coverage.md#coverage-runtime-evidence) |
| [Processes and transport](process-and-transport.md) | Editor startup/identity, filesystem IPC, sockets, MCP stdio | [Stale ports/PIDs, sharing failures, locks, and paths](process-and-transport.md#learned-pitfalls); [historical startup errors](process-and-transport.md#historical-startup-logs); [encoding and dead pipes](process-and-transport.md#mcp-stdio-and-encoding) |
| [Testing, configuration, and release](testing-and-release.md) | Host/Editor compatibility, schemas, test selection, fixtures, configuration, packaging | [Host builds passing while Unity fails](testing-and-release.md#shared-source-and-schemas); [stale fixtures and mock hangs](testing-and-release.md#learned-pitfalls); [artifact mismatch and skipped release tests](testing-and-release.md#configuration-and-release-artifacts) |

## Maintaining these guides

Keep repository-wide rules in [AGENTS.md](../AGENTS.md). Update the owning topic when a lasting decision or reproducible, non-obvious pitfall changes. Verify historical advice against current code: later commits can supersede the original fix. Keep routine change history and implementation inventories in Git and code, and add a router entry only when it improves discovery.
