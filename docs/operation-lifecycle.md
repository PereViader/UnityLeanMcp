# Operation lifecycle

Read when changing admission, polling, cancellation, domain reload, server bootstrap, or compiler diagnostics. See the [architecture overview](README.md) for system boundaries.

## Decisions

### Admission and retries

The MCP client assigns a fresh `operationId` to each mutating operation. `UnityCommandGate` serializes mutations in the project Editor, across all MCP hosts; there is no additional tool-level semaphore queue.

- The main thread stores ownership in native `SessionState`, then publishes a managed snapshot for socket workers. Workers never read `SessionState` or a disk ownership journal.
- Matching kind/ID requests are retries. Handlers check both active ownership and an existing correlated result before executing side effects.
- Foreign ownership returns `BUSY`. Clients wait with progress notifications and retry admission; cancellation of a waiter must not cancel the foreign owner.
- A lost acknowledgement does not prove admission: domain reload can discard queued work. Retry the same ID rather than polling an operation that may never have started.
- Main-thread admission also checks compilation and manually started Test Runner jobs. Unknown Test Runner activity blocks admission until it can be determined.
- PlayMode exit returns a retryable busy response. Do not retain a deferred command delegate across the transition; the caller resubmits after exit and ownership is checked again.

### Terminal publication

For an admitted operation:

1. Persist ownership before starting work or publishing running state.
2. Execute the backing operation, keeping resources associated with its ID.
3. Store the exact serialized terminal payload in `SessionState` before attempting file publication.
4. Atomically publish the correlated result. If publication fails, retain ownership and retry on paced Editor updates, including after domain reload.
5. Clean up running state and release ownership only after successful publication.

Backing-work completion and result delivery are separate phases. Restore temporary profiler settings when the backing test finishes, even if result publication must retry. A `finally` block must not release an admitted operation whose terminal result has not been published.

`SessionState` survives domain reload, not Editor process exit. Atomic file visibility is not a guarantee of recovery from process crashes or power loss. Reload recovery restores pending publication before deciding whether interrupted work can resume. Eval cannot survive managed reload; tests require a known runner state before recovery can declare interruption. Lifecycle handlers own command-specific cancellation, reload, and quitting behavior.

### Result files and polling

Runtime IPC, markers, diagnostics, and logs belong under the Unity project's `Temp/` directory. Do not rely on directory cleanup at a particular Editor lifecycle event.

- Refresh and recompile share `unity_refresh_<operationId>.json`; eval and tests use `unity_eval_<operationId>.json` and `unity_test_<operationId>.json`.
- Correlated files are authoritative for operation completion. The poller normally deletes them after consumption; this bounds their retry/deduplication lifetime and is not indefinite exactly-once delivery.
- Static `unity_refresh_result.json` and `unity_test_results.json` are best-effort history, including failed-test reruns. They cannot prove that a new operation completed and are preserved by default.
- Writers stage content in the destination directory and publish with move-overwrite. Editor code uses the native platform helper for Unity's API surface. Unique operation IDs avoid cross-operation collisions, but retries may encounter an existing destination.
- Shared-history publication makes one best-effort attempt after the correlated result is published. History contention must not block operation completion.
- Polling checks the correlated result before active state and rechecks before declaring unexpected idle. Running markers and ownership are progress evidence, not terminal outcomes.
- Bare `POLL_REFRESH` probes availability; `POLL_REFRESH <operationId>` polls a specific operation. Unknown correlated IDs can return `IDLE`. Neither generic `READY` nor a foreign compilation outcome substitutes for a requested refresh's result.
- Use successful project-bound socket replies as liveness evidence; inspect processes when communication fails. Internal status requires an explicit ready response, not merely an earlier successful `PING`.

### Cancellation

`CANCEL_OPERATION <operationId>` targets only the requested operation. Dispatch must also handle cancellation between command send and initial acknowledgement, using an uncancelled token for the out-of-band cancellation request before propagating caller cancellation. Retain cancellation responsibility across ambiguous acknowledgement retries; transfer it to the poller after dispatch. Retry transport failures until a specific cancellation response, matching terminal result, or Editor exit establishes the outcome.

For tests, the worker persists cancellation intent and the main thread calls `TestRunnerApi`. Acceptance does not mean completion. Preserve ownership and the running marker until `RunFinished` or a known inactive runner state; an indefinitely cancelling runner remains busy. Persist intent and job identity so reload recovery can resume cancellation.

### Main-thread and worker boundaries

Unity-dependent initialization and execution run on the main thread. Asset-import worker processes must skip server bootstrap and Test Runner callback registration so they cannot compete with the project Editor for endpoint metadata. Socket acceptance, framing, early rejection, and polling use managed data on worker threads.

- Bootstrap explicitly initializes paths, gate state, compilation tracking, dispatch, Roslyn services, recovery, and lifecycle callbacks before opening the listener. Static initialization must not accidentally invoke main-thread-only APIs from a worker.
- Worker polling uses immutable-by-convention snapshots and a Unity-independent JSON reader. Worker and teardown diagnostics use the managed file logger so they do not depend on engine services during unload.
- Handler metadata (`ExecutionTarget`, `IsMutating`, `RequiresCompilationSettled`) controls dispatch and early busy rejection, including compilation checks for coverage reads.
- Public command registration can precede bootstrap. Add built-in defaults without overwriting external registrations or constructing Unity-dependent handlers on a registration caller's thread.
- Lifecycle handlers return domain results; dispatchers own wire framing. Refresh and recompile share their common lifecycle implementation.
- Resource cancellation and disposal occur outside synchronization locks, after checking the owning operation ID.

During reload or shutdown, signal waiting dispatchers, close the listener under lifecycle synchronization, join the listener thread, then remove endpoint metadata. The listener retries endpoint publication until successful or stopped; a transient metadata write failure cannot leave a permanently undiscoverable listener. Signal and close client sockets without joining workers from the Unity main thread. `EXIT` notifies operation lifecycle handlers before stopping the server and requesting Editor exit; abrupt termination still cannot guarantee terminal publication.

### Compilation and diagnostics

Eval and test execution each cross a correlated normal `AssetDatabase.Refresh` barrier before consuming compiled user code. A readiness probe cannot establish that Unity has noticed external source edits. Normal refresh stays incremental; `unity_refresh(clean: true)` requests `RequestScriptCompilationOptions.CleanBuildCache`.

Compilation settlement tracks pending requests, compilation, and asset updates across multiple idle Editor ticks. These ticks are a settlement heuristic, not a deadline or proof of a universal Unity scheduling bound. Trigger exceptions publish failure and must not later become observer success. Socket acknowledgement failure cannot prevent admitted compilation work from starting; requested ownership alone is not evidence that compilation executed. A reload that restores a compilation request which never reached execution publishes an interrupted result.

Compiler diagnostics describe current assembly state. Capture them in assembly compilation callbacks and retain them across no-op refreshes until replaced by callbacks for that assembly. Non-C# errors emitted during an active import or refresh window—including shader compilation errors, missing MonoBehaviour script references, asset import failures, and asset postprocessor exceptions—are captured at error level and persist across domain reload in `SessionState` before reload occurs; unchanged assets not re-imported during a refresh do not re-emit them. Source locations are normalized to forward-slash project-relative paths and extracted from diagnostic messages, shader error lines, quoted asset paths, or exception stack traces. Preserve the developer's Console and exclude historical gameplay logs. Embed the merged diagnostic snapshot in the correlated refresh result before releasing ownership; clients never enrich it from subsequently shared diagnostics.

## Learned pitfalls

### Reload is not restart

Managed domain reload discards static state, callbacks, dynamic eval assemblies, and transport workers. `SessionState` survives that reload but resets when the Editor process exits. Persist recovery state before triggering reload, and do not depend on a later callback in the unloading domain. Unknown Test Runner state during bootstrap is not evidence that a run ended.

Test Runner rebuilds its nonserialized active-run registry in its own load initializer. Even a successful `IsRunActive() == false` query during another load initializer can precede that restoration. Defer test recovery and resumed cancellation to Editor updates after load initialization, then retry unknown activity and recognize known inactivity as interruption.

`beforeAssemblyReload` leaves little execution lifetime. Listener publication can race teardown: a worker may assign its listener after cleanup observed `null`. Synchronize that handoff and remove endpoint metadata only after listener shutdown. Do not join main-thread-dependent client workers from Unity's main thread.

### Initialization order and thread affinity

The CLR runs a static constructor on the thread that first touches its type; `[InitializeOnLoad]` ordering across assemblies must not be assumed. If that constructor calls `SessionState` or `Application.dataPath` on a worker and throws, the type remains poisoned for the managed domain. Keep managed-only static initialization, and explicitly initialize Unity services and cached paths on the main thread before starting workers.

Registry access can happen before bootstrap. Preserve early custom registrations when adding built-ins. A load-time test must not join a worker that may itself need Unity's main thread for type loading.

Most Unity object APIs require the main thread, but a blanket prohibition on all Unity utility APIs is inaccurate: Unity explicitly permits background-thread `JsonUtility` use with appropriate object synchronization. This project's worker codec and file logger remain Unity-independent to avoid engine lifecycle dependencies during reload and shutdown. [Unity JSON serialization documentation](https://docs.unity.com/en-us/engine/6000.0/manual/scripting/compilation-and-code-reload/script-serialization/json-serialization).

### Refresh observations are asynchronous

A `READY` probe can precede Unity noticing an external edit. `isCompiling == false` can also occur before or between compilation phases. Use the correlated refresh barrier and pending-request tracking, not a single idle observation or a presumed millisecond scheduling bound. Current settlement counts consecutive idle updates; update cadence varies.

Capture compiler messages in `assemblyCompilationFinished` so they can survive reload. No-op refreshes may emit no assembly callbacks: clearing the previous compiler map at admission loses still-current errors. Conversely, old gameplay Console errors must not contaminate the current import window. Capture non-C# error severity during the operation, not by matching words such as `AssetPostprocessor` in historical logs.

### Completion and cancellation races

- A queued command can disappear before admission during reload. A lost socket acknowledgement is ambiguous; same-ID retries must check active state and completed files before side effects.
- A result write can fail after backing work completes. Preserve the exact payload and ownership for paced retries. Orphaned pending payloads without a valid matching ownership record must be discarded during bootstrap.
- A caller can cancel before initial acknowledgement, before the poller's cancellation hook exists. Handle cancellation in dispatch as well as polling.
- Test cancellation must call `TestRunnerApi` on the main thread. `CancelTestRun` may leave a run indefinitely `Cancelling`; only a completion callback or a known inactive state establishes completion.
- An Editor-update delegate waiting for PlayMode exit can vanish on reload or outlive a cancelled caller. Retry admission after exit instead of retaining the command. Observe both `isPlaying` and `isPlayingOrWillChangePlaymode`.
- Test Framework reload locking can defer script changes during a run. Merely editing a script in a test does not establish that the test exercised mid-run domain reload.

### Snapshot publication

Persist native ownership before publishing the worker snapshot; erase native ownership before clearing that snapshot. Workers may briefly observe the preceding snapshot during a transition, so polling must recheck terminal evidence before declaring idle. Do not describe the two writes as an atomic transaction across threads or consult retired disk journals to reconcile them.

## Code and history

[UnityCommandGate](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/UnityCommandGate.cs), [UnityLeanMcpServer](../src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/UnityLeanMcpServer.cs), [OperationPoller](../src/UnityLeanMcp.Mcp/OperationPoller.cs).

History anchors (inspect with `git show <commit>`): `75f3116` (asset-import worker exclusion), `c585b64` (SessionState gate), `c0a105d` (retry pacing and removal of host semaphore).
