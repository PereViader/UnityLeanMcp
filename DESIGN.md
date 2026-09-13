# Design Decisions

This document records the architectural guidelines, core patterns, and design decisions governing UnityLeanMcp and related tools operating across compilation, domain reloads, shutdowns, reconnects, and multiple desktop platforms.

---

## 1. Core Design Principles

- **Cross-Platform Reliability**: Every component must work reliably across Windows, Linux, and macOS. Avoid OS-specific assumptions regarding path separators, process naming, file locking, or shell features.
- **Simple, Transactional Designs**: Keep state transitions and operations easy to reason about and recover from. Operations must follow clear transactional phases (claim ownership, persist intent, perform work, persist result, release ownership) so failure or interruption at any point leaves the system in a clean, recoverable state.
- **Asynchronous Domain Reload Tolerance**: Treat Unity domain reloads as asynchronous, externally triggered events. Reloads can occur at any moment due to external script modifications, Editor focus changes, or internal compilation. Code must tolerate interruption and reinitialization without relying on uninterrupted in-memory process state.
- **Unbounded Operations & Deterministic Failure Detection**: Never impose arbitrary bounded timeouts to cut off operations. Delays should only be used when strictly necessary for reliability—not as operation timeouts, but as short delays to allow external operations or services to warm up and settle. If a backing operation remains active indefinitely, the runner and MCP tools must remain active until the backing work completes, fails, or is stopped by the user. Failure detection must be deterministic (e.g. process exit, PID/lockfile checks, diagnostic error publication), never time-based.

---

## 2. Operation Lifecycle & State Machine

### Client-Generated Operation IDs
Every mutating request must be assigned a unique client-generated operation ID (`operationId`). This ID must accompany the initial request, every polling probe, durable journal state, running progress markers, and the terminal result file.

### Single-Owner Mutating Operations & Idempotent Retries
Mutating operations are serialized through a single project-scoped owner record (`unity_operation.json`):
- A request with an identical `operationId` is treated as an idempotent retry.
- A request with a different `operationId` while an operation is active receives an immediate `BUSY` response rather than racing shared Unity or filesystem state.
- If a socket connection drops while Unity is processing an operation, retrying with the same `operationId` retrieves the correlated result rather than re-executing the command.

### Transactional Persistence Ordering
The operation lifecycle must strictly follow this transactional sequence:
1. **Claim Ownership**: Register the operation ID and intent in the durable operation journal (`unity_operation.json`).
2. **Mark Active**: Write the active running marker (e.g. `unity_test_running.txt`).
3. **Execute Work**: Dispatch the work to the Unity main thread or appropriate engine subsystem.
4. **Persist Result**: Atomically write the terminal result file.
5. **Release Ownership**: Remove the operation record from the journal.

Ownership or running markers must **never** be cleared before the terminal result is durable on disk. Clearing ownership in a `finally` block or before the result file is securely committed causes false-positive `IDLE` reporting if writing fails or encounters transient delays.

### Operation-Scoped Result Files & Atomic Delivery
Terminal results are persisted to operation-scoped file paths (`Temp/unity_eval_<operationId>.json`, `Temp/unity_execute_<operationId>.json`, `Temp/unity_test_<operationId>.json`):
- Because each operation generates a fresh unique path, the destination file never exists beforehand.
- Writers write to a temporary file in the destination directory and move it into place via `File.Move(tempPath, targetPath)`.
- This eliminates the need for `File.Replace` (and underlying Win32 `ReplaceFileW`), avoiding mandatory replacement locks and sharing collisions with concurrent readers across all platforms.
- For test re-runs requiring historical context (such as `--failed-only`), the runner dual-writes results to both the operation-scoped file for client polling and the static `unity_test_results.json` for Editor history.

### Hierarchical Polling Precedence & IDLE Race Prevention
Polling handlers must evaluate operation state in strict hierarchical precedence:
1. **Terminal Result File**: Check the operation-scoped result file for a matching client-generated `operationId`.
2. **Active Running Marker**: Check the active running marker on disk.
3. **Durable Operation Journal**: Query the journal for active ownership. If owned by another operation, return `BUSY`; if owned by this operation, return `RUNNING`.
4. **Race-Condition Safeguard**: If the operation journal is empty, re-check the terminal result file before declaring `IDLE`. This prevents a boundary race condition where a poll occurs precisely between `File.Move` completion and ownership deletion. Additionally, clients observe a brief settlement retry window before declaring unexpected idle failures.

### Extensible Polymorphic Lifecycle Handlers
Operation-specific mechanics (cancellation, domain-reload recovery, Editor restart recovery, and shutdown cleanup) are decoupled from the core server loop via polymorphic lifecycle handlers (`IOperationLifecycleHandler`, `OperationLifecycleRegistry`):
- Each command kind registers its own handler conforming to the Open-Closed Principle (OCP).
- New operation types implement lifecycle hooks without modifying server dispatchers or switch statements.

---

## 3. Threading, Transport & Dispatching

### Thread Separation & Main-Thread Affinity
- **Main Thread**: Serialized Unity API execution, scene manipulation, test execution, compilation triggers, and reflection dispatch.
- **Worker Thread**: Socket connection acceptance, protocol framing, lock-free polling of plain managed snapshots, and early rejection.
- Keeping socket acceptance off the Unity main thread ensures transport processing does not block Editor updates, compilation, or domain reloads.

### Worker-Thread Early Rejection
Commands declare metadata polymorphically via `ICommandHandler` (`IsMutating`, `RequiresCompilationSettled`):
- When a command arrives at the socket server, the worker thread inspects `handler.IsMutating` and checks `UnityLeanMcpOperationStore.ReadThreadSafeSnapshot()` before enqueuing to the main-thread dispatcher.
- Conflicting requests (`BUSY <kind> <opId>` or `BUSY compile`) are rejected immediately on the worker thread.
- This prevents enqueuing onto the main-thread dispatcher when the main thread is occupied with synchronous execution, avoiding deadlocks and TCP client timeouts.

### Explicit Main-Thread Service Initialization
Classes must not initialize main-thread Unity APIs in static constructors or static field initializers. The socket server must only begin accepting external connections after dependent main-thread services (`UnityLeanMcpPaths`, `UnityLeanMcpOperationStore`, `UnityLeanMcpCompilationTracker`, `UnityLeanMcpDispatcher`, `RoslynCompilerHelper`) have completed explicit `EnsureInitialized()` calls on the Unity main thread.

### Single-Line Status Framing & Token Escaping
Line-oriented socket communication uses strict single-line framing:
- Status responses: `SUCCESS <payload>`, `FAILURE <message>`, `INTERRUPTION <message>`, `BUSY <reason>`.
- Payloads are compactly serialized (`JsonUtility.ToJson(result, false)`).
- Token codecs symmetrically escape and unescape `\\`, `\"`, `\r`, `\n`, and `\t`.
- Status token prefixes (e.g. `FAILURE <msg>`) must be stripped immediately upon response receipt so protocol framing does not leak into diagnostic parsers or error payloads.

### Clean Shutdown & Transport Decoupling
- Socket-initiated Editor shutdown (`EXIT`) stops listener services and calls `EditorApplication.Exit(0)` immediately. Standard quitting hooks mark in-flight operations as interrupted in durable state.
- A broken TCP connection indicates transport interruption, not that the underlying Unity operation failed. Clients rediscover the endpoint and resume polling by `operationId`.

---

## 4. MCP Tool Surface & Agent Ergonomics

### Lean Public MCP Surface
To optimize LLM context window consumption and eliminate agent decision friction:
- **Consolidate Execution into `unity_eval`**: All dynamic C# code execution, static method invocation, and inspection are funneled through `unity_eval`. The redundant `unity_execute_method` tool is retired.
- **Retire `unity_status` from MCP Catalog**: Autonomous agents frequently waste reasoning turns making pre-flight status calls. Because all execution tools auto-start Unity when not running and autowait for busy states, `unity_status` is removed from the public MCP catalog (while preserved internally for diagnostics and tests).
- **Proactive Compilation Checks via `unity_refresh`**: Merging clean rebuild capabilities into `unity_refresh(clean: bool = false)` avoids multiple compilation tools. Documenting that `unity_refresh` is fast (<200ms when unchanged) encourages agents to check compilation health after edits.

### `unity_eval` Script Model & Semantics
- **Top-Level Script Mental Model**: `unity_eval` accepts standard C# top-level script statements, supporting direct statement execution, asynchronous execution via top-level `await`, and returning values via `return <value>;`.
- **Explicit Returns**: Explicit returns (`return <expr>;`) are required to produce output payloads. Void execution returns an explicit diagnostic note (`"(Evaluation completed without a return statement...)"`), preventing confusion with `null` references.
- **No Implicit Default Namespaces**: To avoid hidden dependencies, compilation nondeterminism, and namespace collisions, dynamic snippets import no ambient default namespaces. Callers explicitly provide whatever `using` directives they require.
- **Separation of Concerns in Tool Schemas**: The tool description defines the complete execution contract (statements, await, return rules, using directives), while the parameter description remains strictly focused on text representation (plain text, avoiding JSON wrapping).

### Tiered Autowaiting for Unity Busy States
Tools handle Editor concurrency through tiered autowaiting rather than failing fast:
- **Transient Compilation & Lifecycle States**: When Unity is compiling or updating (`BUSY compile`, `COMPILING`, `UPDATING`), tools autowait indefinitely until compilation settles, streaming periodic progress notifications to keep the MCP transport alive.
- **Active Foreign Mutating Operations**: When an operation encounters an active foreign operation (`BUSY <kind> <opId>`), the client waits for a short grace period (3 seconds) to allow naturally completing operations to finish. If the lock persists, it fails fast with an actionable diagnostic pointing to `unity_stop`.
- **Refresh/Recompile Polling Priority**: Active refresh and recompile operations take precedence over operation ID matching in polling handlers, reporting uniform `COMPILING` status across domain reloads.

### Unified Compiler Diagnostic Formatting
Compiler output is formatted for optimal LLM context efficiency and navigability:
- **Warning Capping**: Warnings are capped at 10, with an omission notice (`... and X more warning(s) omitted...`) displayed only when exceeding the cap.
- **Error-First Attention Placement**: Warnings appear first, followed by unfiltered compilation errors at the end of the payload to maximize LLM recency attention.
- **RFC 8089 Navigation Links**: File locations are formatted as direct RFC 8089 URIs (`• file:///AbsolutePath#LLine: error CODE: msg`), allowing one-click IDE navigation while minimizing markdown link token bloat. Synthetic evaluation snippets use `snippet line X, col Y:` to avoid hallucinated file paths.

### Stack Trace Sanitization
Unity Test Framework appends 20–50 lines of internal NUnit runner plumbing below failing test assertions. The stack trace sanitizer:
- Anchors on framework runner markers (`NUnit.Framework.Internal.`, `UnityEditor.TestTools.TestRunner.`, `UnityEngine.TestRunner.`, `TestMethodCommand`).
- Scans backwards past immediate reflection invocation plumbing to locate the user's test entry point.
- Truncates all internal runner frames beneath that entry point while preserving assertion lines and nested helper calls with RFC 8089 URIs.
- Safely falls back to the original trace if no known runner markers are recognized.

### Interactive GUI Editor Termination Protection
When Unity runs in interactive GUI mode, terminating the Editor risks losing unsaved user work (scene edits, inspector changes). `unity_stop` inspects the target Editor mode:
- Refuses termination when running in `GUI` mode unless `force: true` is explicitly provided.
- Safe termination proceeds unprompted when running in `Batchmode`.

---

## 5. Testing Architecture & Performance Discipline

### Three-Tier Test Categorization
Tests are partitioned using xUnit traits to support fast local developer loops alongside comprehensive CI validation:
1. `Category=Unit`: Pure C# logic, codecs, formatters, reflection helpers, and path resolution. Executes in <3 seconds.
2. `Category=Subsystem`: Mock TCP loopback servers verifying wire protocols, tiered autowaiting, and cancellation logic without running Unity.
3. `Category=UnityIntegration`: Live Unity Editor smoke and end-to-end integration tests.

### Pre-Compiled Test Fixtures (Domain Reload Avoidance)
Modifying project files or creating dynamic script fixtures during test runs triggers asset importing and domain reloads (2–4 seconds each). Integration tests use statically compiled test fixtures (`DummyTest.cs`, `DummyExecuteClass.cs`) combined with NUnit test filters (`testNames`, `groupNames`, `categoryNames`), executing suite runs against pre-compiled assemblies in milliseconds without domain reloads.

### Persistent MCP Client Sessions
Spawning a fresh child process (`dotnet UnityLeanMcp.Mcp.dll`) per test incurs significant .NET CLR initialization and pipe handshake overhead. Reusing a persistent `McpTestClient` session per test fixture mirrors production MCP clients, tests socket stability under repeated requests, and substantially cuts suite execution time.

### Configurable Busy Grace Periods
Production defaults use a 3-second grace period when encountering foreign locks. `UnityClient` exposes a configurable `BusyGracePeriod` property, allowing subsystem tests to configure sub-100ms timeouts to test busy handling deterministically without artificial delays.

### Process Provider Isolation in Tests
`UnityProcessManager` uses an injectable `ProcessProvider` delegate rather than querying global system processes directly (`Process.GetProcessesByName("Unity")`). This prevents live Unity instances running on developer machines or CI agents from leaking into isolated test fixtures.
