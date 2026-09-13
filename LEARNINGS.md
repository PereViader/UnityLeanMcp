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

### Roslyn Reflection Traps
When calling Roslyn APIs via reflection across Unity Editor versions:
- **`CSharpSyntaxTree.GetRoot`**: Has an optional parameter (`GetRoot(CancellationToken cancellationToken = default)`). Reflective lookup specifying 0 parameters (`new Type[0]`) returns `null`. The reflection lookup must explicitly match `GetRoot(CancellationToken)` and supply `default(CancellationToken)`.
- **`SyntaxNode.DescendantNodes`**: Has multiple overloads, including `DescendantNodes(Func<SyntaxNode, bool>, bool)` (2 parameters) and `DescendantNodes(TextSpan, ...)` (3 parameters). Loose reflection matching can latch onto the 3-parameter overload; passing a default `TextSpan` traverses an empty span `[0..0)`, yielding zero nodes. Reflection must strictly match the 2-parameter overload and pass `new object[] { null, false }`.

### Roslyn `CS1529` Using Directive Hoisting & Whitespace Blanking
When users or AI agents provide standard C# source code containing `using` directives at the top (e.g. `using System.IO;`):
- Placing them inside a generated runner method body causes Roslyn error `CS1529: A using directive must precede all other elements defined in the namespace`.
- Directives must be extracted (matching `UsingDirectiveSyntax` while ignoring `UsingStatementSyntax` and `LocalDeclarationStatementSyntax`) and hoisted to file scope before the class declaration.
- Extracted directive characters must be replaced with spaces rather than deleted. This preserves exact original line breaks (`\r\n`), ensuring that compiler error line and column numbers remain 1:1 identical to the caller's submitted code.

### Optional Package Assemblies in Headless Environments
In minimalist Unity installations (headless, server, batchmode, or VR builds), package-modular assemblies such as `UnityEngine.UI.dll` (from `com.unity.ugui`) may not be installed or loaded. Emitting unconditional `using UnityEngine.UI;` in dynamically compiled Roslyn wrappers triggers compiler error `CS0234`. Dynamic code wrappers must avoid ambient `using` directives or conditionally probe assembly metadata before emitting optional namespace imports.

### Type Hierarchy Formatter Matching Order
In hierarchical type matchers, `UnityEngine.Transform` inherits from `UnityEngine.Component`. Specialized formatters for `Transform` must precede generic `Component` checks; otherwise, `Transform` instances are captured and misformatted by generic component inspection logic.

---

## 3. Operating System & Filesystem Quirks

### Windows NTFS File Locking, `ReplaceFileW`, & `DELETE_PENDING`
On Windows (NTFS / Win32):
- `File.Replace` (backed by Win32 `ReplaceFileW`) requires exclusive write access to the destination file. If another process or background thread has the destination file open—even with `FileShare.ReadWrite`—`ReplaceFileW` fails with `ERROR_SHARING_VIOLATION`.
- `File.Delete` places files into a transient `DELETE_PENDING` state until all open handles close. During this window, subsequent file creation or replacement attempts fail with sharing violations, and concurrent readers observe 0-byte files.
- **Solution**: Avoid static shared result files. Use operation-scoped unique result paths (`Temp/unity_<kind>_<opId>.json`) and atomically move temporary files into place via `File.Move(tempPath, targetPath)`. Moving to an uncreated path avoids `ReplaceFileW` and requires no exclusive replacement locks.

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

---

## 5. External Client & Tool Quirks

### MCP Client-Side Tool Execution Timeouts
Even when server-side tools avoid arbitrary bounded timeouts and poll indefinitely, MCP clients (Claude Code, Cursor, Codex) enforce client-side JSON-RPC execution timeouts on `tools/call` requests (defaulting to 60s or 300s). For heavy compilation or long test suites, client-side configuration (e.g. `tool_timeout_sec = 1800` in `.codex/config.toml`) is required to avoid premature client aborts.

### Synthetic Source Locations in Diagnostics
Dynamic in-memory compilation injects directive `#line 1 "eval"` so line numbers match user-submitted snippets. Naive URI builders treat `"eval"` as a relative file path and prepend the project root, hallucinating non-existent file URIs (`file:///.../eval#L1`) that trigger "File not found" errors in AI agents. Eval diagnostics must format synthetic IDs as `snippet line X, col Y:` rather than file URIs.

### Protocol Status Prefix Leaks
When an operation fails immediately upon dispatch, line-oriented socket servers return single-line tokens like `FAILURE <message>`. If client handlers fail to strip the `FAILURE` prefix before passing the message to compiler diagnostic regex parsers, the parser misinterprets `"FAILURE eval"` as a file path and generates corrupt file URIs (`file:///.../FAILURE eval#L1`).

### System.Text.Json Parameter Conversion in MCP Tool Methods
- `System.Text.Json.Serialization.JsonConverterAttribute` targets classes, structs, properties, and fields, but is not valid on method parameters (producing compiler error `CS0592`). When an MCP server registers tools via method reflection (such as `WithTools<T>()` in `ModelContextProtocol.Server`), method parameters cannot be decorated with `[JsonConverter]`. To support flexible parameter deserialization (such as accepting either a JSON string `"value"` or a JSON array `["value"]`), wrap the parameter in a dedicated type (e.g. `SingleOrArray`) decorated with `[JsonConverter(typeof(SingleOrArrayJsonConverter))]`. The MCP argument deserializer automatically invokes the type's converter when binding incoming JSON-RPC tool call arguments.
- Custom parameter types implementing `IEquatable<T>` must explicitly overload `operator ==` and `operator !=` (CA2231). Without explicit operator overloads, C# `==` falls back to reference equality, causing identical instances to compare as unequal when checked with `==`.
- Deserializing whitespace or empty strings in custom parameter converters should consistently return `null` if the implicit string operator maps whitespace to `null`, ensuring consistent semantics between direct C# assignment and JSON-RPC dispatch.

