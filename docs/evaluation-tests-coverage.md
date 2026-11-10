# Evaluation, tests, and coverage

Read when changing snippet compilation/execution, Test Runner integration, coverage, tool schemas, or output formatting. Operation ownership is covered in [operation lifecycle](operation-lifecycle.md).

## Decisions

### Evaluation

Eval accepts C# statements, top-level `await`, and explicit `return`, taking input strictly at face value without heuristic transformations, magic preprocessing, or code fence stripping. Snippets provide their own `using` directives; anonymous-object returns support compact property inspection. Hoist directives while preserving source line/column positions and comments. Snippet compilation automatically injects `using UnityLeanMcp;` so built-in token-bounded introspection helpers (`Inspect`) can be called directly without namespace qualification to summarize hierarchies (with optional compact uGUI layout tokens via `includeUiDetails: true`), UI Toolkit `UIDocument` instances and `VisualElement` layout trees (`Inspect.UIDocument`), active Prefab Stages, GameObjects (with `RectTransform` and `Canvas` layout awareness), components, project assets, asset dependencies and reverse-references (token-bounded queries with path normalization, type annotations, and missing-asset diagnostics), prefab overrides (added/removed components and property deltas relative to source assets with internal engine noise filtered), active selection, and camera settings. Visual capture helpers (`Inspect.CaptureCamera` and `Inspect.CaptureSceneView`) render off-screen frames safely across Built-in and Scriptable Render Pipelines directly to disk (defaulting to `Temp/unity_capture.png` and `Temp/unity_sceneview.png`), deterministically destroying temporary textures and restoring render target state in `finally` blocks without dirtying the scene, and return concise file, dimension, and byte size summaries to avoid context window pollution. `Inspect.Help()` uses dynamic reflection over public static `Inspect` methods to prevent cheatsheet documentation drift.

Roslyn initialization is transactional and retryable. Build metadata references per evaluation from loaded assemblies, Editor compilation assemblies, and user precompiled DLLs, caching `MetadataReference` instances by file path and last write timestamp to avoid redundant PE parsing across evaluations. Repeated evaluation snippets in the same domain cache compiled runner delegates in memory by exact source key, skipping Roslyn compilation and dynamic assembly loading entirely. Runtime assembly resolution handles project/plugin assemblies not already loaded. Generated assemblies remain in the managed domain until reload; there is no per-evaluation unload promise. In EditMode, evaluation crosses a correlated refresh barrier before execution to pick up external file edits; during active PlayMode, evaluation skips the refresh barrier and evaluates directly against the active runtime domain, preserving game state, active GameObjects, and scene changes.

Result formatters use a priority registry with thread-safe registration and a published array snapshot. More specific formatters precede base-type fallbacks. Composite formatters share one recursion context, with cycle, depth, item, character, and UTF-8 limits. These bound traversal and output, not the execution time of arbitrary getters or enumerators.

`OperationExecutionEngine` unwraps `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` without blocking the Editor thread. Pending task completion returns through the main-thread dispatcher before Unity-facing formatting and publication. Returning an un-awaited `UnityEngine.Awaitable` or `Awaitable<T>` emits diagnostic guidance (`(Evaluation returned an un-awaited Awaitable. Use 'return await <expr>;' to await the result.)`) rather than reflectively unwrapping it across the package's Unity 2021.3 baseline; if the minimum supported Unity version is updated to 2023.1+, full `Awaitable` unwrapping compatibility should be added. Snippets receive `cancellationToken`; cancellation is cooperative and cannot forcibly stop arbitrary synchronous user code.

### Tests and coverage

`unity_test` requires `mode: editmode|playmode` and uses plural array filters (`testNames`, `groupNames`, `categoryNames`, `assemblyNames`). Validate mode and filter elements before refresh, and validate independently at the Editor boundary. Do not silently drop invalid entries and broaden a test run. `failedOnly` uses retained test history. When a filtered test run executes 0 tests, the runner inspects available tests in loaded assemblies to detect cross-mode matches (reporting when target tests exist in the opposite mode) or up to 5 close near-matches (e.g. when a fixture or substring passed in `testNames` would have matched under `groupNames`), and directs callers to discover available tests via `Inspect.Tests("<filter>")`. Reconstruct failure details from the final runner result tree so callback recreation across reload cannot lose completed failures.

Coverage is opt-in during tests and queried separately by path through `unity_coverage`. A coverage-enabled test owns one snapshot: admission invalidates the old snapshot, saves the original profiler setting in `SessionState`, synchronously imports project changes, requests compilation, and establishes the input fingerprint only after the resulting compiler lifecycle settles. When profiling was initially disabled, it then enables coverage, waits for the instrumentation compilation to finish, verifies that inputs stayed stable, resets counters, and starts Test Runner. Ordinary tests leave profiling settings alone.

On a completed Test Runner result with at least one test (including assertion failures), capture native counters once and persist the frozen report before restoring the original profiler setting. Infrastructure errors, setup failures, cancellation, and partial runs do not produce a report. Restoration state is persisted before changing the setting; result publication waits for any resulting compilation and Editor update/import work to settle. A restoration compiler failure or input change marks coverage unavailable and removes the snapshot while preserving the test pass/fail result. Pending publication resumes after reload without recapturing native counters. `coverageStatus` and `coverageMessage` accompany the terminal test result and are included in MCP output.

`unity_coverage` is a repeatable, read-only filter over that one frozen report. It does not toggle/reset coverage or query live counters; invalid paths, disconnected requests, and response write failures leave the report available for another query. Freshness compares the content and membership of compilation inputs plus Unity's assembly graph: source files under `Assets`, `Packages`, and mutable resolved package roots, assembly definitions/references, compiler settings and response/analyzer files (caching content digests for immutable `Library/PackageCache` and Editor installation files), package manifests, and user plugin DLL bytes and importer metadata. The compilation epoch alone is not source identity, so a profiler-only recompilation of unchanged inputs leaves the report valid. Inputs changed during the measured run or before a later query make coverage unavailable for the current source. A new admitted coverage attempt replaces the snapshot even if setup or test execution later fails. The report lasts for the Editor session, not across Editor exit.

Report missing paths distinctly from existing paths without compiled scripts. Include generated nested methods and constructors exactly once, exclude hidden sequence points, map synthetic compiler-generated state machines and lambdas back to declaring method names, and report a line as uncovered if any of its points is unhit.

**Coverage limitations:** See [coverage runtime evidence](#coverage-runtime-evidence) for the validated environment, the unverified PlayMode gate, and the boundaries of freshness checks and query cost.

Test callbacks bind to the admitted run ID. Callback-owner cleanup is limited to this package's named `TestRunnerApi` objects; it must not destroy instances owned by the Test Runner UI or other packages.

### Output

Public responses are capped at 65,536 UTF-16 characters and 65,536 UTF-8 bytes, with explicit truncation markers and intact Unicode scalars. Format real source locations as plain file URIs or canonical paths and eval locations as snippet coordinates. Eval inspection outputs must not use clickable Markdown links or decorative formatting: responses are designed for machine resolution by AI agents rather than human consumption, keeping payloads token-efficient and syntax-dense. Use invariant culture for numeric inspection.

Warnings are capped at 10; errors take priority within the overall response budget. Test failures show up to 5 detailed entries followed by up to 20 compact summaries with reserved capacity, so one large trace cannot hide every later failure. Preserve multiline assertion messages in details, strip recognized runner plumbing, prioritize pre-execution errors over empty-filter notices, and omit zero skipped counts.

## Learned pitfalls

### Reflection signatures and reference discovery

- `CSharpSyntaxTree.GetRoot` takes an optional `CancellationToken`; zero-argument reflection lookup does not match it.
- Select the intended two-parameter `DescendantNodes` overload. Accidentally selecting its `TextSpan` overload with an empty span finds no directives.
- `MetadataReference.CreateFromFile` has optional parameters including a value-type options argument. Reflect the supported signature and supply defaults correctly; do not assume a one-element argument array is sufficient.
- Build reflection state privately and publish it only after successful initialization. Leave failed initialization retryable; do not expose a partial compiler graph.
- Assemblies absent from `AppDomain.GetAssemblies()` can still be compiled project/plugin dependencies, including `autoReferenced: false` assemblies. Query the compilation pipeline as well. Compile-time metadata references alone do not establish runtime assembly resolution; emitted code can still fail at JIT time without resolving the DLL.
- `Assembly.Load(byte[])` does not provide per-evaluation unloading. Generated assemblies accumulate until managed domain reload.
- Roslyn compiler assemblies (`Microsoft.CodeAnalysis.CSharp.dll` and dependencies) are relocated under `Resources/Scripting/` in Unity 6000.3+ (e.g. `Resources/Scripting/DotNetSdkRoslyn` and `Resources/Scripting/MonoBleedingEdge/...`), while earlier versions place them directly under the application contents root (`DotNetSdkRoslyn`, `MonoBleedingEdge/...`). Candidate probing must check both layouts across platforms.

### Preserve snippet source positions

Using directives cannot remain inside the generated runner method. Hoist only `UsingDirectiveSyntax`, excluding using statements/declarations, and blank its `Span` with spaces. `FullSpan` includes comments and trivia; deleting lines shifts diagnostic coordinates and can erase statements sharing a line. The fallback extractor must preserve those same boundaries. Do not inject optional namespaces such as `UnityEngine.UI`, which may not exist in the project.

Classify value returns in the snippet body through syntax, not a search for `return`. Returns inside lambdas or local functions do not make the outer snippet value-returning; `return;` is also distinct from `return value;`. Choose one wrapper and compile once, rather than retrying compilation under guessed expression/statement forms.

### Formatter boundaries

Specific type matchers must precede base types (`Transform` before `Component`). Preserve custom registrations during default initialization. Use a shared recursion context and reference identity for the active path: a repeated reference in another branch is not a cycle.
Introspection in EditMode must avoid calling mutating component property getters (`Renderer.material`, `Renderer.materials`, `MeshFilter.mesh`, `Collider.material`, `Volume.profile`, `Terrain.materialTemplate`, `Cloth.coefficients`), which instantiate copies, leak assets, and dirty scene state; inspect safe shared accessors (`sharedMaterial`, `sharedMesh`, `sharedProfile`, or graphic `sharedMaterial` instead of mutating copy APIs like `CanvasRenderer.GetMaterial()`) and skip `[Obsolete]` members instead. Off-screen visual captures in EditMode must use temporary `RenderTexture` allocations with deterministic `ReleaseTemporary` and `DestroyImmediate` in `finally` blocks rather than asynchronous `ScreenCapture` APIs (which fail or queue into inactive frame loops outside PlayMode) or OS display pixel buffers (which fail when minimized, obscured, or headless).

Depth, item, and output caps do not stop a blocking getter or `MoveNext`. UTF-16 length also does not bound UTF-8 bytes; truncation must respect both and avoid splitting surrogate pairs. Allocate a reusable `stackalloc` buffer outside loops, since repeated stack allocations remain until the method returns.

### Coverage reflection

- Coverage accepts project-relative conventions such as `/Assets/...` and whole-project `.`/`./`. Reuse canonical paths after existence validation, including `.` and `..` segments. Normalize project-relative conventions explicitly and enforce directory boundaries so `Assets/Scripts` does not match `Assets/ScriptsExtended`.

- Coverage source identity, aggregation, filtering, and ordering use case-insensitive comparison on Windows and ordinal comparison on Unix, preserving case-distinct files on Linux and case-sensitive macOS volumes. Foreign Windows drive-path normalization is syntax handling, separate from local source identity.

- Traverse compiler-generated nested types for async methods, iterators, and lambdas, but avoid revisiting nested types already returned by `Assembly.GetTypes()`.
- Enumerate instance constructors separately from `TypeInitializer`; requesting static constructors in both paths double-counts `.cctor`.
- Exclude hidden sequence points (`0xfeefee`) and nonpositive lines. Unity's sequence-point line field is unsigned; DTO conversion needs an explicit checked/validated boundary.
- A source line with several sequence points is uncovered when any point is unhit, even if another point on that line ran.
- Use a `Coverage` type alias where importing both TestTools namespaces would make `TestMode` ambiguous.

### Coverage runtime evidence

Live validation of the transactional EditMode workflow was performed on macOS with Unity 6000.0.68f1 and on Windows with Unity 6000.0.77f1, including instrumentation/restoration reloads, repeated queries, failed assertions, and snapshot invalidation. These checks do not establish runtime coverage behavior on Linux or other Unity versions, including the declared Unity 2021.3 baseline. Declared platform and Unity-version support is unchanged. The .NET tests exercise lifecycle and failure logic with Unity stubs; they cannot establish native counter persistence or Editor scheduling behavior. See [verification boundaries](testing-and-release.md#verification-boundaries).

PlayMode coverage was not validated by these EditMode checks. `RunTestsHandler.IsPlayModeCoverageSupported` admits it using a Unity 6.7 version threshold, which remains an implementation assumption about counter persistence. Version parsing is not proof of this behavior: Unity's June 2026 update describes 6.7 CoreCLR as an experimental desktop **player**, with a full CoreCLR Editor targeted for 7.0. Editor PlayMode reload settings also vary. Do not claim that every 6.7+ Editor preserves hit counters or every earlier configuration necessarily loses them. Verify counter persistence with the actual Editor, Test Framework, and reload settings before changing or relying on the gate. [Unity runtime update](https://discussions.unity.com/t/coreclr-scripting-and-serialization-update-june-2026/1723299).

On Unity 6000.0.68f1, changing `Coverage.enabled` immediately sets `EditorApplication.isCompiling`; compilation-start and compilation-finished callbacks follow, then the Editor reloads. Assigning the same value emits no compiler lifecycle. Disabling coverage discards native hit counters, so the report must be frozen before restoration. This trace supports waiting on observed compiler callbacks and persisted state rather than treating the epoch change itself as evidence that sources changed.

A report describes execution by the selected tests; it does not establish coverage by the entire suite or correctness of the code. Freshness checks cover the enumerated compilation inputs, not scenes, prefabs, arbitrary asset contents, runtime state, or undeclared files read by tests or source generators. Rerun the relevant tests after behavior-affecting changes to those inputs even when the compilation fingerprint still matches.

Queries read and hash compilation inputs on the Editor main thread. Large source trees or plugin DLL sets can therefore increase query cost. Batch related paths into one `unity_coverage` request to share a single freshness check.

### Test Runner callback ownership

Earlier code tied registration to the lifetime of a `TestRunnerApi` Unity object. A destroyed native object can compare equal to `null` while its managed wrapper remains, so recreating the API and registering the same callback again produced duplicate reports. The current bootstrap creates a named callback owner, rebinds persisted run identity after reload, and removes only its own prior owners. A global sweep of every `TestRunnerApi` object breaks other Editor tooling.

### Test filters and results

Unity Test Framework `groupNames` are .NET regular expressions; `testNames` are exact full names. Do not translate globs heuristically. Parse every group expression before host refresh and again at the Editor boundary, reporting its array index and regex parse error. Exact name, category, and assembly filters are not regex-validated. Invalid filter entries must fail rather than silently becoming an unfiltered suite. Explicit EditMode/PlayMode selection avoids ambiguous discovery.

Runner startup failures can return zero tests. Check failure before emitting an empty-filter message. Keep the first nonempty line for compact failure summaries, reserve output space for later failures, and sanitize stack traces only for failures receiving details. Recognize package paths containing `@`, `+`, and Unicode when locating diagnostics. Synthetic `eval` locations are snippet coordinates, not files.

## Code and history

[EvalHandler](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs), [OperationExecutionEngine](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/OperationExecutionEngine.cs), [RunTestsHandler](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/RunTestsHandler.cs).

History anchors (inspect with `git show <commit>`): `09280c8` and `c1e749d` (callback lifetime and ownership), `84e7484` (return classification), `dc38551` and `bff9f8b` (async execution; standalone execute tool since removed).
