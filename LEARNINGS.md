# Non-Obvious Learnings & Platform Edge Cases

This document records non-obvious edge cases, platform quirks, and unexpected framework behaviors discovered across Unity, operating systems (Windows, Linux, macOS), .NET / CLR, Roslyn, and the Unity Test Framework.

---

## 1. Unity Editor & Domain Reload Quirks

### Managed Domain Reload Wipes Managed State
Unity's managed domain reload is asynchronous and can be triggered externally at any moment (script edits, compilation, asset importing, Editor focus, or shutdown).
- Static fields, callbacks, background threads, TCP sockets, and in-memory managed objects do not survive domain reloads.
- Any operation spanning a domain reload must persist its identity and minimal recovery state outside ordinary managed memory (e.g. on disk or in `SessionState`) before initiating the action.

### `SessionState` vs. Process Restart
- Unity's `SessionState` survives managed domain reloads within the same Editor process.
- However, `SessionState` is completely reset when the Unity Editor process restarts.
- Persisting an Editor process session identifier in `SessionState` allows distinguishing same-process domain reload recovery from a full Editor restart.

### `AssemblyReloadEvents.beforeAssemblyReload` Lifecycle Restrictions
`AssemblyReloadEvents.beforeAssemblyReload` executes just before the old managed domain unloads, but very little managed execution lifetime remains. It can only be relied upon for small, idempotent durable state flushes and transport shutdowns; never depend on a later callback executing in the unloading domain.

Listener shutdown must synchronize publication with teardown. If the server thread can assign its listener after a reload hook observes `null`, it may recreate the endpoint or rewrite the port file after shutdown cleanup. Capture the listener under a lifecycle lock, close it, wait for the server thread to exit, and only then remove endpoint metadata. Client workers should be signaled and socket-closed without being joined from the Unity main thread.

### Unity Test Framework Assembly Reload Lock
Unity locks managed assembly reloads while tests are actively running. Script modifications made during a test run are queued until the Test Framework completes and releases the lock. Consequently, a test modifying a script during execution does not test mid-test domain reload recovery.

### Play Mode Exit Nuances & Stalling
- When requesting Play Mode exit, waiting solely for `EditorApplication.isPlaying == false` is insufficient. Both `EditorApplication.isPlaying` and `EditorApplication.isPlayingOrWillChangePlaymode` must be observed as `false` before starting operations requiring Edit Mode.
- Play Mode exit cannot be assumed to complete within an arbitrary fixed deadline: user scripts (`Application.wantsToQuit` returning `false`), open modal dialogs, or infinite loops can delay or prevent Play Mode exit. Operations must remain active and truth-telling rather than aborting on arbitrary timers.

### Transient Lifecycle Flags
Flags such as `EditorApplication.isCompiling` and `EditorApplication.isUpdating` are transient point-in-time observations. They frequently evaluate to `false` immediately before compilation begins and briefly between internal lifecycle phases. Never conclude that compilation or refresh has completed on the first `false` reading; require the request flag to clear and observe an idle state across multiple update ticks.

### A `READY` Probe Cannot Prove Compiled Sources Are Current
Unity's external-file watcher and `projectChanged` notification can arrive after a worker-thread readiness probe observes no pending refresh or compilation. Using that one-time `READY` response to skip preparation lets a compiled-code operation execute an old assembly. `unity_eval`, static execution, and test execution must therefore issue a correlated normal `AssetDatabase.Refresh` and await its durable outcome; an unchanged project takes Unity's no-op refresh path, while changed sources compile or report current diagnostics before execution begins. Do not retain an obsolete readiness-check protocol as a conservative fallback: remove the parser entirely so every client uses the current barrier contract.

### Compiler Diagnostic Capture Timing
Compiler diagnostics must be captured during `CompilationPipeline.assemblyCompilationFinished`, before the domain reload occurs. Waiting for `CompilationPipeline.compilationFinished` or a subsequent Editor update tick is too late: the domain reload unloads the assembly context, causing compiler warnings from the old domain to be lost.

### Asynchronous Compilation Failure Overrides Optimistic Refresh Success
- When triggering AssetDatabase refresh or script recompilation, Unity may return an initial `READY` or success status while compilation errors are asynchronously published to the diagnostics file (`unity_compilation_errors.txt`).
- Client refresh handlers must parse compilation diagnostics upon operation completion and demote `UnityRefreshResult.Success` to `false` if any error-level diagnostics or unparsed compilation errors exist, preventing false-positive success reporting when builds fail.

### Refresh Poll Responses Must Not Stand In For Correlated Results
`POLL_REFRESH <operationId>` can return a generic `READY` after the operation journal has been cleared. A shared static refresh result or that generic response is not evidence that the requested operation completed: it may belong to an earlier request. Refresh completion therefore requires the operation-scoped durable result; only a wait that began from an explicitly observed pre-existing compilation may derive its outcome from the newly observed current compilation state.

---

## 2. CLR, Threading & Roslyn Quirks

### `[InitializeOnLoad]` Static Constructor Background Thread Poisoning
Unity does not guarantee execution ordering for `[InitializeOnLoad]` classes across assemblies.
- If a background thread (such as a TCP listener thread) references a static class before Unity's main thread runs its static constructor, the CLR executes that static constructor on the background thread.
- If the static constructor calls main-thread-affine Unity APIs (`EditorUtility`, `SessionState`, `EditorApplication`, `Application.dataPath`), Unity throws a `UnityException`.
- This unhandled exception permanently poisons the type with a CLR `TypeInitializationException` for the entire remaining lifetime of that domain.
- **Rule**: Helper and path classes must eliminate static constructors and static field initializers, defaulting fields to neutral values and relying on explicit main-thread `EnsureInitialized()` invocations during startup.

The server itself must follow the same boundary. A public registry call can be the first reference from an external assembly or a background listener thread, so `UnityLeanMcpServer` cannot use its static constructor as the Unity startup hook. Use `[InitializeOnLoadMethod]` for an explicit main-thread bootstrap, keep pre-bootstrap registration and lookup as a lock-free managed dictionary operation without eagerly constructing the built-in handler graph, and add defaults with non-overwriting inserts on the main thread. Start the socket only after all dependent services and reload/quitting callbacks are registered. A load-time probe must not join a worker from an `[InitializeOnLoad]` callback because CLR type loading can require the Unity main thread; if such coverage is needed, let the worker run asynchronously and join only from a test body. On each domain reload, static bootstrap state is recreated and the ordered initialization sequence must run again.

### Unity API Main-Thread Affinity
Virtually all Unity APIs are strictly main-thread-affine unless explicitly documented otherwise. This includes APIs that resemble standalone utility code, such as Unity JSON serialization (`JsonUtility`) and data path retrieval (`Application.dataPath`). Background threads must never invoke Unity engine APIs directly.

### Worker-Thread Operation Store Snapshot Caching
- `UnityLeanMcpOperationStore.ReadThreadSafeSnapshot()` is invoked from background TCP listener threads to inspect active operation state during command dispatch and busy checks.
- `ReadThreadSafeSnapshot()` must not trust a non-null in-memory state without checking the durable operation file: external cleanup, domain reload recovery, or malformed writes can otherwise leave stale ownership visible to later polls.
- The managed worker reader classifies durable state as valid, missing, invalid, or temporarily unavailable. Valid state refreshes the cache, missing/invalid state clears it, and only temporary unavailability may return a previously valid active record. This preserves a genuinely active operation during filesystem contention without allowing an unreadable-but-removed journal to remain cached forever.

### Roslyn Reflection Traps
When calling Roslyn APIs via reflection across Unity Editor versions:
- **`CSharpSyntaxTree.GetRoot`**: Has an optional parameter (`GetRoot(CancellationToken cancellationToken = default)`). Reflective lookup specifying 0 parameters (`new Type[0]`) returns `null`. The reflection lookup must explicitly match `GetRoot(CancellationToken)` and supply `default(CancellationToken)`.
- **`SyntaxNode.DescendantNodes`**: Has multiple overloads, including `DescendantNodes(Func<SyntaxNode, bool>, bool)` (2 parameters) and `DescendantNodes(TextSpan, ...)` (3 parameters). Loose reflection matching can latch onto the 3-parameter overload; passing a default `TextSpan` traverses an empty span `[0..0)`, yielding zero nodes. Reflection must strictly match the 2-parameter overload and pass `new object[] { null, false }`.

Roslyn reflection initialization must be serialized and transactional. Build the complete set of loaded assemblies, reflected methods/types, compiler options, and metadata references in local state before publishing shared fields. Set the initialized marker only after every step succeeds; otherwise a transient Unity startup or assembly-load failure becomes a permanent unsupported state. Leaving the marker clear allows a later access to retry without exposing a partially initialized compiler to concurrent callers.

### Dynamic Evaluation Assembly Lifetime
Unity 2021.3 and later supported Editor runtimes do not provide one unloadable assembly-isolation API that can safely be used across all supported Mono/.NET configurations while preserving access to Unity objects. `Assembly.Load(byte[])` therefore remains the compatible evaluation load path: each generated evaluation assembly remains in the current managed domain until Unity performs a domain reload. No per-evaluation unloading guarantee is made, and no timeout-based cleanup is used; the Roslyn lifecycle fix is limited to reliable initialization and retry.

### Roslyn `CS1529` Using Directive Hoisting & Whitespace Blanking
When users or AI agents provide standard C# source code containing `using` directives at the top (e.g. `using System.IO;`):
- Placing them inside a generated runner method body causes Roslyn error `CS1529: A using directive must precede all other elements defined in the namespace`.
- Directives must be extracted (matching `UsingDirectiveSyntax` while ignoring `UsingStatementSyntax` and `LocalDeclarationStatementSyntax`) and hoisted to file scope before the class declaration.
- Extracted directive characters must be replaced with spaces rather than deleted (both in the Roslyn AST parser and in the regex-based fallback `ExtractUsingDirectivesFallback`). Replacing entire lines with empty strings deletes any code sharing that line (e.g. `using System; int x = 42;`) and shifts column offsets. Blanking only matched directive spans (`charArray[c] = ' '`) and looping per line preserves multiple directives, trailing statements, total line count, and 1:1 column positioning across both parsing strategies.

### Optional Package Assemblies in Headless Environments
In minimalist Unity installations (headless, server, batchmode, or VR builds), package-modular assemblies such as `UnityEngine.UI.dll` (from `com.unity.ugui`) may not be installed or loaded. Emitting unconditional `using UnityEngine.UI;` in dynamically compiled Roslyn wrappers triggers compiler error `CS0234`. Dynamic code wrappers must avoid ambient `using` directives or conditionally probe assembly metadata before emitting optional namespace imports.

### Type Hierarchy Formatter Matching Order
In hierarchical type matchers, `UnityEngine.Transform` inherits from `UnityEngine.Component`.
- In the decomposed `IUnityTypeFormatter` architecture, `TransformFormatter.Priority` must be strictly higher than `ComponentFormatter.Priority` (e.g. 110 vs 90).
- If priorities were equal or inverted, `ComponentFormatter.CanFormat(value)` would return `true` for a `Transform`, capturing the instance and misformatting it with generic component inspection logic rather than rendering child count and local transform coordinates.

### Extensible Type Formatters & `[InitializeOnLoad]` Execution Order
When providing static registration APIs (`RegisterFormatter`, `UnregisterFormatter`) on `UnityResultFormatter`:
- External packages and editor scripts may register custom formatters in their own `[InitializeOnLoad]` static constructors before or after `UnityLeanMcpServer` or `UnityResultFormatter` initializes.
- Default built-in formatters must be seeded safely via thread-safe lazy initialization (e.g. `EnsureDefaultFormatters()`) before external registration, unregistration, or formatting occurs, preventing late-executing default initializers from overwriting custom registrations.
- Formatting occurs frequently during interactive eval loops. Caching sorted formatters in a volatile copy-on-write array snapshot (`s_SortedFormattersSnapshot`) allows evaluations to iterate formatters lock-free without thread contention.

### Result Formatter Graph and Output Limits
Enumerable results can contain themselves directly or through custom enumerators, and Unity's JSON fallback can produce a large serialized string before the MCP layer sees it. A formatting call must carry one shared context through every child delegate: use reference identity for the active path (not value equality), cap recursive depth and enumerable items, and enforce both character and UTF-8 byte budgets on the returned payload. Returning an explicit truncation marker keeps eval and execute responses deterministic and prevents accidental context flooding; the item cap also bounds normal custom enumerators without relying on arbitrary elapsed-time cutoffs.

### Public MCP UTF-8 Output Boundary
The final public response builder must enforce both the 65,536 UTF-16-character cap and a conservative 65,536-byte UTF-8 cap. Character-count prefixes are not sufficient for CJK or supplementary-plane output: every bounded entry point, including field truncation helpers and `McpOutputLimits.Truncate`, must back up before a high surrogate and never append a partial surrogate pair. The existing aggregate truncation marker remains the deterministic signal for either limit.

### `stackalloc` Outside Loops (`CA2014`)
In .NET / CLR, stack memory allocated via `stackalloc` is not reclaimed until the containing method exits, rather than when the enclosing block or loop iteration ends. Allocating small stack buffers (such as `stackalloc byte[4]` for UTF-8 character encoding) inside a loop causes cumulative stack growth with each iteration and triggers compiler warning `CA2014` (`Do not use stackalloc in loops`). Declaring the fixed-size span once prior to the loop allows every iteration to safely reuse the same stack buffer with zero heap allocations, zero cumulative stack consumption, and zero compiler warnings.

### Operation-Scoped Resource Lifetime & Out-of-Lock Disposal
When managing static references to active operation resources (such as `ConsoleLogCapture` or `CancellationTokenSource`) across asynchronous, domain-reloaded, or interrupted operations:
- Methods marking operations interrupted (e.g. `MarkInterrupted`) must strictly verify that `targetOperationId` matches the currently active operation before disposing static runtime resources. Disposing `s_ActiveLogCapture` unconditionally when `targetOperationId` does not match causes active, concurrent operations to lose log capture mid-flight.
- Never invoke disposable or cancelable callbacks (such as `CancellationTokenSource.Cancel()`, `CancellationTokenSource.Dispose()`, or `ConsoleLogCapture.Dispose()`) while holding internal synchronization locks (`s_CtsLock`). Cancellation callbacks or event unsubscriptions can execute external code or cause lock contention; resources should be extracted and nulled within the lock and disposed safely outside of it.

### Extensible Command Handlers & `[InitializeOnLoad]` Execution Order
When providing static registration APIs (`RegisterHandler`, `UnregisterHandler`, `TryGetHandler`) on classes decorated with `[InitializeOnLoad]`:
- External packages and editor scripts may register custom command handlers in their own `[InitializeOnLoad]` static constructors before or after `UnityLeanMcpServer` initializes.
- Default built-in handlers must be seeded safely via thread-safe lazy initialization (e.g. `EnsureDefaultHandlers()`) prior to any external registration, retrieval, or unregistration.
- This ensures external custom command registrations are neither lost nor overwritten by late-executing default initializers.

### Worker-Thread Polling and Cancellation Boundary

Worker command handlers can run while the Unity main thread is synchronously executing an operation. Calling `JsonUtility`, Unity logging, or cache-miss persistence readers from these handlers can throw thread-affinity exceptions or deadlock during domain reload. Worker paths therefore need managed immutable snapshots and a Unity-independent JSON reader for durable operation, test-running, and terminal-result files. Test cancellation must enqueue the Unity Test Runner call and terminal persistence to the main-thread dispatcher without waiting for completion; the worker response acknowledges request acceptance, while the durable result remains authoritative for the eventual terminal state.

### Worker-Thread Diagnostic Logging

`UnityEngine.Debug` is not a safe diagnostic sink for the socket listener or client worker threads, including exception handlers: Unity may be reloading or may reject the call because it is not on the main thread. Worker diagnostics must use a managed-only sink with a synchronized, bounded file append and must read only path values captured during explicit main-thread initialization. Logging is best effort and must never mask or replace the transport exception being reported.

### Unbounded Dispatcher Waits via Cooperative Shutdown Handles
In `UnityLeanMcpServer.ProcessClient`, polling loops with artificial timeouts (e.g. `while (WaitHandle.WaitAny(handles, 100) == WaitHandle.WaitTimeout)`) introduce unnecessary thread context switching and thread wakeups while waiting for main-thread execution. Because `s_ShutdownEvent` is signaled immediately upon domain reload (`beforeAssemblyReload`) or Editor quitting, an unbounded wait `WaitHandle.WaitAny(new WaitHandle[] { finishedEvent, s_ShutdownEvent })` wakes immediately upon either normal dispatcher completion or shutdown signaling. Checking `completedIndex == 1 || s_ShutdownEvent.WaitOne(0) || IsShuttingDown()` guarantees immediate exit during domain unload while avoiding spin-wait overhead.

---

## 3. Operating System & Filesystem Quirks

### Windows NTFS File Locking, `ReplaceFileW`, & `DELETE_PENDING`
On Windows (NTFS / Win32):
- `File.Replace` (backed by Win32 `ReplaceFileW`) requires exclusive write access to the destination file. If another process or background thread has the destination file open—even with `FileShare.ReadWrite`—`ReplaceFileW` fails with `ERROR_SHARING_VIOLATION`.
- `File.Delete` places files into a transient `DELETE_PENDING` state until all open handles close. During this window, subsequent file creation or replacement attempts fail with sharing violations, and concurrent readers observe 0-byte files.
- **Solution**: Avoid static shared result files. Use operation-scoped unique result paths (`Temp/unity_<kind>_<opId>.json`) and atomically move temporary files into place via `File.Move(tempPath, targetPath)`. Moving to an uncreated path avoids `ReplaceFileW` and requires no exclusive replacement locks.

### Windows NTFS Transient Contention & `UnauthorizedAccessException` vs `IOException`
On Windows NTFS, transient file contention (such as anti-virus scanning, Windows Search indexing, or atomic file replacements via move/delete) frequently manifests as Win32 `ERROR_ACCESS_DENIED` (5), which the .NET CLR surfaces as `UnauthorizedAccessException` rather than `IOException` (which usually wraps `ERROR_SHARING_VIOLATION` (32) or `ERROR_LOCK_VIOLATION` (33)).
- File retry routines (e.g. `UnityProcessManager.ReadFileWithRetry`, `CommandHelper.ReadFileWithRetry`, `UnityLeanMcpOperationStore.WriteAtomic`) must catch both `IOException` and `UnauthorizedAccessException` during intermediate retries.
- Catching only `IOException` causes intermittent read failures on Windows when reading PID, port, lock, or log files while another process briefly holds an inspection or deletion handle.

### Windows Locked Assembly Renaming Workaround
On Windows, when publishing a .NET assembly (e.g. `dotnet publish ... -o .../MCP~`) while the target DLL is held open by a running host process (such as an IDE language server or MCP runner), file replacement fails with `MSB3021` / `MSB3026`.
- Windows allows renaming files that were opened with `FILE_SHARE_DELETE` (the standard sharing flag used by the .NET CLR when loading assemblies).
- Renaming the locked target DLL to `<name>.old` prior to publishing allows the build to succeed immediately without killing the host process.

### Cross-Platform Path Rooting & Unix `Path.IsPathRooted` Drive Letter Quirk
In .NET on Unix-like environments (Linux, macOS):
- `Path.IsPathRooted` returns `true` only for paths starting with `/` (POSIX separator). It returns `false` for Windows-style rooted paths with drive letters (e.g. `C:\...` or `C:/...`).
- When normalizing or converting paths to RFC 8089 URIs (`file:///...`), relying solely on `Path.IsPathRooted` on Linux causes Windows paths to be treated as relative, incorrectly prefixing them with the working directory (e.g. `/Repo/C:/Repo/...`).
- Code must use cross-platform path rooting checks that inspect drive letters (`^[A-Za-z]:[\\/]`) and leading slashes before prefixing base paths.

### Line Ending Heterogeneity
`StringBuilder.AppendLine()` uses `\r\n` on Windows. Concatenating strings using hardcoded `\n` alongside `AppendLine()` creates mixed line endings within single payloads, causing regex mismatches or broken line-oriented protocol framing. Consistently use `Environment.NewLine`.

### POSIX Shell Syntax: Function-Only Keywords
In POSIX shell scripts across Linux, macOS, and Git Bash:
- Declaring function-only keywords like `local` in top-level script scope causes syntax errors on strict POSIX shells.
- All variable scoping must adhere to POSIX standards when executing outside function definitions.

### Unity Lockfiles
Unity lockfiles (`UnityLockfile`) are platform-dependent:
- They may be 0 bytes, omit owner PID information, disappear briefly during startup or domain reload, or remain orphaned after an abnormal Editor crash.
- Lockfiles should be treated as supporting evidence, never as authoritative proof of process liveness.

### Interactive Editor Socket Discovery Precedes Lockfile and Process Inspection
An interactive Unity Editor can publish a valid project-local MCP port while its lockfile is zero bytes and its process command line cannot be read (notably on macOS). Treating failed PID/lockfile/process inspection as proof that Unity is absent causes an MCP host to launch a conflicting batchmode Editor. The project `Temp/unity_lean_mcp_port.txt` is only a candidate endpoint, but a successful loopback `PING` / `PONG` exchange through that port is positive evidence of the live project Editor and must be attempted before process heuristics. A missing, invalid, unreachable, or non-`PONG` endpoint remains stale supporting data and must not suppress normal discovery or auto-start.

### Wire Protocol Backslash Escaping on Windows
In line-oriented protocols where single-line status responses encode string payloads:
- Failing to escape backslashes (`\\` -> `\\\\`) causes Windows path separators (`C:\new\read`) to be unescaped into newline (`\n` -> `ew`) and carriage return (`\r` -> `ead`) control characters, corrupting filesystem paths.
- Codecs must symmetrically escape and unescape `\\`, `\"`, `\r`, `\n`, and `\t`.

### Winsock `SO_REUSEADDR` Port Hijacking on Windows
On POSIX platforms (Linux, macOS), setting `SO_REUSEADDR` allows a socket to immediately rebind to a local port in `TIME_WAIT` state (e.g. following quick restarts or domain reloads).
- On Windows (Winsock), `SO_REUSEADDR` behaves fundamentally differently: it permits multiple sockets across different processes to bind simultaneously to the exact same IP:port even while another process is actively listening (`listen()`), enabling socket port hijacking and traffic theft.
- Never set `SO_REUSEADDR` on Windows TCP listeners. Restrict `SO_REUSEADDR` to non-Windows platforms.
- Background listener threads must inspect the OS via pure CLR `!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` rather than Unity's `Application.platform`, preserving main-thread affinity rules.

### `System.Diagnostics.Process` Handle Disposal & `HasExited`
- Calling `Process.GetProcessesByName(...)` or `Process.GetProcessById(...)` allocates native OS process handles wrapped in `Process` instances. These handles are not automatically released until GC/finalization unless explicitly disposed via `Process.Dispose()`.
- Once a `Process` instance is disposed, its `SafeProcessHandle` is closed. Subsequent queries to properties like `proc.HasExited` throw `InvalidOperationException: No process is associated with this object.`
- Methods allocating system `Process` candidate arrays internally (such as `FindProjectUnityPid`) must deterministically dispose all allocated instances in a `finally` block when ownership is retained, while external candidates or provider delegates injected for testing must retain caller ownership to avoid invalidating caller assertions.

### Shared Static Result Files vs Operation-Scoped Result Files in Polling Engines
In file system, concurrency, and inter-process communication (IPC) polling engines:
- Issuing a blanket `File.Delete(resultFilePath)` in polling engines is harmful for static result files: static files like `unity_refresh_result.json` record the last known Editor compilation/refresh state across sessions, and deleting them after a single tool call wipes out the record for subsequent calls or diagnostic fallback readers.
- Polling engines must distinguish operation-scoped files (bearing the operation ID, e.g. `unity_eval_<opId>.json`, `unity_test_<opId>.json`) from shared static files. Operation-scoped files are single-use and safely unlinked after terminal consumption to prevent disk clutter and stale reads, whereas shared static result files persist Editor status across domain reloads and tool invocations, and must be preserved by default unless single-use deletion is explicitly configured (`DeleteResultFileOnCompletion = true`).

### Shared Static History Publishing Under Windows Read Contention

Shared history files are non-authoritative snapshots, not part of the operation's durable delivery contract. Publishing them with `File.Replace` can fail when a Windows reader holds the existing file without delete sharing. History writers therefore stage content in a unique temporary file and make one move-overwrite attempt; contention returns a failure and preserves the previous snapshot without delaying or failing the already-persisted operation-scoped result. The strict retried `WriteAtomic` path remains appropriate for operation journals and unique result files.

The `File.Move(source, destination, overwrite)` overload is not available in the Unity 2021.3-compatible API surface. For Editor-only shared-history publishing, use the platform-native move-overwrite operations (`MoveFileEx` on Windows and `rename` on Unix-like systems) so the package retains best-effort atomic replacement semantics without depending on a newer .NET API.

---

## 4. Unity Test Framework Quirks

### `Filter` Parameter Semantics in Unity Test Framework
The filter parameters passed to `TestRunnerApi.Execute` have strict, non-obvious semantics:
- **`Filter.groupNames`**: Strictly evaluated as .NET Regular Expressions (`FullNameFilter { IsRegex = true }`). Passing glob patterns (such as `*Test*`) causes a regex compilation exception (`Quantifier * following nothing`) before test execution starts.
- **`Filter.testNames`**: Evaluated as exact string equality against full test names (`Namespace.Class.MethodName`). Test names cannot be regex patterns or substrings.
- **`Filter.assemblyNames`**: Filters test fixtures by assembly name.
- **`Filter.categoryNames`**: Filters tests decorated with NUnit `[Category("...")]`.
- Filter arguments must be passed verbatim without lossy heuristic translations (such as naively rewriting `*` to `.*`).

### Test Mode Validation Must Precede Refresh
The MCP test tool's documented default applies only when the `mode` argument is omitted and the method default supplies `all`. Explicit null, blank, or unsupported modes must be rejected before the client refreshes AssetDatabase or sends a `RUN_TESTS` command; otherwise an invalid request can unexpectedly execute the full suite.

### Pre-Execution Test Run Failure Masking
When the Unity Test Framework aborts prior to running tests (e.g. due to an invalid regex in `groupNames`, an assembly compilation error, or a runner initialization exception):
- `totalTests` is reported as `0` and `Success` is `false` with a failure message.
- If client-side result formatting checks `hasFilter && totalTests == 0` before checking `!result.Success`, it falsely outputs `"No tests found matching filter..."`.
- This masks the true exception from the user or agent. Client formatters must always prioritize `!result.Success` errors over zero-count filter notices.

### Deep Runner Plumbing in Stack Traces
Unity Test Framework executes tests through a deep NUnit runner pipeline (`TestMethodCommand`, `UnityTestMethodCommand`, `UnityEditor.TestTools.TestRunner.*`, `UnityEngine.TestRunner.*`). When a test fails, Unity captures 20–50 lines of framework runner and reflection plumbing beneath the actual test method, cluttering tool responses and consuming thousands of context tokens without diagnostic value.

### `CancelTestRun` Indefinite `Cancelling` State
Calling `TestRunnerApi.CancelTestRun` signals cancellation acceptance, but does not guarantee a terminal callback. In some Unity versions, the runner can remain indefinitely in the `Cancelling` state. Cancellation handling requires an explicit fallback path and cannot assume a clean completion callback will fire.

The cancellation intent and Test Runner job identity must therefore be persisted before releasing the socket worker. The operation remains owned and the running marker remains present until `RunFinished` arrives or `IsRunActive` reports the runner is no longer active. A repeated cancellation request must only reaffirm the same intent; it must not create a second terminal result or permit another test run to overlap the cancelling runner. If Unity remains in `Cancelling`, the protocol truthfully remains busy indefinitely rather than using a timeout.

### Leaking Host Processes into Unit Tests
`Process.GetProcessesByName("Unity")` returns all Unity instances running on the host machine. If unit tests test process discovery without mocking, an unrelated live Editor instance will cause `IsUnityRunning` to return `true` for a temporary test directory, entering unexpected readiness loops. Unit tests must inject a stubbed process provider.

### PATH-Dependent Child Processes in Cross-Platform Tests
Long-lived process fixtures cannot assume that utilities such as `sleep` or `ping` are discoverable by the test runner's `PATH`; sandboxed runners and hosted environments may omit or restrict them. A deterministic fixture should launch the test project's own apphost by its output-directory path and wait on an explicit test-only mode, with the owning test retaining disposal and termination responsibility.

### Multi-Line Test Failure Messages & Stack Trace Sanitization Overhead
- When NUnit or custom test assertions fail, failure messages frequently contain leading whitespace or multiple lines (`\r\n  Expected: ...\r\n  But was: ...`). Extracting single-line summaries requires finding the first non-empty line after trimming to prevent empty summary lines or inadvertent multi-line tool framing.
- In suites with dozens or hundreds of test failures, running regular expression sanitizers (`SanitizeTestStackTrace`) and source location extractors (`ExtractSourceLocation`) across every failure incurs noticeable overhead. Capping these regex operations strictly to the tests receiving detailed reporting (`maxDetailedFailures = 5`) eliminates redundant work on discarded stack frames.

### `UnityTestRunResult.Interrupted` State Symmetry & `IOperationResult` Contract
In polymorphic operation result handling (`IOperationResult`), `Interrupted` is exposed as a mutable boolean property (`bool Interrupted { get; set; }`). In `UnityTestRunResult`, `Interrupted` maps onto `ResultState == "Interrupted"` / `resultState == "Interrupted"`. If code assigns `Interrupted = false` to clear interruption state, a one-way setter that only assigns on `true` leaves `Interrupted` returning `true`, violating the Liskov Substitution Principle and property symmetry. The setter must explicitly restore `ResultState` / `resultState` to `"Passed"` (if `Success` is true), `"Failed"` (if `FailCount > 0`), or `""`.

---

## 5. External Client & Tool Quirks

### MCP Client-Side Tool Execution Timeouts
Even when server-side tools avoid arbitrary bounded timeouts and poll indefinitely, MCP clients (Claude Code, Cursor, Codex) enforce client-side JSON-RPC execution timeouts on `tools/call` requests (defaulting to 60s or 300s). For heavy compilation or long test suites, client-side configuration (e.g. `tool_timeout_sec = 1800` in `.codex/config.toml`) is required to avoid premature client aborts.

### Synthetic Source Locations in Diagnostics
Dynamic in-memory compilation injects directive `#line 1 "eval"` so line numbers match user-submitted snippets. Naive URI builders treat `"eval"` as a relative file path and prepend the project root, hallucinating non-existent file URIs (`file:///.../eval#L1`) that trigger "File not found" errors in AI agents. Eval diagnostics must format synthetic IDs as `snippet line X, col Y:` rather than file URIs.

### Protocol Status Prefix Leaks
When an operation fails immediately upon dispatch, line-oriented socket servers return single-line tokens like `FAILURE <message>` or `ERROR: <message>`.
- If client handlers fail to strip the status prefix before passing the message to compiler diagnostic regex parsers, the parser misinterprets `"FAILURE eval"` as a file path and generates corrupt file URIs (`file:///.../FAILURE eval#L1`).
- When stripping status prefixes (`ERROR:`, `FAILURE:`, `SUCCESS:`), colon-delimited prefixes must be inspected before searching for whitespace (`response.IndexOf(' ')`). If a whitespace search runs first on colon-delimited payloads without spaces (e.g. `SUCCESS:All tests passed`), the space between subsequent words causes the first word of the payload to be mistakenly stripped.

### System.Text.Json Parameter Conversion in MCP Tool Methods
- `System.Text.Json.Serialization.JsonConverterAttribute` targets classes, structs, properties, and fields, but is not valid on method parameters (producing compiler error `CS0592`). MCP tools registered by method reflection therefore cannot use a parameter-level converter. Use native `string[]?` parameters when the current public contract is an array, and verify the emitted schema through `McpServerTool.Create(...).ProtocolTool.InputSchema` rather than reflection metadata alone.

### Interface Segregation & Path Resolution Anti-Pattern
- Forwarding entire sub-service surfaces through a coordinator interface (e.g. `IUnityProcessManager` re-exposing 15+ path properties and methods from `IUnityPathResolver`) creates tight coupling and forces test doubles or mocks to implement dozens of pass-through members unnecessarily, violating the Interface Segregation Principle (ISP). Callers should directly access the dedicated sub-service (e.g. `processManager.PathResolver`).
- Using a type-safe enum (`UnityOperationKind`) instead of string identifiers or dedicated per-command properties for result paths provides compile-time checking, enables exhaustive switch expression matching, and prevents path formatting mismatches between command executors and result pollers.

### Unity Process Startup and PID Reuse

An MCP host can issue concurrent requests before Unity has finished starting. A liveness check followed directly by `Process.Start` is therefore insufficient: each caller can observe the same absent state and launch another Editor. Startup must be serialized per normalized project root and must recheck ownership after entering the gate. PID files are also vulnerable to PID reuse; a live numeric PID is not proof of ownership. Auto-started Editors persist a sidecar identity record with PID, process start time, executable path, and project root, and readers reject missing, mismatched, or stale identity records. `Process` instances retained by startup/readiness monitoring must be disposed when that lifecycle path completes.

The startup claim must cross the MCP host process boundary. A persistent lock-file inode held open with `FileShare.None` provides the same exclusion on Windows, Linux, and macOS, and the operating system releases the handle after a host crash. Do not delete or replace that file during release: POSIX permits unlinking an open file, which would allow a waiter to create and lock a different inode while the original owner is still active. Lockfile presence, lockfile PID bytes, and a lone system-process candidate remain supporting evidence only; ownership requires the durable sidecar identity or explicit project-targeting command-line evidence. Publishing the identity atomically before the PID pointer lets a later host recover a live Editor if the first host exits between those writes.

The Unity process manager keeps startup/shutdown orchestration separate from the PID sidecar's file and process-identity mechanics. The file-backed identity store must preserve the existing atomic publication order and validate PID, start time, executable path, and project root before returning a process handle. Keeping that store behind an internal seam allows focused tests without broadening the public process-manager API.

### Unity CLI and Editor Executable Identity

The Unity CLI can be a real native executable named `Unity`, so checking only the filename, extension, executable bit, or Mach-O/PE/ELF format cannot distinguish it from the Editor. Unity Editor installation markers are safer and deterministic: macOS uses the `Unity.app/Contents/MacOS/Unity` bundle layout with `Contents/Managed/UnityEditor.dll`; Windows and Linux use the Editor's sibling `Data/Managed/UnityEditor.dll` (and `UnityEngine.dll`). Note that `<executable>_Data` is used only for standalone player builds, never for the Unity Editor. All discovery sources must run the same validation, and a rejected configured candidate should leave a diagnostic explaining the expected layout so auto-start errors are actionable.

### MCP Configuration Working-Directory & Workspace Variable Support
MCP clients differ fundamentally in their support for workspace-relative path variables in configuration files:
- **VS Code** supports `${workspaceFolder}` in `.vscode/mcp.json` (under the `"servers"` root key).
- **Claude Code** supports environment and directory expansion in `.mcp.json` using the `${CLAUDE_PROJECT_DIR:-.}` format (under the `"mcpServers"` root key).
- **Antigravity and Cursor** do not expand workspace root variables in their MCP client configurations. Attempting to use workspace tokens or relative paths causes them to fail to locate the server. Therefore, Antigravity (`.agents/plugins/unity-lean-mcp/mcp_config.json`) and Cursor (`.cursor/mcp.json`) must use absolute paths in `cwd`.
- **Working Directory (`cwd`) Affinity**: The MCP server discovers the target Unity project by ascending directories from its working directory until reaching a directory containing `Assets` and `ProjectSettings`. Setting `cwd` directly to the `MCP~` folder and running `dotnet UnityLeanMcp.Mcp.dll` ensures reliable project resolution without requiring hardcoded `--project` arguments.

Machine-specific absolute-path configurations must not be committed or validated as tracked artifacts. CI checkouts use different roots and operating systems, so Cursor and Antigravity configurations should be generated by the Unity installer and covered by tests that create a temporary package/configuration layout.
 
### Unity Package Location & MCP~ Directory Discovery
`PackageInfo.FindForAssembly(typeof(...).Assembly)` reliably resolves the absolute disk path for any package registered in the Unity Package Manager (whether embedded in `Packages/`, cloned via git, referenced by `file:`, or downloaded into `Library/PackageCache/` or the global package cache). However, if an assembly is not registered as a UPM package (for instance, when users copy source files directly into `Assets/`), `PackageInfo.FindForAssembly` returns `null`. Robust package discovery must pair `PackageInfo.FindForAssembly` with an `AssetDatabase.FindAssets` fallback that locates the installer script and ascends to find `package.json` or `MCP~`. Furthermore, on case-sensitive filesystems (Linux), probing both `MCP~` and `mcp~` ensures cross-platform compatibility.


### Integration-Test MCP Artifact Selection

Integration tests must not infer which MCP server binary to launch from build timestamps: a newer Debug DLL can be unrelated to the Release artifact consumed by Unity. Publish the Release server directly into the Unity package's `MCP~` directory for fixture setup, require a successful `dotnet publish` exit code, and have every integration client select that package DLL exclusively.

### Integration Fixture Source Changes Need an Explicit Asset Refresh

Copying a fixture over an existing Unity script and waiting a fixed interval is racy: the Editor may not have delivered its asynchronous external-file/project-change notification before the following `unity_run_tests` readiness probe. The probe can then report `READY` and execute stale compiled code. Fixture setup must issue and await `unity_refresh` after replacing the source; the refresh may legitimately return a compile-error result for a negative fixture.

### Reserving Space for Test Failure Summaries

Failure formatting must reserve a deterministic portion of the aggregate response budget for compact summaries before rendering detailed failures. Otherwise, the first five failures' bounded-but-large stack traces can exhaust the shared builder and hide failures 6 through 25 entirely. When summaries exist, a 16 KiB reservation covers 20 summaries at the existing 512-character identifier and 200-character message limits, including Windows newline overhead, while the detailed section retains the remainder of the 64 KiB cap. Runs with 5 or fewer failures should not reserve unused summary space.

### Cancellation Before the Initial Operation Acknowledgement

An MCP caller can cancel after a mutating command has reached Unity but before the socket response containing `RUNNING` is received. If cancellation is handled only inside the operation poller, this dispatch window leaves the Unity operation journal owned indefinitely because polling never begins. Command dispatch must issue the correlated cancellation request before propagating the caller's cancellation; Unity safely ignores it when the command was not accepted.

### Interactive GUI Editor Lockfile Detection vs. Batchmode Identity Records

`UnityProcessIdentityStore` persists sidecar identity records (`.identity.json`) exclusively for batchmode instances launched by the MCP server (`EnsureUnityRunningAsync`). Interactive GUI Unity Editors launched directly by the user or Unity Hub do not create this identity sidecar. Instead, Unity holds an exclusive lock on `Temp/UnityLockfile` (or `Temp/UnityLockFile`). `IsUnityRunning` must recognize a locked GUI Editor lockfile held open by a live Unity process without requiring `.identity.json`, preventing spurious auto-start attempts that collide with the user's active GUI session.

### MCP Layer Parameter Normalization vs. Low-Level API Contract

In MCP server tools exposed to LLM agents (such as `unity_run_tests`), omitted arguments use their documented defaults and finite string choices should be represented by schema-aware enums. The tool boundary still validates invalid enum values before refresh or dispatch. Lower-level service methods (`UnityClient.RunTestsAsync`) maintain the independent string contract used by the Unity socket protocol and reject blank or unrecognized modes immediately with clear error messages. This keeps schema validation and protocol defense in depth without duplicating cross-tool instructions in every description.

### Test-Environment PATH Isolation During Parallel Test Runs

When unit tests modify process-wide environment variables (such as `PATH`) to verify fallback behavior or rejection of unauthorized binaries, completely wiping `PATH` or omitting the `dotnet` host directory causes concurrent test processes (which rely on `dotnet` in PATH to launch child processes such as `McpTestClient`) to fail with `Win32Exception: The system cannot find the file specified`. Furthermore, `Environment.ProcessPath` during `dotnet test` points to the test host runner (`testhost.exe`), which cannot execute `dotnet` CLI verbs like `publish`. Tests isolating `PATH` must preserve the directory containing the genuine `dotnet` CLI binary (discovered from the original `PATH` or `DOTNET_ROOT`) so that concurrent background subprocess execution remains deterministic and unaffected.

### Blocking Retry Tests Must Not Depend on ThreadPool Scheduling

Tests that synchronously exercise retry loops must not schedule the condition-clearing callback with `Task.Run` and then block the same shared ThreadPool with `Thread.Sleep`. Under CI parallelism, the callback can remain queued until every retry is exhausted even though the delay is nominally shorter than the retry window. Release simulated file locks from the first failed read (or use a dedicated synchronization primitive) so the test is deterministic across Windows, Linux, and macOS without relying on elapsed-time scheduling. Similarly, startup test doubles must report endpoint readiness as false until their simulated process reaches the running state; an always-ready endpoint bypasses startup and can leave readiness gates permanently unsignaled.

### Immutable Result Models vs. Stateful Diagnostic Properties on Services

Exposing diagnostic properties like `LastDiagnostic` on singleton discovery services creates hidden temporal coupling and thread-safety bugs: concurrent calls from separate threads or callers overwrite each other's diagnostic state. Returning a dedicated, immutable Result type (such as `UnityLocatorResult`) bundles the outcome (`ExecutablePath`) with actionable failure details (`Diagnostic`) in a single return value. This keeps locator services completely stateless, eliminates the need for property locking, and makes mocking straightforward without residual diagnostic state.

### Transport Coupling in Lifecycle Handlers

Passing transport primitives (such as `StreamWriter`) into core domain or lifecycle interfaces couples business logic directly to wire protocols, prevents reusing handlers across alternative transports (or in-process tests), and scatters wire framing decisions across multiple classes. Handlers should return structured enum results (`OperationCancelResult`), leaving protocol framing to command dispatchers.

### Temporal In-Memory Result Caches Across Domain Reloads

Caching the last completed operation result in static in-memory fields (`s_LastRefreshResult`) introduces temporal coupling: a client querying state may read an obsolete cached result from a prior operation before new operations run, or lose results entirely if an asynchronous domain reload occurs. Reading durable operation-scoped files (`Temp/unity_refresh_<opId>.json`) eliminates cross-operation pollution and survives asynchronous domain reloads reliably.

### Nullable Context for Linked Unity Package Files in .NET Projects

When linking C# source files from Unity Editor packages (which target Unity 2021.3 / .NET Standard 2.1 without nullable reference types enabled) into a .NET test project with `<Nullable>enable</Nullable>`, MSBuild ignores per-item `<Nullable>disable</Nullable>` on `<Compile>`. Adding `#nullable disable` directives directly into Unity package source files violates coding standards and alters package source code. Instead, place an `.editorconfig` file in the Unity source directory (`src/UnityLeanMcp.Unity3d/.editorconfig`) suppressing CS86xx/CS87xx compiler diagnostic severity (`severity = none`) for files under that tree. This cleanly silences nullable warnings for linked package code when compiled by Roslyn while maintaining strict `<Nullable>enable</Nullable>` enforcement across test and host projects.

### `UNITY_PATH` Directory Resolution in Containerized Environments

Official Unity CI Docker images (such as `unityci/editor:ubuntu-...`) set `UNITY_PATH=/opt/unity` pointing to the installation root directory rather than the Editor binary directly. When resolving `UNITY_PATH` or `UNITY_EDITOR`, executable locators must probe candidate executable locations inside configured directories (`Editor/Unity`, `Unity`, `Editor/Unity.exe`, `Unity.exe`, `Unity.app/Contents/MacOS/Unity`) and validate the containing installation metadata (`Data/Managed/UnityEditor.dll` / `UnityEngine.dll`) before declaring candidates missing or invalid.

### Linux Cross-Process Identity Precision & Yama LSM Permissions

- **Start-Time Jitter Tolerance**: On Linux, `Process.StartTime` is computed by .NET from `/proc/stat` `btime` and `/proc/[pid]/stat` `starttime`. Because `/proc/stat` `btime` can fluctuate by up to ±1 second between reads, strict tick equality (`==`) across different process queries causes false-negative process matches. Comparing start times with a short tolerance (e.g. 3 seconds) preserves durable protection against PID reuse while accommodating kernel jitter.
- **Process Path Fallback**: In restricted Linux environments (such as Docker or systems with Yama LSM `ptrace_scope` enabled), calling `process.MainModule` on another process may fail with `EACCES` (`Win32Exception: Permission denied`). Falling back to resolving `/proc/[pid]/exe` via `File.ResolveLinkTarget` or inspecting `/proc/[pid]/cmdline` ensures deterministic identity verification across containerized environments.

### macOS Interactive Unity Locks May Be Unattributable to the MCP Host

On macOS, an interactive Unity Editor can hold a zero-byte project `UnityLockfile` while a sandboxed MCP host cannot inspect its process command line. Treating that failed inspection as proof that no Editor exists causes a conflicting batchmode launch. A held lock must therefore be preserved and treated as an active-but-unattributed Editor for startup safety; it blocks auto-start and waits for the project socket, but must not be used as authority to stop a process.

### xUnit Exact Type Matching with `OperationCanceledException`

In .NET asynchronous code, operations honoring `CancellationToken` may throw either `OperationCanceledException` directly or its subclass `TaskCanceledException` (for example, from async socket, stream, or task continuations). In xUnit:
- `Assert.ThrowsAsync<OperationCanceledException>` requires an exact type match (`ex.GetType() == typeof(OperationCanceledException)`) and fails if `TaskCanceledException` is thrown.
- Always use `Assert.ThrowsAnyAsync<OperationCanceledException>` when testing cancellation behavior to accommodate any derived `OperationCanceledException` subtype across platforms and CLR async state machines.

### Consistent Zero-Skipped Test Metric Omission

While `PollTestsHandler` on the wire protocol omitted `, 0 skipped` when `SkipCount == 0`, the MCP tool layer (`UnityTools.cs`) and client progress reporter (`UnityClient.cs`) previously hardcoded `, {result.SkipCount} skipped.` onto test summaries and progress messages. This produced inconsistent formatting (`Tests Passed: 27 passed, 0 skipped.` and `Tests finished: 27 passed, 0 skipped.`) and consumed unnecessary LLM token context. Conditionally formatting `, {result.SkipCount} skipped` only when `result.SkipCount > 0` across all tool result lines, empty suite messages (`Tests Passed: 0 passed (no tests found in suite).`), and progress notifications ensures consistent, concise output without noisy zero counts.

### Consistent Filter Array Validation Across Host and Editor Boundaries

When filtering test runs by test names, groups, categories, or assemblies, allowing `null` or whitespace-only elements in filter arrays introduces subtle bugs:
- If a client or Editor handler silently filters out or ignores `null` elements in an array for "compatibility", an array intended to scope tests (e.g. `[""]` or `[null]`) could collapse into an empty filter set and execute the entire test suite without restrictions.
- Client-side validation (`TestFilterValidation.cs`) and Editor server-side validation (`RunTestsHandler.cs`) must enforce identical invariants: rejecting any null, empty, or whitespace-only strings immediately with a clear diagnostic message rather than omitting them.
- Eliminating legacy CLI argument parsing fallbacks in favor of structured JSON payloads ensures that both sides serialize and deserialize identical models without divergence.

### Unity TestRunnerApi Cancellation Must Be Main-Thread Dispatched

Calling `UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.CancelTestRun(jobGuid)` from a background socket thread throws a `UnityException` ("CancelTestRun can only be called from the main thread"). When handling `CANCEL_OPERATION` on a socket worker thread:
- Record the cancellation intent immediately in a thread-safe snapshot file (`WorkerTestCancellationFile`) so that cancellation intent survives background thread context switches or domain reloads.
- Dispatch the actual `TestRunnerApi.CancelTestRun` invocation onto Unity's main thread via `UnityLeanMcpDispatcher.Enqueue(...)`.
- Never call synchronous main-thread Unity APIs directly from `ICommandHandler` implementations marked with `ExecutionTarget.WorkerThread`.

### Pre-Flight Refresh Barrier Deadlocks in Mock Socket Transports

`UnityClient.EvalAsync` unconditionally invokes `RefreshBeforeUsingCompiledAssembliesAsync` prior to evaluating C# snippets to guarantee that compiled assemblies match current source files on disk. In test suites, custom mock socket transports implementing `IUnitySocketTransport` must not stall or intercept `REFRESH` commands unconditionally if testing snippet dispatch cancellation: doing so prevents `EvalAsync` from ever completing its pre-flight refresh and advancing to snippet evaluation, deadlocking the test runner. Mocks should isolate delayed responses to the specific operation under test (e.g. via an opt-in property like `DelayRefresh = true`).

### Editor Shutdown Ordering with Operation Interruption

In `ExitHandler.ExitUnity()`, the Editor process is terminated explicitly via `EditorApplication.Exit(0)` after stopping the socket server. When `EditorApplication.Exit(0)` is called programmatically, `EditorApplication.quitting` callbacks may execute after socket shutdown or not at all before OS termination begins. Explicitly invoking `OperationLifecycleRegistry.NotifyQuitting(operation)` before stopping the server and calling `Exit(0)` ensures that active test runs, AssetDatabase refreshes, and recompilations persist terminal interrupted results to disk before the process shuts down.

### Darwin Kernel `KERN_PROCARGS2` Buffer Layout

On macOS, inspecting process command lines via `sysctl(KERN_PROCARGS2)` yields a binary buffer formatted as:
`[argc (int32)] [exec_path\0] [null padding bytes] [argv[0]\0] [argv[1]\0] ... [argv[argc-1]\0] [envp[0]\0] ...`
- The string immediately following `argc` is the kernel-resolved executable path (`exec_path`), *not* `argv[0]`.
- Null padding bytes align `argv[0]` after `exec_path`.
- The actual argument strings begin at `argv[0]` and run through `argv[argc - 1]`.
- Assuming `exec_path` is `argv[0]` and looping only `argc - 1` times drops the final argument (`argv[argc - 1]`, commonly `-projectPath <path>`). Parsers must skip `exec_path` and padding, then read all `argc` null-terminated argument strings.

### Linux `fcntl` vs. .NET `flock` Lock Domain Separation

On Linux:
- Unity Editor locks `Temp/UnityLockfile` using POSIX `fcntl(fd, F_SETLK, ...)`.
- .NET `FileStream` with `FileShare.None` uses BSD `flock(fd, LOCK_EX)`.
- The Linux kernel maintains `fcntl` record locks and `flock` file locks in completely separate, non-interacting lock domains. A held `fcntl` lock does *not* prevent a `.NET` `File.Open(..., FileShare.None)` from succeeding.
- Consequently, probing `FileShare.None` on Linux returns `false` (not locked) even while Unity is running and holding the lockfile. Deleting `UnityLockfile` on a failed lock probe unlinks Unity's active lockfile while the Editor is running. `UnityLockfile` belongs to Unity and must never be deleted by external hosts.

### macOS Case-Insensitivity in Path Comparisons

Default APFS and HFS+ filesystems on macOS are case-preserving but case-insensitive.
- In .NET, `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` returns `true` on macOS.
- Using `StringComparison.Ordinal` for path equality on macOS causes false-negative matches when paths differ in casing (e.g. `/Users/...` vs `/users/...`, `/Applications/Unity/...` vs `/applications/unity/...`). Both Windows and macOS must use `StringComparison.OrdinalIgnoreCase` for filesystem path equality.

### Symlink Target Resolution in Process Ownership Verification

When Unity is launched via a symlink (e.g. `/usr/local/bin/unity` pointing to `/opt/unity/.../Editor/Unity`):
- `Path.GetFullPath` does not resolve symlink targets.
- The operating system (e.g. Linux `/proc/{pid}/exe` or macOS `MainModule.FileName`) reports the resolved canonical binary path.
- Comparing the raw launch path against the running process executable fails unless symlinks are resolved via `File.ResolveLinkTarget(..., returnFinalTarget: true)`.

### Package Cache Paths (`@`) and Unicode in Compiler Error Regexes

Unity Package Manager downloads packages into `Library/PackageCache/` with version strings containing `@` (e.g., `com.unity.test-framework@1.1.33/`). Additionally, user home directories and project paths frequently contain non-ASCII Unicode characters (e.g., accented characters, Cyrillic, CJK) or `+` (e.g., C++ project directories). Regexes scanning compiler output for file paths must not restrict paths to `[a-zA-Z0-9_./\\ -]+`: doing so silently ignores compilation errors from packages and international paths.

### Windows ProcessStartInfo Trailing Backslash Escaping

Under Win32 command line rules (MSVCRT `CommandLineToArgvW`), a trailing backslash immediately preceding a double quote (`\"`) is treated as an escaped literal quotation mark. If `ProjectRoot` or `LogFile` ends with a trailing backslash, `$"\"{ProjectRoot}\""` creates an unclosed quote that swallows subsequent arguments. All paths passed to `ProcessStartInfo.Arguments` must trim trailing directory separators prior to quoting.

### Decoupling Process Liveness from Active Polling Loops

Scanning the OS process table (`Process.GetProcessesByName`) on every 500ms tick of an operation polling loop introduces measurable CPU spikes, lock contention, and kernel mode transitions while the target process is actively processing requests. When the TCP loopback socket returns valid responses (`RUNNING`, `BUSY`, `COMPILING`, etc.), the Unity process is undeniably alive. Process liveness probing should only execute on socket communication failure (`pollResp == null`).

### Relative `-projectPath` Arguments in Process Command Lines

When Unity is launched via scripts or terminal commands, `-projectPath` is often passed as a relative path (such as `.`, `./`, or relative directory names) rather than an absolute path. Substring or boundary matching against an absolute `ProjectRoot` fails unless the argument tokenizer explicitly extracts `-projectPath <arg>` and `-projectPath=<arg>` and resolves the path relative to the process's working directory or canonical full path.

### Unity Project Root Cleanliness & Batchmode Log Placement

When auto-starting Unity in batchmode (`-batchmode -nographics -projectPath ... -logFile ...`), configuring `-logFile` to point to a file directly under `ProjectRoot` (such as `unity_background_log.txt`) pollutes the repository root, creating git status noise and risking accidental commits. Placing `-logFile` under Unity's `Temp/` directory (`Temp/unity_background_log.txt`) ensures it resides in Unity's standard transient directory alongside all other UnityLeanMcp IPC and marker files, where it is automatically ignored by standard Unity `.gitignore` rules (`[Tt]emp/`) and safely wiped by Unity lifecycle cleans.

### Safe Path Resolution Across Background Threads via Bootstrap Boundary

Unity APIs like `Application.dataPath` throw an exception (`UnityException: get_dataPath can only be called from the main thread`) if called from a worker thread. Creating duplicate shadow properties (`Worker*`) that read uninitialized static fields risks silent `null` path dereferencing if accessed early. By ensuring that path initialization (`UnityLeanMcpPaths.EnsureInitialized()`) is invoked deterministically during the main thread bootstrap sequence (`[InitializeOnLoadMethod] BootstrapOnMainThread()`), the project path is safely resolved and cached into plain string fields once. Background worker threads can then safely access the canonical path properties without needing duplicate properties or risking background thread Unity API invocation.

### Custom Response Handlers and Automatic Result Completion in Polling

In `OperationPoller.PollOperationUntilTerminalAsync`, when `spec.CustomResponseHandler` returns a non-null terminal result, `OperationPoller` automatically invokes `spec.OnResultFound(customResult)` before returning it. Invoking completion logic (such as `CompleteRefreshResult(result)`) manually inside `CustomResponseHandler` causes duplicate execution of diagnostic enrichment and progress reporting callbacks. Custom response handlers must return synthesized or read results directly without manual completion calls.

### Dynamic Result File Path Lookups for Settlement Polling

When polling for compilation settlement in `WaitForCompilationToSettleAsync`, the operation may represent either a standard `Refresh` or a clean `Recompile`. Hardcoding `UnityOperationKind.Refresh` in `CustomResponseHandler` fails to locate operation results produced by recompilations. Polling handlers must read `spec.ResultFilePath` directly so that they automatically query the path determined by the active operation's kind.

### Elimination of Redundant Pre-Flight Probing in Mutating Operations

Pre-flight status probes (such as sending `POLL_REFRESH` before `REFRESH` in `UnityClient.RefreshAsync`) add an extra socket roundtrip to every operation. Because the socket worker thread performs early rejection based on thread-safe in-memory and durable operation store snapshots, sending the mutating command (`REFRESH` / `RECOMPILE`) directly returns `BUSY` immediately if another operation or compilation is active. The command dispatch loop already handles `isBusy` responses by autowaiting on ongoing compilation or foreign locks, making the initial probe completely redundant.



