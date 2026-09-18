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

### Transactional Unity Test Cancellation
Test cancellation is a two-phase request: the worker first durably records a cancellation intent, then the Unity main thread signals `TestRunnerApi`. The cancellation acknowledgement means that the request was accepted, not that the test run has finished. The test operation journal and running marker remain owned while Unity is `Cancelling`; they are released only from `RunFinished` or after the Test Runner reports a known inactive state. The intent and Test Runner job identity are persisted so a domain reload can resume the cancellation. Repeated requests for the same run are idempotent, and no elapsed-time fallback is used.

### Operation-Scoped Result Files & Atomic Delivery
Terminal results are persisted to operation-scoped file paths (`Temp/unity_refresh_<operationId>.json`, `Temp/unity_eval_<operationId>.json`, `Temp/unity_test_<operationId>.json`):
- Because each operation generates a fresh unique path, the destination file never exists beforehand.
- Writers write to a temporary file in the destination directory and move it into place via `File.Move(tempPath, targetPath)`.
- This eliminates the need for `File.Replace` (and underlying Win32 `ReplaceFileW`), avoiding mandatory replacement locks and sharing collisions with concurrent readers across all platforms.
- For test re-runs requiring historical context (such as `--failed-only`), the runner dual-writes results to both the operation-scoped file for client polling and the static `unity_test_results.json` for Editor history.
- Refresh and recompile dual-write their correlated operation result and the static `unity_refresh_result.json` history file; clients consume only the operation-scoped result. An uncorrelated `READY` is accepted only when the client was already waiting on a compilation state explicitly reported as active.
- Shared history snapshots use a best-effort, single-attempt move-overwrite publisher. The Editor package calls the platform-native replacement primitive (`MoveFileEx` on Windows and `rename` on Unix-like systems) because the overwrite overload added to newer .NET versions is unavailable under the package's Unity 2021.3 compatibility surface. A reader holding `unity_refresh_result.json` or `unity_test_results.json` open may prevent replacement on Windows; that failure is logged and ignored after the operation-scoped result is durable. The strict `WriteAtomic` path remains reserved for operation state and operation-scoped delivery.

### Operation Result Lifecycle & File Cleanup
Under state persistence, file-based IPC, and the interruption lifecycle, operation results follow distinct lifecycle and cleanup behaviors depending on their scope:
- **Operation-Scoped Result Files**: Operation-scoped result files (e.g. `unity_eval_<opId>.json`, `unity_test_run_<opId>.json`) are single-use and automatically deleted by `OperationPoller` upon terminal read to prevent disk clutter and avoid stale reads by future operations.
- **Shared Static Result Files**: Shared static result files (such as `unity_refresh_result.json`) persist Editor status across domain reloads and tool invocations, and must be preserved by default by `OperationPoller` (unless explicitly configured with `DeleteResultFileOnCompletion = true`).

### Centralized Project Transient Storage (`Temp/`)
All UnityLeanMcp runtime and temporary files (such as `unity_background_log.txt`, `unity_lean_mcp_operation.json`, `unity_compilation_errors.txt`, `unity_lean_mcp_port.txt`, `unity_lean_mcp_process.pid`, `unity_lean_mcp_startup.lock`, running/cancellation markers, worker log, and operation-scoped result files) are strictly isolated under the Unity project's `Temp/` directory. No temporary, diagnostic, or runtime log files are permitted at the Unity project repository root. Storing all transient state under `Temp/` prevents repository noise, simplifies repository cleanliness checks, prevents accidental version-control inclusion, and ensures Unity automatically cleans transient state with the rest of its engine cache across restarts.

### Cancellation During Initial Command Dispatch
The client must cancel an operation even when its caller token is canceled after the mutating command has been sent but before Unity returns the initial acknowledgement. In that window polling has not yet started, so the poller's cancellation hook cannot run; the command-specific dispatch path sends the correlated `CANCEL_OPERATION` request before rethrowing the caller cancellation. A cancellation request for an operation that was not accepted is harmless because Unity responds with no active operation.

### Resilient Inter-Process File I/O Retries
Files accessed across process boundaries (such as PID files, port discovery files, operation journals, lockfiles, and Editor logs) are subject to transient filesystem contention, atomic replacements, and external scanner interference:
- File read, write, and delete retry helpers (`ReadFileWithRetry`, `WriteAtomic`, `DeleteFileWithRetry`) must catch both `IOException` and `UnauthorizedAccessException` across intermediate retry attempts.
- On Windows NTFS, file access contention frequently manifests as Win32 `ERROR_ACCESS_DENIED` (surfaced as `UnauthorizedAccessException`) rather than `ERROR_SHARING_VIOLATION` (surfaced as `IOException`). Treating both exceptions as transient retry conditions ensures deterministic inter-process communication across platforms.

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

### Polymorphic Operation Result Symmetry (`IOperationResult`)
All command execution results implement `IOperationResult` (`OperationId`, `Success`, `Interrupted`, `Message`). Concrete results that map boolean interface flags to underlying domain status strings (such as `UnityTestRunResult.Interrupted` mapping to `ResultState` / `resultState`) must provide symmetric getters and setters: setting `Interrupted = false` when an operation was previously marked interrupted must restore the underlying state cleanly based on outcome (`Success` / `FailCount`), preventing sticky flag bugs across polymorphic consumers.

### Streamlined Path Resolution & Process Management (Interface Segregation Principle)
- **Type-Safe Result Path Resolution**: `IUnityPathResolver` avoids per-command property proliferation (`RefreshResultFile`, `EvalResultFile`, `TestResultsFile`, `GetEvalResultFile`, `GetTestResultsFile`) by consolidating result file resolution into a single method: `string GetResultFilePath(UnityOperationKind kind, string? operationId = null)`.
- **Compile-Time Safety via `UnityOperationKind`**: Using a strongly-typed enum (`Refresh`, `Recompile`, `Test`, `Eval`) ensures compile-time safety and exhaustive pattern matching across result path lookups, preventing typos and runtime drift inherent in stringly-typed APIs.
- **Strict Interface Segregation (ISP)**: `IUnityProcessManager` and `UnityProcessManager` focus strictly on process lifecycle management, process liveness, and socket readiness. Redundant forwarded path resolver properties and methods (`ProjectRoot`, `TempDir`, `PidFile`, `PortFile`, etc.) are eliminated from the manager contract; callers access filesystem paths directly through `processManager.PathResolver`.
- **Process Identity Persistence Boundary**: Unity PID sidecar serialization, atomic publication, cross-platform identity matching, and validated process-handle acquisition are isolated behind the internal `IUnityProcessIdentityStore`. `UnityProcessManager` retains startup and shutdown orchestration while its default file-backed store preserves the existing sidecar protocol.
- **Deterministic Process Handle Lifetime**: `System.Diagnostics.Process` handles allocated by `UnityProcessManager` during discovery (`FindProjectUnityPid`) or forced process termination (`StopUnityAsync`) must be explicitly and promptly disposed (via `using` or `finally` blocks) to prevent unmanaged operating system handle leaks during long-running sessions.

---

## 3. Threading, Transport & Dispatching

### Thread Separation & Main-Thread Affinity
- **Main Thread**: Serialized Unity API execution, scene manipulation, test execution, compilation triggers, and reflection dispatch.
- **Worker Thread**: Socket connection acceptance, protocol framing, lock-free polling of plain managed snapshots, and early rejection.
- Keeping socket acceptance off the Unity main thread ensures transport processing does not block Editor updates, compilation, or domain reloads.

### Worker-Thread Early Rejection
Commands declare metadata polymorphically via `ICommandHandler` (`IsMutating`, `RequiresCompilationSettled`):
- When a command arrives at the socket server, the worker thread inspects `handler.IsMutating` and checks `UnityLeanMcpOperationStore.ReadThreadSafeSnapshot()` before enqueuing to the main-thread dispatcher.
- `ReadThreadSafeSnapshot()` reads the durable operation file with the Unity-independent managed codec while holding the cache lock. Valid content is authoritative and refreshes the cache; missing or invalid content clears it. The cache is used only as a fallback when the file remains unreadable after the existing contention retries, preserving a genuinely active operation instead of reporting false `IDLE` during transient filesystem access failures.
- Conflicting requests (`BUSY <kind> <opId>` or `BUSY compile`) are rejected immediately on the worker thread.
- This prevents enqueuing onto the main-thread dispatcher when the main thread is occupied with synchronous execution, avoiding deadlocks and TCP client timeouts.

Worker-thread polling and cancellation use immutable managed snapshots decoded by a Unity-independent JSON codec. Cache misses read durable operation, test-running, and terminal-result files directly through managed file I/O; they must not fall back to `JsonUtility`, Unity logging, or any Unity main-thread service. Cancellation handlers only acknowledge an accepted request from the worker and enqueue Unity Test Runner and terminal persistence work to the main thread without waiting, so a synchronous command or domain reload cannot deadlock the socket worker.

Worker-thread diagnostics use the Unity-independent `WorkerDiagnosticsLogger`, which appends bounded, newline-normalized entries to `Temp/unity_lean_mcp_worker.log`. Writes are serialized by a managed lock and truncate the file in place before it exceeds its fixed byte budget, avoiding static-file replacement operations and keeping diagnostic growth bounded. The worker reads only path values captured during main-thread initialization; Unity `Debug` logging remains reserved for main-thread paths.

### Extensible Command Registry (Open-Closed Principle)
The socket server decouples command dispatch from concrete command implementations using an extensible, thread-safe command registry conforming to the Open-Closed Principle (OCP):
- `ICommandHandler` and `CommandExecutionTarget` are public interfaces, allowing external Unity editor assemblies and packages to implement and register custom commands without modifying `UnityLeanMcpServer.cs`.
- `UnityLeanMcpServer` provides thread-safe registration APIs (`RegisterHandler`, `UnregisterHandler`, `TryGetHandler`) backed by a `ConcurrentDictionary<string, ICommandHandler>` configured with `StringComparer.OrdinalIgnoreCase`.
- Built-in commands (`PING`, `EXIT`, `REFRESH`, `EVAL`, `RUN_TESTS`, etc.) are safely seeded during server initialization or on first registry access, remaining open to custom extension or override.
- Worker-thread request dispatch resolves commands via case-insensitive lookup, inspecting polymorphic handler properties (`ExecutionTarget`, `IsMutating`, `RequiresCompilationSettled`) to safely route or early-reject requests across threads.

### Explicit Main-Thread Service Initialization
Classes must not initialize main-thread Unity APIs in static constructors or static field initializers. The socket server must only begin accepting external connections after dependent main-thread services (`UnityLeanMcpPaths`, `UnityLeanMcpOperationStore`, `UnityLeanMcpCompilationTracker`, `UnityLeanMcpDispatcher`, `RoslynCompilerHelper`) have completed explicit `EnsureInitialized()` calls on the Unity main thread.

`UnityLeanMcpServer` uses a managed-only type initialization path and performs its Unity-dependent startup from an `[InitializeOnLoadMethod]` main-thread bootstrap. Public handler registration and lookup may safely occur before that hook without forcing construction of the built-in handler graph; custom registrations are retained and built-in defaults are added with non-overwriting inserts during bootstrap. The listener is opened only after dependency initialization, operation recovery, test callback registration, and lifecycle callback registration have completed. Domain reload creates a fresh bootstrap state and repeats the same ordered sequence.

`RoslynCompilerHelper` follows the same explicit lifecycle boundary while also protecting its reflection graph with a lock. It builds assemblies, reflected types, methods, options, and metadata references in private initialization state, publishes them only after the complete graph succeeds, and leaves the initialized marker clear after failure so a later access can retry.

### Single-Line Status Framing & Token Escaping
Line-oriented socket communication uses strict single-line framing:
- Status responses: `SUCCESS <payload>`, `FAILURE <message>`, `INTERRUPTION <message>`, `BUSY <reason>`.
- Payloads are compactly serialized (`JsonUtility.ToJson(result, false)`).
- Token codecs symmetrically escape and unescape `\\`, `\"`, `\r`, `\n`, and `\t`.
- Status token prefixes (e.g. `FAILURE <msg>`) must be stripped immediately upon response receipt so protocol framing does not leak into diagnostic parsers or error payloads.

### Clean Shutdown & Transport Decoupling
- Socket-initiated Editor shutdown (`EXIT`) stops listener services and calls `EditorApplication.Exit(0)` immediately. Standard quitting hooks mark in-flight operations as interrupted in durable state.
- A broken TCP connection indicates transport interruption, not that the underlying Unity operation failed. Clients rediscover the endpoint and resume polling by `operationId`.
- `StopServer` signals shutdown, closes the listener captured under a lifecycle lock, and waits without an elapsed-time cutoff for the server thread to exit. Port-file cleanup occurs only after that join; active clients are signaled and closed without joining client workers from Unity's main thread.
- Listener socket initialization on background threads configures `SO_REUSEADDR` conditionally using pure CLR `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`, enabling quick `TIME_WAIT` rebinding on POSIX while avoiding Winsock socket port hijacking vulnerabilities on Windows.

### Thread-Safe Operation Resource Tracking & Disposal
- Static references to active operation resources (such as cancellation token sources and console log captures) must be scoped strictly to the owning operation ID.
- Operations marking prior or foreign operations interrupted must verify that the target operation ID matches the active operation before resetting state or disposing resources.
- Cancellation triggers and resource disposal must be executed outside of synchronization locks (`s_CtsLock`) to prevent deadlocks caused by synchronous cancellation callbacks or event unsubscription contention.

### Project-Scoped Unity Startup Ownership

`UnityProcessManager` serializes auto-start attempts with an operating-system file claim at `Temp/unity_lean_mcp_startup.lock`, held with `FileShare.None` from before the ownership recheck through Editor readiness. The lock file is persistent and is never unlinked while held, preventing POSIX inode replacement races; the OS releases the handle automatically after a host crash. Every caller rechecks process ownership after acquiring the inter-process claim, so concurrent MCP hosts cannot launch multiple Editors for one project. The PID file is paired with an atomically published sidecar identity record containing the launched process ID, UTC start time, executable path, and project root; a live PID is accepted only when those values still match. A Unity lockfile or a single unproven process candidate is supporting evidence only and never establishes project ownership. Launched `Process` handles are disposed after readiness monitoring completes, while process handles supplied by injected test providers remain owned by the provider.

### Project-Scoped Endpoint Verification Before Process Heuristics

Before process discovery or batchmode auto-start, `UnityProcessManager` probes the port recorded in that project's `Temp/unity_lean_mcp_port.txt` with `PING` and accepts only `PONG`. This verified endpoint is authoritative evidence that the project's Editor is available, including interactive GUI Editors whose lockfile is empty or whose command line cannot be inspected. A port-file value alone never establishes availability: missing, invalid, unreachable, or non-`PONG` endpoints are stale and must fall through to durable identity, lockfile, and project-targeting process discovery before auto-start. Status and start results use the same endpoint-first rule so they cannot disagree about a live GUI Editor.

### Deterministic Unity Editor Executable Discovery

`UnityExecutableLocator` validates every configured, Unity Hub, and PATH candidate before returning it. Configured directory paths (e.g. `UNITY_PATH=/opt/unity` in containerized environments) automatically probe nested Editor binaries (`Editor/Unity`, `Unity`, `Editor/Unity.exe`, `Unity.exe`, `Unity.app/Contents/MacOS/Unity`) before evaluation. Validation uses the installed Editor layout rather than the executable filename alone: macOS candidates must be the binary inside `Unity.app/Contents/MacOS` with the managed `UnityEditor.dll` present, while Windows and Linux candidates must have the corresponding `Data/Managed/UnityEditor.dll` (or `UnityEngine.dll`) installation marker. POSIX candidates must also have an execute bit. This rejects same-named Unity CLI binaries and shims without executing untrusted candidates, and discovery preserves an actionable rejection diagnostic for auto-start failures. Discovery returns a strongly-typed immutable `UnityLocatorResult` (`ExecutablePath`, `Diagnostic`, `Success`) rather than mutating shared state or exposing mutable diagnostic properties on `IUnityExecutableLocator` (`LastDiagnostic`). This ensures atomic delivery of path or failure reasons, eliminates temporal coupling, and guarantees thread-safety when registered as a singleton service.

---

## 4. MCP Tool Surface & Agent Ergonomics

### Lean Public MCP Surface
To optimize LLM context window consumption and eliminate agent decision friction:
- **Consolidate Execution into `unity_eval`**: All dynamic C# code execution, static method invocation, and inspection are funneled through `unity_eval`. The redundant `unity_execute_method` tool and its raw protocol commands are removed.
- **Retire `unity_status` from MCP Catalog**: Autonomous agents frequently waste reasoning turns making pre-flight status calls. Because all execution tools auto-start Unity when not running and autowait for busy states, `unity_status` is removed from the public MCP catalog (while preserved internally for diagnostics and tests).
- **Proactive Compilation Checks via `unity_refresh`**: Merging clean rebuild capabilities into `unity_refresh(clean: bool = false)` avoids multiple compilation tools. Documenting that `unity_refresh` is fast (<200ms when unchanged) encourages agents to check compilation health after edits.
- **Compilation Diagnostic Synchronization**: When refreshing or recompiling, compilation error diagnostics published by Unity or background log scanners override optimistic success states, ensuring `UnityRefreshResult.Success` is synchronized to `false` whenever error-severity diagnostics or unparsed compilation errors are detected.

### Compiled-Assembly Source Synchronization

Every operation that consumes compiled user code (`unity_eval`, static execution, and `unity_run_tests`) begins with its own correlated normal `AssetDatabase.Refresh` operation and waits for that operation's durable terminal result before dispatch. A point-in-time `READY` readiness probe is insufficient: Unity can report idle before its asynchronous external-file watcher has noticed a changed source. The normal refresh remains intentionally non-clean, so unchanged projects retain Unity's inexpensive no-op refresh path while changed sources either compile or return their current compiler diagnostics. Passive readiness-check protocol variants do not exist: compiled-code consumers must always cross the correlated refresh barrier.

### Current-Client-Only Protocol Evolution

Protocol and client API revisions are current-client-only. When a command, overload, or input shape is replaced, its parser, adapter, fallback behavior, and tests are removed. The server does not translate deprecated requests or keep a conservative response path for an older client, because a visible incompatibility is more reliable than silently preserving an obsolete contract.

### MCP Configuration Architecture & Working-Directory Strategy
All MCP client configurations execute `dotnet` with `args: ["UnityLeanMcp.Mcp.dll"]` and set `cwd` to the package's `MCP~` directory, allowing the MCP server to automatically resolve the Unity project root from its current working directory.
- **Root Repository Variable Clients**: VS Code (`.vscode/mcp.json`, using `"servers"`) and Claude Code (`.mcp.json`, using `"mcpServers"`) support repository root variables (`${workspaceFolder}` and `${CLAUDE_PROJECT_DIR:-.}` respectively) within `cwd`, allowing tracked configurations to remain portable across developers and OS platforms when the package is located inside the checkout repository.
- **Absolute Path Clients**: Antigravity (`.agents/plugins/unity-lean-mcp/mcp_config.json`) and Cursor (`.cursor/mcp.json`) require absolute paths for `cwd`. Their generated machine-specific configuration files are excluded from version control; the Unity installer materializes them for the current package location.
- **External Package Fallback**: If the Unity package is resolved outside the repository checkout (such as in a package cache), all configurations fall back to machine-specific absolute paths in `cwd`.
- **Dynamic Package Path & MCP~ Resolution**: The Unity installer dynamically detects the package directory via `PackageInfo.FindForAssembly`, falling back to `AssetDatabase` script location search, standard embedded paths (`Packages/com.pereviader.unityleanmcp`), and UPM cache probing (`Library/PackageCache/com.pereviader.unityleanmcp*`). It probes both `MCP~` and lowercase `mcp~` for cross-platform filesystem case sensitivity.
- **Codex Configuration**: Codex's generated `.codex/config.toml` uses machine-specific absolute `cwd` and specifies `tool_timeout_sec = 1800`.

### Single-Source Package Release Versioning
`.env.shared` is the single authoritative release-version source for both preview (`major.minor.patch-preview.N`) and stable (`major.minor.patch`) package releases. The package version is not validated. At build time, `build.sh` reads the version from `.env.shared` and places it onto `package.json` in the generated build package.

### `unity_eval` Script Model & Semantics
- **Top-Level Script Mental Model**: `unity_eval` accepts standard C# top-level script statements, supporting direct statement execution, asynchronous execution via top-level `await`, and returning values via `return <value>;`.
- **Explicit Returns**: Explicit returns (`return <expr>;`) are required to produce output payloads. Void execution returns an explicit diagnostic note (`"(Evaluation completed without a return statement...)"`), preventing confusion with `null` references.
- **No Implicit Default Namespaces**: To avoid hidden dependencies, compilation nondeterminism, and namespace collisions, dynamic snippets import no ambient default namespaces. Callers explicitly provide whatever `using` directives they require.
- **Separation of Concerns in Tool Schemas**: The tool description defines the complete execution contract (statements, await, return rules, using directives), while the parameter description remains strictly focused on text representation (plain text, avoiding JSON wrapping).
- **Lossless Using Directive Blanking**: When snippets supply `using` directives at top-level, they are hoisted to namespace/file scope to prevent `CS1529`. Both AST and fallback regex blanking replace only matched directive characters with spaces (`' '`), ensuring exact 1:1 error line and column correspondence and preserving any statements placed on the same line.

### Extensible Result Formatter Registry (Open-Closed Principle)
Result formatting in `unity_eval` and method execution is decoupled from monolithic `if (result is ...)` cascades via an extensible, priority-based formatter registry conforming to the Open-Closed Principle (OCP):
- `IUnityTypeFormatter` defines the contract (`Priority`, `CanFormat(value)`, `Format(value, formatChild, prettyPrint)`), allowing external Unity editor assemblies and packages to register custom formatters for domain-specific or engine types without modifying `UnityResultFormatter.cs`.
- `UnityResultFormatter` provides thread-safe registration APIs (`RegisterFormatter`, `UnregisterFormatter`, `UnregisterFormatter<T>`, `ResetToDefaults`).
- Formatters are evaluated in descending order of `Priority`, falling back to `ToString()` if no custom or built-in formatter matches.
- High-frequency evaluation is lock-free via a `volatile` copy-on-write array snapshot (`s_SortedFormattersSnapshot`), eliminating lock contention during evaluation while ensuring thread-safe mutations.
- Fast paths for `null`, primitives, strings, decimals, enums, and destroyed `UnityEngine.Object` (`unityObj == null`) are evaluated early for maximum performance.
- Built-in type formatters are modularized: `TransformFormatter` (Priority 110), `GameObjectFormatter` (Priority 100), `ComponentFormatter` (Priority 90), `ScriptableObjectFormatter` (Priority 80), `SceneFormatter` (Priority 70), `SerializedObjectFormatter` (Priority 60), `SerializedPropertyFormatter` (Priority 50), `EnumerableFormatter` (Priority 40), and `JsonUtilityFallbackFormatter` (Priority -1000).
- Composite and collection formatters (such as `EnumerableFormatter`) recursively format child elements using the injected `Func<object, string> formatChild` delegate.

### Bounded, Cycle-Safe Result Formatting
`UnityResultFormatter.FormatResult` creates a fresh formatting context for every result and applies the same safeguards to all formatter paths, including enumerable children and the JSON fallback:
- Reference identity is tracked only for the active recursion path, so cycles produce an explicit marker while repeated references in separate branches remain valid.
- Configurable maximum depth and enumerable item count stop recursive or custom enumerable expansion deterministically without operation timeouts.
- Both UTF-16 character and UTF-8 byte limits are applied to the final formatted payload, with a truncation marker preserving a bounded MCP response.
- Public MCP text responses use a conservative 65,536-byte UTF-8 ceiling alongside the existing 65,536 UTF-16-character ceiling. The shared bounded builder counts bytes incrementally and truncates only at Unicode scalar boundaries, so multibyte and supplementary-plane output cannot expand the final AI payload or produce malformed surrogate pairs.
- Child formatters receive a context-bound delegate, ensuring built-in and registered composite formatters share cycle, depth, item, and output protections.

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

### Test Failure Detail Capping & Conciseness
When test suites experience large numbers of failures, emitting full stack traces for every test can consume 5,000–10,000 context tokens. To balance deep diagnostic information with token efficiency:
- **Detailed Failure Capping (Max 5)**: The first 5 failures report full diagnostic details, including execution duration, RFC 8089 source location links, full multi-line failure messages, and sanitized stack traces. Intermediate `StructuredTestFailure` diagnostic models are allocated strictly for this detailed subset.
- **Concise One-Line Summaries (Max 20)**: Failures 6 through 25 are formatted as concise single-line entries (`• {fullName}: {oneLineMessage}`), extracting the first non-empty message line (truncated to 200 characters if long) directly from test results without intermediate allocations, omitting stack traces and location links.
- **Reserved Summary Budget (16 KiB)**: When later summaries exist, the failure section reserves 16 KiB of the existing aggregate output cap for up to 20 compact summaries. Detailed failures use only the remaining capacity, so large first-failure stack traces cannot suppress later failure summaries. The reserved size covers the documented identifier and 200-character summary-message limits, including platform newline overhead. Runs with 5 or fewer failures have no summary section and retain the full aggregate capacity for details.
- **Total Failure Cap & Truncation (Max 25 Total)**: At most 25 failures total are listed in the response. Any failures exceeding 25 are truncated with an omission notice (`... and X more failed test(s).`). If 5 or fewer tests fail, all failures are rendered with full details without summary sections or truncation notices.

### Concise Test Outcome Summaries & Skipped Test Omission
Test execution summaries in `unity_run_tests` and progress notifications report pass, fail, and skip metrics cleanly. To optimize token usage and avoid visual clutter for autonomous agents, skipped test counts are omitted entirely when zero (e.g. `Tests Passed: 27 passed.` rather than `Tests Passed: 27 passed, 0 skipped.`, and `Tests Passed: 0 passed (no tests found in suite).`), and included only when tests were actually skipped (`Tests Passed: 27 passed, 1 skipped.`).

### Direct Diagnostic Formatter Separation & MCP Tool Decomposition
Diagnostic parsing and RFC 8089 URI formatting reside strictly within `IDiagnosticFormatter` / `DiagnosticFormatter.Default`. Redundant forwarding pass-through methods on `UnityTools` are removed, directing internal callers and tests to `DiagnosticFormatter.Default` directly. Tool execution methods (`UnityRefreshAsync`, `UnityEvalAsync`, `UnityRunTestsAsync`, `UnityStopAsync`) use uniform `Result` and `Error` helpers to ensure consistent `CallToolResult` creation and clean error propagation without catching `OperationCanceledException`. Complex tool formatting flows (such as `UnityRunTestsAsync` outcome header and failure sections) are decomposed into focused private helpers adhering strictly to the 16 KiB reserved summary budget, 5 detailed / 25 total failure caps, and surrogate-safe output truncation boundaries.

### Interactive GUI Editor Termination Protection
When Unity runs in interactive GUI mode, terminating the Editor risks losing unsaved user work (scene edits, inspector changes). `unity_stop` inspects the target Editor mode:
- Refuses termination when running in `GUI` mode unless `force: true` is explicitly provided.
- Safe termination proceeds unprompted when running in `Batchmode`.

### Canonical Typed Test Parameters
To make the public MCP schema directly actionable for tool-selection models:
- **Canonical Plural Parameter Surface**: `unity_run_tests` exposes only canonical plural filter parameters (`testNames`, `groupNames`, `categoryNames`, `assemblyNames`, `mode`, `failedOnly`). Deprecated singular or legacy aliases (`testName`, `group`, `filter`, `category`, `assembly`) are eliminated from the tool signature.
- **Accurate Regular Expression Documentation**: The `groupNames` parameter schema explicitly documents that patterns are evaluated by the Unity Test Framework as .NET Regular Expressions (e.g. `['.*Movement.*']`) rather than shell globs (e.g. `*Movement*`), preventing pre-execution regex compilation exceptions.
- **Native Array Schema**: Filter parameters use nullable `string[]` values so MCP clients receive `type: ["array", "null"]` with string items instead of an untyped custom-object schema. Empty or whitespace-only entries are rejected before refresh or Unity execution instead of broadening to an unfiltered suite.
- **String Enum Schema**: `mode` uses the `UnityTestMode` enum with JSON string values `all`, `editmode`, and `playmode`, so schema-aware clients can reject unsupported modes before dispatch. An explicit invalid enum value is still rejected at the tool boundary for defense in depth.

### Test Mode Validation
The public `unity_run_tests` tool preserves its documented omitted-parameter default of `all`, exposes only the accepted string enum values, and rejects invalid enum values before refreshing or dispatching work to Unity. The lower-level socket protocol continues to validate its string mode contract independently.

---

## 5. Testing Architecture & Performance Discipline

### Three-Tier Test Categorization
Tests are partitioned using xUnit traits to support fast local developer loops alongside comprehensive CI validation:
1. `Category=Unit`: Pure C# logic, codecs, formatters, reflection helpers, and path resolution. Executes in <3 seconds.
2. `Category=Subsystem`: Mock TCP loopback servers verifying wire protocols, tiered autowaiting, and cancellation logic without running Unity.
3. `Category=UnityIntegration`: Live Unity Editor smoke and end-to-end integration tests.

### Pre-Compiled Test Fixtures (Domain Reload Avoidance)
Modifying project files or creating dynamic script fixtures during test runs triggers asset importing and domain reloads (2–4 seconds each). Integration tests use statically compiled test fixtures (`DummyTest.cs`) combined with NUnit test filters (`testNames`, `groupNames`, `categoryNames`), executing suite runs against pre-compiled assemblies in milliseconds without domain reloads.

### Persistent MCP Client Sessions
Spawning a fresh child process (`dotnet UnityLeanMcp.Mcp.dll`) per test incurs significant .NET CLR initialization and pipe handshake overhead. Reusing a persistent `McpTestClient` session per test fixture mirrors production MCP clients, tests socket stability under repeated requests, and substantially cuts suite execution time.

### Explicit Refresh After Integration Fixture Edits

Integration fixtures that replace a Unity source file must await an explicit `unity_refresh` after the copy. Unity's external-file watcher and `projectChanged` callback are asynchronous; relying on a short delay can leave the readiness probe at `READY` and run tests against the previously compiled assembly. The refresh result may intentionally be an MCP error when the fixture represents a compile failure, so fixture setup must await completion without requiring success.

### Integration Tests Use the Published Release Package Artifact
Unity integration tests always publish the MCP server in `Release` configuration into the Unity package's `MCP~` directory before starting the shared client. All integration-test clients launch that package DLL exclusively; Debug build output and file timestamps are not considered. A non-zero publish exit code or missing published DLL fails fixture setup before Unity is started.

### Configurable Busy Grace Periods
Production defaults use a 3-second grace period when encountering foreign locks. `UnityClient` exposes a configurable `BusyGracePeriod` property, allowing subsystem tests to configure sub-100ms timeouts to test busy handling deterministically without artificial delays.

### Process Provider Isolation in Tests
`UnityProcessManager` uses an injectable `ProcessProvider` delegate rather than querying global system processes directly (`Process.GetProcessesByName("Unity")`). This prevents live Unity instances running on developer machines or CI agents from leaking into isolated test fixtures.

### Deterministic Child Processes in Process Tests
Process lifecycle tests that need a live child launch the checked-in test executable from `AppContext.BaseDirectory` with an explicit test-only argument. The child waits until its owning test terminates it, so the fixture does not depend on OS utilities, shell syntax, or machine-specific `PATH` entries.

### Held Project Lockfiles Prevent Conflicting Auto-Start

A lock held at the target project's `Temp/UnityLockfile` or `Temp/UnityLockFile` is sufficient evidence that starting a second Unity Editor would be unsafe, even if platform sandboxing prevents the MCP host from attributing that lock to a process. In that case, startup treats the Editor as active with an unknown PID and waits for its project-local MCP socket rather than launching batchmode. The lock is deliberately not treated as process ownership: it authorizes neither process termination nor deletion of Editor state.

### Atomic Support Evaluation & Service Segregation
- **Atomic Roslyn Support Evaluation**: `RoslynCompilerHelper` evaluates support status atomically via `RoslynSupportStatus(IsSupported, UnsupportedReason)`, eliminating split-return race conditions and temporal coupling.
- **Interface Segregation**: `IUnityExecutableLocator` is stripped down to `UnityLocatorResult FindUnityExecutable()`. `IUnityProcessManager` and `UnityProcessManager` remove pass-through and dead surface methods (`GetProjectEditorVersion()`, `StartUnityAsync()`, `WaitForHealthyAsync()`). Concrete methods remain available on implementing classes where appropriate.
- **Durable Refresh Results**: In-memory caching (`s_LastRefreshResult`) is eliminated in favor of operation-scoped durable result files (`Temp/unity_refresh_<opId>.json`) and operation journal state.
- **Transport-Decoupled Cancellation**: `IOperationLifecycleHandler.TryCancel` returns `OperationCancelResult` (`Cancelled`, `NotCancelable`, `NotFound`), keeping lifecycle handlers cleanly decoupled from `StreamWriter` transport logic.
- **Client Options & Test Seam Injection**: `UnityClient` accepts `UnityClientOptions(PollIntervalMs, BusyGracePeriod)` and exposes read-only properties. `UnityProcessManager` accepts `processProvider` and `processStarter` via constructor injection, preventing post-construction mutation of singleton test seams.

### Strict Protocol Contracts & Elimination of Backwards-Compatibility Bridges
- **Unified Operation Cancellation (`CANCEL_OPERATION`)**: Specialized cancellation commands (`CANCEL_TESTS`) and legacy aliases are completely removed. All operations (tests, eval, refresh, recompile) are canceled exclusively through the unified `CANCEL_OPERATION <operationId>` protocol command.
- **JSON-Only Payload for Test Runs (`RUN_TESTS`)**: `RUN_TESTS` requires a serialized JSON payload containing `RunTestsArgs`. Legacy CLI string argument parsing (e.g. `--filter`, `--category`, `--failed-only`) and ad-hoc command line tokenizers (`CommandLineTokenizer`, `CommandHelper.SplitArguments`) are eliminated, ensuring single-path payload handling across client and server.
- **Retirement of Legacy Execute-Method Remnants**: Dead artifacts from the retired `unity_execute_method` command (such as `ParameterTypeConverter`, `MethodResolver.FindStaticMethod`, and `ExecuteResultFile` path accessors) are completely removed rather than retained as compatibility deadweight.
- **Symmetric Array Filter Validation**: Filter values (`testNames`, `groupNames`, `categoryNames`, `assemblyNames`) reject `null`, empty, or whitespace-only elements synchronously on both the MCP client and Unity Editor handler. Null entries are never silently omitted or broadened into unfiltered runs.
- **Thread-Safe Cancellation Dispatch**: Socket workers executing `CANCEL_OPERATION` on a background thread record the cancellation intent durably via `WorkerThreadSnapshots.TryWriteTestCancellationRequest` and enqueue main-thread cancellation actions through `UnityLeanMcpDispatcher.Enqueue` rather than invoking main-thread-only Unity APIs (`TestRunnerApi.CancelTestRun`) directly on background worker threads.
- **Symmetric Refresh & Recompile Paths**: `UnityPathResolver` maps both `Refresh` and `Recompile` operations to `unity_refresh_result.json` / `unity_refresh_{operationId}.json`, matching the actual file emitted by the Editor package.
- **Complete Operation Interruption on Editor Shutdown**: Both voluntary editor exit (`EditorApplication.quitting`) and commanded exit (`ExitHandler.ExitUnity`) invoke `OperationLifecycleRegistry.NotifyQuitting` prior to server teardown. Lifecycle handlers write terminal interrupted results for test runs, AssetDatabase refreshes, and recompiles, ensuring that any polling client detects deterministic shutdown interruption rather than unexpected connection loss or unhandled operation state. If an operation has no active lifecycle handler, `NotifyQuitting` completes the operation record as a fail-safe against orphaned journal state.
- **Terminal Interrupted Refresh Recognition in Worker Cancellation**: `CancelOperationHandler` checks for durable interrupted refresh results (`IsTerminalInterruptedRefreshResult`) when the active operation store entry has cleared, ensuring idempotent cancellation responses across refresh and recompile operations.

### Deterministic Polling & Single-Pass Process Verification
- **Socket-First Liveness Probing in Operation Polling**: `OperationPoller` does not query the operating system process manager on every 500ms polling tick. If the TCP socket communicates successfully (returning `RUNNING`, `BUSY`, `COMPILING`, etc.), the Unity process is undeniably active. Process liveness is checked only when socket communication fails or drops (`pollResp == null`).
- **Immediate Terminal Returns in Custom Response Handlers**: When a custom response handler (e.g. `WaitForCompilationToSettleAsync`) identifies a valid terminal result upon observing `READY` or `COMPILATION_ERROR`, the result is returned and completed immediately, eliminating the artificial 500ms poll loop delay.
- **Single-Pass Typed Identity Verification**: Batchmode process detection in `IsUnityRunning` queries `_processIdentityStore.TryRead(out var identity)` directly to check PID ownership and project matching in a single pass without parsing loose text files or re-reading redundant state.
- **Relative Command-Line Project Path Resolution**: `UnityProcessManager.CommandLineTargetsProject` tokenizes command-line arguments to extract `-projectPath` values (including `-projectPath <val>` and `-projectPath=<val>`). Relative paths such as `.` or `./` are resolved against process working directory context and canonical path equality across all platforms.

### Implementation Simplifications & Sequence Unification
- **Unified Mutating Command Dispatch Pipeline (`DispatchMutatingCommandAsync`)**: The triplicated dispatch-and-retry loop across mutating client operations (`EvalAsync`, `RunTestsAsync`) is centralized in `UnityClient.DispatchMutatingCommandAsync`. It encapsulates operation-scoped ID generation, cancellation trapping, immediate synchronous result file detection, autowaiting on ongoing compilations and foreign operations, socket protocol status prefix stripping, and poller handoff.
- **Consolidated Terminal State Resolution (`ResolveTerminalStateAsync`)**: In `OperationPoller.PollOperationUntilTerminalAsync`, repeated disk checks for terminal results across `IDLE`, `ERROR`, `FAILURE`, and `SUCCESS` socket tokens are consolidated into `ResolveTerminalStateAsync`. This checks the durable disk file once for non-transient status tokens, preserving disk file authority over socket tokens while maintaining Windows NTFS directory settlement grace periods for `IDLE` responses.
- **Elimination of Shadow Worker Properties in Path Resolution**: Duplicate `Worker*` path properties (`WorkerOperationFile`, `WorkerDiagnosticsFile`, `WorkerPortFile`, etc.) in `UnityLeanMcpPaths` are eliminated in favor of canonical properties. Because `BootstrapOnMainThread` guarantees main-thread initialization before socket server threads or worker threads start, paths are cached once and can be accessed safely from any thread.
- **Unified Test Run Lifecycle Cleanup (`CleanupTestRun`)**: The 7-step test cleanup sequence across cancellation, interruption, and normal completion (`ClearCancellationRequest`, `StopCancellationMonitoring`, `DeleteRunningStateIfOwned`, `ClearCachedRunState`, `UnityLeanMcpOperationStore.Complete`, clearing job GUID, and resetting callbacks) is centralized into `RunTestsHandler.CleanupTestRun`.
- **Consolidated Operation Execution Fault Handling (`FailWithException` / `FailWithCancellation`)**: Error handling, exception unwrapping (including `TargetInvocationException`, single-item `AggregateException`, and nested unwrapping), cancellation detection, log capture disposal, and terminal failure publishing across multiple execution phases in `OperationExecutionEngine` are consolidated into `FailWithException` and `FailWithCancellation`. This eliminates duplicate catch/fault boilerplate across synchronous invocation, `ValueTask` conversion, task continuation, and task completion.
- **Immediate Terminal Result Extraction (`TryCreateImmediateTerminalResult`)**: Protocol status parsing for immediate `INTERRUPTION`, `ERROR`, and `FAILURE` responses across mutating dispatch operations (`RefreshAsync`, `DispatchMutatingCommandAsync`) is unified in a generic `TryCreateImmediateTerminalResult<TResult>` helper, eliminating duplicated status parsing and unescaping routines.
- **Direct Project Root Ownership in `UnityLeanMcpPaths`**: `ProjectRoot` resolution is owned directly by `UnityLeanMcpPaths` alongside all other transient and persistent paths, removing the intermediate indirection through `CommandHelper` and eliminating redundant `EnsureInitialized()` entry points.
- **Process Lifecycle & Socket Probe Deduplication**: In `UnityProcessManager`:
  - `WaitForSocketReadinessAsync` caches the socket probe result per iteration, avoiding redundant double probing of `IsSocketReadyAsync` when waiting on existing or GUI processes.
  - `IsUnityRunning` caches `IsFileLocked` and avoids duplicate `FindProjectUnityPid` scans across held lockfile detection and system fallback.
  - `GetUnityMode` checks `_processIdentityStore` directly to identify batchmode instances launched by the MCP server without redundant `IsOwnedPid` and process handle re-evaluations.
  - `StopUnityAsync` unifies graceful socket exit, pre-existing process exit, and fallback termination into a single shared completion point (`WaitForUnityExitAsync` -> `PurgeOperationState`).
- **Unbounded Dispatcher Wait & Elimination of Worker Context Switching**: In `UnityLeanMcpServer.ProcessClient`, the main-thread dispatch wait replaces the artificial 100ms polling loop with an unbounded wait on `{ finishedEvent, s_ShutdownEvent }`. Immediate awakening is preserved when `s_ShutdownEvent` signals domain reload or Editor shutdown, eliminating thread context switching and unnecessary CPU wakeups.
- **Direct Mutating Refresh Dispatch Without Redundant Pre-Flight Probe**: In `UnityClient.RefreshAsync`, the redundant pre-flight `POLL_REFRESH` check before command dispatch is eliminated. The primary mutating command (`REFRESH` / `RECOMPILE`) is dispatched directly and is early-rejected by the server worker thread with `BUSY` if another operation or compilation is active, saving a full socket roundtrip per refresh call while preserving all autowaiting and grace-period guarantees.
- **Centralized Protocol Status Decoding (`StripStatusPrefix` / `TryCreateImmediateTerminalResult`)**: Protocol status parsing and prefix stripping (`ERROR:`, `FAILURE:`, `SUCCESS:`, and single-line whitespace separation) across command dispatch and terminal polling are consolidated into `ProtocolCodec.StripStatusPrefix` and `ProtocolCodec.TryCreateImmediateTerminalResult`. This eliminates duplicated slicing logic in `UnityClient` and `OperationPoller` and fixes a latent bug where naive 7-character slicing of `SUCCESS:` left a leading colon in success payloads.
- **Unified Compilation Command Handlers (`CompilationCommandHandlerBase`)**: `RefreshHandler` and `RecompileHandler` share identical request validation, operation journal ownership, busy checks, diagnostic clearing, and settlement observation logic. This common lifecycle is unified in `CompilationCommandHandlerBase`, allowing concrete handlers to specify only their operation kind, status word, and underlying compilation trigger (`AssetDatabase.Refresh()` vs `CompilationPipeline.RequestScriptCompilation()`).
- **Pre-Cached Roslyn Reflection Members & Metadata Reference Array**: `RoslynCompilerHelper` pre-caches `CSharpSyntaxTree.GetRoot`, `Microsoft.CodeAnalysis.SyntaxNode.DescendantNodes`, and the typed `MetadataReference[]` array during transactional initialization (`TryInitialize()`). `CompileAndEmit` deduplicates syntax tree parsing via `ParseSyntaxTree` and reuses `s_CachedMetadataReferenceArray` directly in compilation invocation rather than re-scanning AST methods, re-allocating reflection argument arrays, and copying metadata references into fresh arrays on every compilation.
- **Centralized Test Infrastructure (`MockUnityServer` & `UnityTestStubs`)**:
  - `MockUnityServer`: Consolidates loopback TCP server initialization, dynamic port allocation, trusted PID/identity file generation, `Temp/Assets/ProjectSettings` directory layout creation, request dispatching, default fallback command handling (`PING` -> `PONG`, `REFRESH` -> `REFRESHING`, `POLL_REFRESH` -> auto result file creation and `READY`), and typed helper result writers (`WriteRefreshResult`, `WriteEvalResult`, `WriteTestResult`, `WriteTestRunning`). Provides clean asynchronous disposal (`IAsyncDisposable` / `IDisposable`) with socket shutdown, connection draining, cancellation, and resilient folder deletion with retry loops.
  - Test Suite Boilerplate Elimination: Replaces verbose, hand-rolled TCP socket accept loops, listener start/stop, and manual temp directory cleanup across `TieredAutowaitingTests`, `ToolProgressTests`, `TestRunProgressTests`, and `TestRunErrorAndIdleTests` with standard `await using var server = await MockUnityServer.StartAsync(...)`.
  - Shared Test Stubs (`UnityTestStubs`): Consolidates duplicate mock implementations (`StubProcessManager`, `RecordingOperationPoller`) into a dedicated internal `UnityTestStubs.cs` accessible across subsystem test suites, eliminating copy-pasted private mock classes.
