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

### Unity Test Framework Assembly Reload Lock
Unity locks managed assembly reloads while tests are actively running. Script modifications made during a test run are queued until the Test Framework completes and releases the lock. Consequently, a test modifying a script during execution does not test mid-test domain reload recovery.

### Play Mode Exit Nuances & Stalling
- When requesting Play Mode exit, waiting solely for `EditorApplication.isPlaying == false` is insufficient. Both `EditorApplication.isPlaying` and `EditorApplication.isPlayingOrWillChangePlaymode` must be observed as `false` before starting operations requiring Edit Mode.
- Play Mode exit cannot be assumed to complete within an arbitrary fixed deadline: user scripts (`Application.wantsToQuit` returning `false`), open modal dialogs, or infinite loops can delay or prevent Play Mode exit. Operations must remain active and truth-telling rather than aborting on arbitrary timers.

### Transient Lifecycle Flags
Flags such as `EditorApplication.isCompiling` and `EditorApplication.isUpdating` are transient point-in-time observations. They frequently evaluate to `false` immediately before compilation begins and briefly between internal lifecycle phases. Never conclude that compilation or refresh has completed on the first `false` reading; require the request flag to clear and observe an idle state across multiple update ticks.

### Compiler Diagnostic Capture Timing
Compiler diagnostics must be captured during `CompilationPipeline.assemblyCompilationFinished`, before the domain reload occurs. Waiting for `CompilationPipeline.compilationFinished` or a subsequent Editor update tick is too late: the domain reload unloads the assembly context, causing compiler warnings from the old domain to be lost.

### Asynchronous Compilation Failure Overrides Optimistic Refresh Success
- When triggering AssetDatabase refresh or script recompilation, Unity may return an initial `READY` or success status while compilation errors are asynchronously published to the diagnostics file (`unity_compilation_errors.txt`).
- Client refresh handlers must parse compilation diagnostics upon operation completion and demote `UnityRefreshResult.Success` to `false` if any error-level diagnostics or unparsed compilation errors exist, preventing false-positive success reporting when builds fail.

---

## 2. CLR, Threading & Roslyn Quirks

### `[InitializeOnLoad]` Static Constructor Background Thread Poisoning
Unity does not guarantee execution ordering for `[InitializeOnLoad]` classes across assemblies.
- If a background thread (such as a TCP listener thread) references a static class before Unity's main thread runs its static constructor, the CLR executes that static constructor on the background thread.
- If the static constructor calls main-thread-affine Unity APIs (`EditorUtility`, `SessionState`, `EditorApplication`, `Application.dataPath`), Unity throws a `UnityException`.
- This unhandled exception permanently poisons the type with a CLR `TypeInitializationException` for the entire remaining lifetime of that domain.
- **Rule**: Helper and path classes must eliminate static constructors and static field initializers, defaulting fields to neutral values and relying on explicit main-thread `EnsureInitialized()` invocations during startup.

### Unity API Main-Thread Affinity
Virtually all Unity APIs are strictly main-thread-affine unless explicitly documented otherwise. This includes APIs that resemble standalone utility code, such as Unity JSON serialization (`JsonUtility`) and data path retrieval (`Application.dataPath`). Background threads must never invoke Unity engine APIs directly.

### Worker-Thread Operation Store Snapshot Caching
- `UnityLeanMcpOperationStore.ReadThreadSafeSnapshot()` is invoked from background TCP listener threads to inspect active operation state during command dispatch and busy checks.
- If `ReadThreadSafeSnapshot()` falls back to invoking `Read()` while an in-memory cached state is present, background worker threads execute `JsonUtility.FromJson<UnityLeanMcpOperationState>` and incur redundant disk I/O under lock.
- Returning `Clone(s_CachedState)` immediately under `s_CacheLock` when `s_CachedState != null` avoids both worker-thread `JsonUtility` execution and disk I/O, ensuring thread safety and preventing background thread engine exceptions.

### Roslyn Reflection Traps
When calling Roslyn APIs via reflection across Unity Editor versions:
- **`CSharpSyntaxTree.GetRoot`**: Has an optional parameter (`GetRoot(CancellationToken cancellationToken = default)`). Reflective lookup specifying 0 parameters (`new Type[0]`) returns `null`. The reflection lookup must explicitly match `GetRoot(CancellationToken)` and supply `default(CancellationToken)`.
- **`SyntaxNode.DescendantNodes`**: Has multiple overloads, including `DescendantNodes(Func<SyntaxNode, bool>, bool)` (2 parameters) and `DescendantNodes(TextSpan, ...)` (3 parameters). Loose reflection matching can latch onto the 3-parameter overload; passing a default `TextSpan` traverses an empty span `[0..0)`, yielding zero nodes. Reflection must strictly match the 2-parameter overload and pass `new object[] { null, false }`.

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


### Operation-Scoped Resource Lifetime & Out-of-Lock Disposal
When managing static references to active operation resources (such as `ConsoleLogCapture` or `CancellationTokenSource`) across asynchronous, domain-reloaded, or interrupted operations:
- Methods marking operations interrupted (e.g. `MarkInterrupted`) must strictly verify that `targetOperationId` matches the currently active operation before disposing static runtime resources. Disposing `s_ActiveLogCapture` unconditionally when `targetOperationId` does not match causes active, concurrent operations to lose log capture mid-flight.
- Never invoke disposable or cancelable callbacks (such as `CancellationTokenSource.Cancel()`, `CancellationTokenSource.Dispose()`, or `ConsoleLogCapture.Dispose()`) while holding internal synchronization locks (`s_CtsLock`). Cancellation callbacks or event unsubscriptions can execute external code or cause lock contention; resources should be extracted and nulled within the lock and disposed safely outside of it.

### Extensible Command Handlers & `[InitializeOnLoad]` Execution Order
When providing static registration APIs (`RegisterHandler`, `UnregisterHandler`, `TryGetHandler`) on classes decorated with `[InitializeOnLoad]`:
- External packages and editor scripts may register custom command handlers in their own `[InitializeOnLoad]` static constructors before or after `UnityLeanMcpServer` initializes.
- Default built-in handlers must be seeded safely via thread-safe lazy initialization (e.g. `EnsureDefaultHandlers()`) prior to any external registration, retrieval, or unregistration.
- This ensures external custom command registrations are neither lost nor overwritten by late-executing default initializers.

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

---

## 4. Unity Test Framework Quirks

### `Filter` Parameter Semantics in Unity Test Framework
The filter parameters passed to `TestRunnerApi.Execute` have strict, non-obvious semantics:
- **`Filter.groupNames`**: Strictly evaluated as .NET Regular Expressions (`FullNameFilter { IsRegex = true }`). Passing glob patterns (such as `*Test*`) causes a regex compilation exception (`Quantifier * following nothing`) before test execution starts.
- **`Filter.testNames`**: Evaluated as exact string equality against full test names (`Namespace.Class.MethodName`). Test names cannot be regex patterns or substrings.
- **`Filter.assemblyNames`**: Filters test fixtures by assembly name.
- **`Filter.categoryNames`**: Filters tests decorated with NUnit `[Category("...")]`.
- Filter arguments must be passed verbatim without lossy heuristic translations (such as naively rewriting `*` to `.*`).

### Pre-Execution Test Run Failure Masking
When the Unity Test Framework aborts prior to running tests (e.g. due to an invalid regex in `groupNames`, an assembly compilation error, or a runner initialization exception):
- `totalTests` is reported as `0` and `Success` is `false` with a failure message.
- If client-side result formatting checks `hasFilter && totalTests == 0` before checking `!result.Success`, it falsely outputs `"No tests found matching filter..."`.
- This masks the true exception from the user or agent. Client formatters must always prioritize `!result.Success` errors over zero-count filter notices.

### Deep Runner Plumbing in Stack Traces
Unity Test Framework executes tests through a deep NUnit runner pipeline (`TestMethodCommand`, `UnityTestMethodCommand`, `UnityEditor.TestTools.TestRunner.*`, `UnityEngine.TestRunner.*`). When a test fails, Unity captures 20–50 lines of framework runner and reflection plumbing beneath the actual test method, cluttering tool responses and consuming thousands of context tokens without diagnostic value.

### `CancelTestRun` Indefinite `Cancelling` State
Calling `TestRunnerApi.CancelTestRun` signals cancellation acceptance, but does not guarantee a terminal callback. In some Unity versions, the runner can remain indefinitely in the `Cancelling` state. Cancellation handling requires an explicit fallback path and cannot assume a clean completion callback will fire.

### Leaking Host Processes into Unit Tests
`Process.GetProcessesByName("Unity")` returns all Unity instances running on the host machine. If unit tests test process discovery without mocking, an unrelated live Editor instance will cause `IsUnityRunning` to return `true` for a temporary test directory, entering unexpected readiness loops. Unit tests must inject a stubbed process provider.

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
- `System.Text.Json.Serialization.JsonConverterAttribute` targets classes, structs, properties, and fields, but is not valid on method parameters (producing compiler error `CS0592`). When an MCP server registers tools via method reflection (such as `WithTools<T>()` in `ModelContextProtocol.Server`), method parameters cannot be decorated with `[JsonConverter]`. To support flexible parameter deserialization (such as accepting either a JSON string `"value"` or a JSON array `["value"]`), wrap the parameter in a dedicated type (e.g. `SingleOrArray`) decorated with `[JsonConverter(typeof(SingleOrArrayJsonConverter))]`. The MCP argument deserializer automatically invokes the type's converter when binding incoming JSON-RPC tool call arguments.
- Custom parameter types implementing `IEquatable<T>` must explicitly overload `operator ==` and `operator !=` (CA2231). Without explicit operator overloads, C# `==` falls back to reference equality, causing identical instances to compare as unequal when checked with `==`.
- Deserializing whitespace or empty strings in custom parameter converters should consistently return `null` if the implicit string operator maps whitespace to `null`, ensuring consistent semantics between direct C# assignment and JSON-RPC dispatch.
- When refactoring collection types from `List<T>` to an encapsulated `IReadOnlyList<T>` (preventing CA1002), callers utilizing C# 12 collection expressions (e.g. `testNames: ["TestA", "TestB"]`) will fail compilation with `CS1061: does not contain a definition for 'Add'` unless the type is decorated with `[CollectionBuilder(typeof(TargetType), nameof(Create))]` paired with a static `Create(ReadOnlySpan<T>)` builder method.

### Interface Segregation & Path Resolution Anti-Pattern
- Forwarding entire sub-service surfaces through a coordinator interface (e.g. `IUnityProcessManager` re-exposing 15+ path properties and methods from `IUnityPathResolver`) creates tight coupling and forces test doubles or mocks to implement dozens of pass-through members unnecessarily, violating the Interface Segregation Principle (ISP). Callers should directly access the dedicated sub-service (e.g. `processManager.PathResolver`).
- Using a type-safe enum (`UnityOperationKind`) instead of string identifiers or dedicated per-command properties for result paths provides compile-time checking, enables exhaustive switch expression matching, and prevents path formatting mismatches between command executors and result pollers.

