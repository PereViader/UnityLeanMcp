# Testing, configuration, and release

Read when selecting validation, editing shared host/Editor source, changing fixtures, generating MCP configuration, or packaging a release. These details are intentionally outside the always-loaded contributor rules.

## Decisions

### Configuration and release artifacts

The installer runs `dotnet UnityLeanMcp.Mcp.dll` from the discovered package `MCP~` directory. Project selection prefers `--project`/`-p`, then `UNITY_LEAN_MCP_PROJECT_ROOT`, then working-directory discovery. Discovery walks ancestors for `Assets` and `ProjectSettings`, including this repository's `src/UnityLeanMcp.Unity3d` layout, and otherwise falls back to the working directory. For a package outside the project ancestry, set an explicit project; locating the DLL alone does not select the right Editor.

Generated VS Code and Claude configurations use repository variables where possible. Cursor, Antigravity, Codex, and external-package fallbacks use machine-specific absolute paths; keep those generated paths out of tracked artifacts. These are installer choices, not universal claims about client capabilities. Codex's generated configuration currently sets `tool_timeout_sec = 1800`; client deadlines are outside the server's unbounded-operation contract.

Configuration editing uses the supported Newtonsoft JSON package to preserve unrelated values and replace only the target server; parse failures leave existing files untouched. Stage configuration files beside their destinations and publish atomically.

`.env.shared` supplies the release version that `build.sh` writes into the generated package's `package.json`.

`build/` is regenerated, ignored output. Edit the package under `src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp`, not its generated copy. `build.sh` copies package sources excluding the embedded `MCP~`, then publishes a fresh Release host into `build/MCP~`. Synchronizing the local package's `MCP~` stages and verifies the complete artifact set before replacing the installed directory, including removing obsolete files. Failed publication restores the previous directory; failed rollback retains an explicit recovery path. Earlier best-effort per-file copying could leave a stale or mixed local install even when the release artifact was valid; verify the binary the consumer actually launches.

The release workflow checks `v<VERSION>` and runs its test/publish jobs only for a release that does not already exist. Publication depends on the test job. A push using an existing version is not evidence that CI ran tests; invoke the reusable/manual Run Tests workflow when that validation is needed. See the workflow files for current signing, credentials, and packaging details.

### Verification boundaries

Tests use `Category=Unit` for isolated logic, `Category=Subsystem` for mocked transports/processes, and `Category=UnityIntegration` for live Editor behavior.

- Prefer precompiled Unity fixtures and persistent MCP client sessions. When a fixture changes source, await refresh explicitly; intentional compile-error fixtures may return a failed refresh result.
- Integration clients use the successfully published Release package DLL. Subsystem subprocess tests explicitly use their current build rather than a possibly stale packaged DLL.
- Isolate host process discovery and environment changes. Child-process fixtures use the test executable, not PATH-dependent shell utilities.
- Control admission, completion, cancellation, and contention with explicit signals. Do not make test correctness depend on ThreadPool timing or fixed sleeps.
- Mock the actual refresh contract: accepted operations publish correlated results; rejected IDs do not own foreign compilation. Test both lost acknowledgement and publication failure across reload boundaries.

Live integration tests share and modify `src/UnityLeanMcp.Unity3d`; their xUnit collection disables parallel execution. Fixture replacement backs up/restores both the script and its `.meta` file, and teardown can stop the Editor. These tests are not disposable in-memory tests and should run against the intended test project.

From the repository root, select the needed boundary:

```sh
dotnet test src/UnityLeanMcp.Mcp.Tests/UnityLeanMcp.Mcp.Tests.csproj --filter 'Category=Unit|Category=Subsystem'
dotnet test src/UnityLeanMcp.Mcp.Tests/UnityLeanMcp.Mcp.Tests.csproj --filter 'Category=UnityIntegration'
```

The latter needs a usable Unity installation/license. Headless execution uses explicit `UNITY_BATCHMODE=true`. Container license activation/return and the Editor-launch wrapper are separate from normal executable discovery; consult the current workflow instead of reviving removed CLI launch heuristics.

## Learned pitfalls

### Shared source and schemas

The MCP host targets .NET 10; the Editor package declares Unity 2021.3 support and uses an Editor-only assembly definition. Host compiler success does not establish package compatibility. Keep package source within its supported C# 9/API surface, and verify Editor compilation for package changes; linked tests with stubs do not validate real Unity thread affinity or lifecycle behavior.

Unity package files linked into .NET projects need explicit imports even when the host enables implicit usings. Per-item `<Nullable>` metadata on `<Compile>` does not disable nullable analysis for linked source; the Unity subtree's `.editorconfig` scopes those diagnostic suppressions.

`JsonConverterAttribute` cannot decorate a method parameter. Use native array/enum tool parameters and verify the emitted MCP schema, not only reflection metadata.

### Mocking asynchronous work

Mocks must complete the refresh barrier before delaying the eval/test command under examination. Eager refresh success files can mask deliberately failing poll responses. A mock `READY` without a correlated file cannot complete an accepted refresh; return control to the mock's default publisher or write an explicit result. Busy requests must wait for availability and then be admitted under their own IDs.

`OperationPoller` invokes `OnResultFound` for custom terminal responses. A custom response handler that also runs completion logic duplicates enrichment/progress. Use the polling specification's result path rather than assuming an operation kind.

### Isolated test environments

- Inject process discovery so unrelated local Editors cannot affect tests. Launch known test child executables rather than relying on `sleep`, `ping`, or PATH.
- Process-wide environment changes affect parallel tests. Preserve access to the real `dotnet` CLI when testing PATH behavior; `Environment.ProcessPath` under `dotnet test` may be the test host.
- Release simulated contention with explicit synchronization, not a ThreadPool callback whose scheduling depends on blocked test workers. Readiness stubs must stay false until the simulated process is ready.
- Use `Assert.ThrowsAnyAsync<OperationCanceledException>` where either it or its `TaskCanceledException` subclass is valid; xUnit's exact-type assertion rejects the subclass.
- On case-insensitive volumes, probing `MCP~` may resolve an existing `mcp~`. Tests must not assume the returned path spelling changes.

### Configuration and client limits

UPM package discovery can return locations outside the project; locating the server DLL and resolving the target project are separate checks. Package lookup can fall back from `PackageInfo.FindForAssembly` to source location and known package directories, with both `MCP~` and `mcp~` supported.

Newtonsoft JSON defaults to interpreting ISO date strings as dates. Disable date parsing when editing configuration so unrelated string values retain their exact content.

Client timeout policies differ and can change. Progress notifications help show continued work but cannot guarantee a client keeps a call alive indefinitely. Distinguish the server's no-deadline policy from generated client configuration and caller cancellation; do not document guessed universal client timeout values.

## Code and history

[build.sh](../build.sh), [UnityIntegrationFixture](../src/UnityLeanMcp.Mcp.Tests/UnityIntegrationFixture.cs), [RunTests.yml](../.github/workflows/RunTests.yml), [TestAndPublish.yml](../.github/workflows/TestAndPublish.yml).

History anchors (inspect with `git show <commit>`): `879774d` (Unity language compatibility and linked tests), `5c42eda` and `4d70fc3` (fresh, untracked build output), `510e9eb` (obsolete local artifacts), `f9b3acd` (test-gated release).
