# Evaluation, tests, and coverage

Read when changing snippet compilation/execution, Test Runner integration, coverage, tool schemas, or output formatting. Operation ownership is covered in [operation lifecycle](operation-lifecycle.md).

## Decisions

### Evaluation

Eval accepts C# statements, top-level `await`, and explicit `return`. Snippets provide their own `using` directives; anonymous-object returns support compact property inspection. Hoist directives while preserving source line/column positions and comments. Snippet compilation automatically injects `using UnityLeanMcp;` so built-in token-bounded introspection helpers (`Inspect`) can be called directly without namespace qualification to summarize hierarchies, GameObjects, components, project assets, active selection, and camera settings.

Roslyn initialization is transactional and retryable. Build metadata references afresh per evaluation from loaded assemblies, Editor compilation assemblies, and user precompiled DLLs. Runtime assembly resolution handles project/plugin assemblies not already loaded. Generated assemblies remain in the managed domain until reload; there is no per-evaluation unload promise.

Result formatters use a priority registry with thread-safe registration and a published array snapshot. More specific formatters precede base-type fallbacks. Composite formatters share one recursion context, with cycle, depth, item, character, and UTF-8 limits. These bound traversal and output, not the execution time of arbitrary getters or enumerators.

`OperationExecutionEngine` unwraps `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` without blocking the Editor thread. Pending task completion returns through the main-thread dispatcher before Unity-facing formatting and publication. Snippets receive `cancellationToken`; cancellation is cooperative and cannot forcibly stop arbitrary synchronous user code.

### Tests and coverage

`unity_test` requires `mode: editmode|playmode` and uses plural array filters (`testNames`, `groupNames`, `categoryNames`, `assemblyNames`). Validate mode and filter elements before refresh, and validate independently at the Editor boundary. Do not silently drop invalid entries and broaden a test run. `failedOnly` uses retained test history. Reconstruct failure details from the final runner result tree so callback recreation across reload cannot lose completed failures.

Coverage is opt-in during tests and queried separately by path through `unity_coverage`; queries do not refresh or write coverage reports. Enable/reset counters only after test admission: persist the previous profiler setting first, enable native coverage before resetting counters (Unity rejects reset while disabled), and restore the previous setting when backing work finishes, including initialization failures. Ordinary tests leave profiling settings alone.

Coverage queries wait for foreign operations and read the latest completed coverage snapshot. Pending compilation and the current script optimization setting do not invalidate that historical measurement; recording a new run still requires Debug optimization. Queries do not refresh or remeasure modified scripts. Report missing paths distinctly from existing paths without compiled scripts. Include generated nested methods and constructors exactly once, exclude hidden sequence points, and report a line as uncovered if any of its points is unhit.

**Known limitation:** `RunTestsHandler.IsPlayModeCoverageSupported` currently admits PlayMode coverage by a Unity 6.7 version threshold. This is an implementation assumption, not verified evidence of Editor counter persistence. See [coverage runtime evidence](#coverage-runtime-evidence); changes to this gate require runtime validation.

Test callbacks bind to the admitted run ID. Callback-owner cleanup is limited to this package's named `TestRunnerApi` objects; it must not destroy instances owned by the Test Runner UI or other packages.

### Output

Public responses are capped at 65,536 UTF-16 characters and 65,536 UTF-8 bytes, with explicit truncation markers and intact Unicode scalars. Format real source locations as file URIs and eval locations as snippet coordinates. Use invariant culture for numeric inspection.

Warnings are capped at 10; errors take priority within the overall response budget. Test failures show up to 5 detailed entries followed by up to 20 compact summaries with reserved capacity, so one large trace cannot hide every later failure. Preserve multiline assertion messages in details, strip recognized runner plumbing, prioritize pre-execution errors over empty-filter notices, and omit zero skipped counts.

## Learned pitfalls

### Reflection signatures and reference discovery

- `CSharpSyntaxTree.GetRoot` takes an optional `CancellationToken`; zero-argument reflection lookup does not match it.
- Select the intended two-parameter `DescendantNodes` overload. Accidentally selecting its `TextSpan` overload with an empty span finds no directives.
- `MetadataReference.CreateFromFile` has optional parameters including a value-type options argument. Reflect the supported signature and supply defaults correctly; do not assume a one-element argument array is sufficient.
- Build reflection state privately and publish it only after successful initialization. Leave failed initialization retryable; do not expose a partial compiler graph.
- Assemblies absent from `AppDomain.GetAssemblies()` can still be compiled project/plugin dependencies, including `autoReferenced: false` assemblies. Query the compilation pipeline as well. Compile-time metadata references alone do not establish runtime assembly resolution; emitted code can still fail at JIT time without resolving the DLL.
- `Assembly.Load(byte[])` does not provide per-evaluation unloading. Generated assemblies accumulate until managed domain reload.

### Preserve snippet source positions

Using directives cannot remain inside the generated runner method. Hoist only `UsingDirectiveSyntax`, excluding using statements/declarations, and blank its `Span` with spaces. `FullSpan` includes comments and trivia; deleting lines shifts diagnostic coordinates and can erase statements sharing a line. The fallback extractor must preserve those same boundaries. Do not inject optional namespaces such as `UnityEngine.UI`, which may not exist in the project.

Classify value returns in the snippet body through syntax, not a search for `return`. Returns inside lambdas or local functions do not make the outer snippet value-returning; `return;` is also distinct from `return value;`. Choose one wrapper and compile once, rather than retrying compilation under guessed expression/statement forms.

### Formatter boundaries

Specific type matchers must precede base types (`Transform` before `Component`). Preserve custom registrations during default initialization. Use a shared recursion context and reference identity for the active path: a repeated reference in another branch is not a cycle.
Introspection in EditMode must avoid calling mutating component property getters (`Renderer.material`, `Renderer.materials`, `MeshFilter.mesh`, `Collider.material`), which instantiate copies, leak assets, and dirty scene state; inspect safe shared accessors (`sharedMaterial`, `sharedMesh`) and skip `[Obsolete]` members instead.

Depth, item, and output caps do not stop a blocking getter or `MoveNext`. UTF-16 length also does not bound UTF-8 bytes; truncation must respect both and avoid splitting surrogate pairs. Allocate a reusable `stackalloc` buffer outside loops, since repeated stack allocations remain until the method returns.

### Coverage reflection

- Coverage accepts project-relative conventions such as `/Assets/...` and whole-project `.`/`./`. Reuse canonical paths after existence validation, including `.` and `..` segments. Normalize project-relative conventions explicitly and enforce directory boundaries so `Assets/Scripts` does not match `Assets/ScriptsExtended`.

- Traverse compiler-generated nested types for async methods, iterators, and lambdas, but avoid revisiting nested types already returned by `Assembly.GetTypes()`.
- Enumerate instance constructors separately from `TypeInitializer`; requesting static constructors in both paths double-counts `.cctor`.
- Exclude hidden sequence points (`0xfeefee`) and nonpositive lines. Unity's sequence-point line field is unsigned; DTO conversion needs an explicit checked/validated boundary.
- A source line with several sequence points is uncovered when any point is unhit, even if another point on that line ran.
- Use a `Coverage` type alias where importing both TestTools namespaces would make `TestMode` ambiguous.

### Coverage runtime evidence

The existing PlayMode coverage gate assumes Unity 6.7 is a persistence boundary. Version parsing is not proof of this behavior: Unity's June 2026 update describes 6.7 CoreCLR as an experimental desktop **player**, with a full CoreCLR Editor targeted for 7.0. Editor PlayMode reload settings also vary. Do not claim that every 6.7+ Editor preserves hit counters or every earlier configuration necessarily loses them. Verify counter persistence with the actual Editor, Test Framework, and reload settings before changing or relying on the gate. [Unity runtime update](https://discussions.unity.com/t/coreclr-scripting-and-serialization-update-june-2026/1723299).

Disabling `Coverage.enabled` discards native hit counters on the verified Unity 6000.0 Editor; re-enabling coverage cannot recover them. Before restoring the prior profiler flag, test completion serializes project sequence-point reports into native `SessionState`. The Test Framework invokes EditMode `RunFinished` before its final cleanup and assembly unlock, so a pending reload can follow the completion callback. A static managed snapshot is insufficient: the serialized snapshot must survive that reload. Queries filter the completed report without enabling the profiler or consulting mutable native counters. A new coverage run clears the previous snapshot; Editor exit clears session state. A missing snapshot requires another coverage test run. The report describes source at capture time and is session memory, not a persistent coverage report on disk.

### Test Runner callback ownership

Earlier code tied registration to the lifetime of a `TestRunnerApi` Unity object. A destroyed native object can compare equal to `null` while its managed wrapper remains, so recreating the API and registering the same callback again produced duplicate reports. The current bootstrap creates a named callback owner, rebinds persisted run identity after reload, and removes only its own prior owners. A global sweep of every `TestRunnerApi` object breaks other Editor tooling.

### Test filters and results

Unity Test Framework `groupNames` are .NET regular expressions; `testNames` are exact full names. Do not translate globs heuristically. Parse every group expression before host refresh and again at the Editor boundary, reporting its array index and regex parse error. Exact name, category, and assembly filters are not regex-validated. Invalid filter entries must fail rather than silently becoming an unfiltered suite. Explicit EditMode/PlayMode selection avoids ambiguous discovery.

Runner startup failures can return zero tests. Check failure before emitting an empty-filter message. Keep the first nonempty line for compact failure summaries, reserve output space for later failures, and sanitize stack traces only for failures receiving details. Recognize package paths containing `@`, `+`, and Unicode when locating diagnostics. Synthetic `eval` locations are snippet coordinates, not files.

## Code and history

[EvalHandler](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs), [OperationExecutionEngine](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/OperationExecutionEngine.cs), [RunTestsHandler](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/RunTestsHandler.cs).

History anchors (inspect with `git show <commit>`): `09280c8` and `c1e749d` (callback lifetime and ownership), `84e7484` (return classification), `dc38551` and `bff9f8b` (async execution; standalone execute tool since removed).
