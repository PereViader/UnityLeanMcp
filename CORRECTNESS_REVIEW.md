# Correctness review memory

## Contract and workflow

- Requests from all MCP hosts must wait until preceding Unity work actually finishes. Caller cancellation is not proof that backing work has stopped.
- Preserve the developer's Editor state; tolerate externally triggered domain reloads; support Windows, Linux, and macOS; use no arbitrary operation deadlines or obsolete protocol compatibility.
- A reviewer subagent identifies evidence-backed issues; a separate fixer subagent implements fixes. Repeat independent review and validation until no actionable findings remain.
- Update this file with stable issue IDs, evidence, resolution, verification, and rejected hypotheses so subsequent passes do not repeat work.

## Initial state (2026-09-30)

- Existing untracked files: `unity_background_log.txt`, `src/UnityLeanMcp.Unity3d/unity_background_log.txt`. Preserve them.
- At the start of this review, implementation had migrated ownership from disk journals to `UnityCommandGate`/`SessionState`, while some architecture documentation still described the previous model. Current documentation starts at [docs/README.md](docs/README.md).
- Review cycle 1 underway: concurrency, cancellation, reload recovery, result publication, and developer-state preservation.
- Baseline non-Unity suite: 514 passed, 2 failed (installer path casing and stale packaged MCP executable; CR-006). Sandbox blocked loopback; test runs use approved unsandboxed dotnet test.

## Findings

| ID | Correctness issue | Resolution |
| --- | --- | --- |
| CR-001 | Foreign-operation waits failed after three seconds. | Indefinite, cancellable waits with progress and paced admission retries; obsolete timeout option removed. |
| CR-002 | Result-write failures and unknown runner state released ownership prematurely. | SessionState retains exact pending terminal payload; one publication attempt per Editor update; reload resumes delivery; unknown runner state retries conservatively. |
| CR-003 | Reload or cancellation lost/deferred unaccepted PlayMode-exit commands; null acknowledgements were mistaken for acceptance. | No retained command delegate; retryable PlayMode admission; stable same-ID redispatch after transport loss; main-thread ownership recheck. |
| CR-004 | Completed eval/test retries reexecuted side effects. | Recognize correlated terminal result before claiming ownership or rejecting for unrelated compilation. |
| CR-005 | Historical Console errors poisoned refresh; no-op refresh erased compiler diagnostics. | Capture operation-window non-CS errors without clearing Console; retain per-assembly compiler evidence until compiler callbacks replace it. |
| CR-006 | Tests assumed case-sensitive spelling and launched stale packaged MCP code. | Filesystem-aware casing expectations and explicit current-build subprocess tests; live socket probes assert actual expected responses. |
| CR-007 | Compilation trigger exceptions could later be reported as success. | Publish correlated failure through the completion path; observer cannot overwrite pending failure. |
| CR-008 | Test cleanup disabled profiling belonging to the developer. | Capture and restore prior profiling state only for owned coverage activation, immediately on actual completion. |
| CR-009 | Startup compilation failures killed auto-started GUI Editor. | Preserve Editor and return diagnostics; process-preservation regression. |
| CR-010 | MCP commands could interfere with manually started Test Runner work. | External/unknown test activity blocks mutation and PlayMode-exit admission; polling remains paced. |
| CR-011 | Reused stale port could route mutations to another project. | Mandatory expected-project envelope validated on every command connection; no legacy bare-command fallback. |
| CR-012 | Foreign compilation waits polled an unaccepted ID and falsely failed on IDLE. | Wait for availability, then execute and consume the caller's own correlated refresh; removed passive outcome synthesis. |
| CR-013 | clean:true still requested incremental compilation. | Explicit CleanBuildCache option; live test verifies a fresh domain token. |
| CR-014 | Internal status reads retired journal or treats a lost poll after PING as Ready. | Removed journal fallback; require explicit READY; stale-journal and lost-poll regressions pass. |

## Review coverage and validation

- Cycle 1 reviewer inspected ownership, server dispatch/bootstrap/shutdown, client busy/poll/cancellation, eval engine, compilation, test execution/cancellation, coverage, process startup and baseline failures.
- Confirmed correct: eval cancellation signals CTS but retains ownership until underlying task finishes, including tasks ignoring cancellation. Local semaphore release is safe only because foreign requests continue waiting on Unity ownership (CR-001).
- Retry guarantee under CR-004 covers correlated terminal results before consumption; no speculative permanent result tombstone machinery added.
- Installed Unity is 6000.0.68f1; repository project requests 6000.0.77f1. Plan isolated temporary project for live package validation to avoid downgrading repository project settings.
- Reviewer hypothesis pending evidence: case-insensitive coverage path aggregation may merge case-distinct Linux source paths.
- Transport probe timeouts are distinct from operation deadlines; no speculative rewrite currently planned.
- Independent post-fix review and final broad test run pending.

### Cycle 2 (independent interim fix review)

- Found and returned for correction: lost initial acknowledgements need same-ID retry for eval/test as well as refresh.
- Found and returned for correction: main-thread preflight must let an already-owned matching ID reach idempotent handling, or a refresh can consume its own result as foreign and repeat execution.
- Found and returned for correction: profiler restoration must precede terminal publication retries, not wait for disk availability.
- Isolated real Unity 6000.0.68f1 snapshot compile and EditMode suite: 62 passed, 2 skipped, 0 failed. Final edited source must be synchronized and rerun.

### Live validation and third review

- Full non-Unity suite: 525 passed. Final source changes require one final confirmation run.
- Initial final Editor suite: 68 passed, 2 failed, 2 skipped. Completed eval/test retry checks occurred after a compilation busy check. Moved result recognition before that check; strengthened regression by deliberately setting RefreshPending. Rerun: 70 passed, 2 skipped, 0 failed.
- Initial live MCP suite in isolated repo: 56 passed, 3 failed. Missing CS diagnostics traced to clearing authoritative per-assembly diagnostics before a no-op refresh. Preserve them until actual assembly compiler callbacks replace/remove them; added Editor regression.
- Direct socket/status integration assertions now establish readiness first. Obsolete disk-journal status test replaced with a real gated eval. RefreshProbe now uses the project envelope and asserts BUSY eval rather than treating any response as success.
- A hypothesis of prematurely settled refresh remains unproven: Unity 6000.0 source includes pending requests in compilation activity. No speculative lifecycle redesign added.
- CleanBuildCache supported in Unity 2021.3 and 6000.0, verified in official Unity API docs.
- One subagent accidentally included integration tests in an earlier broad run. Audit confirmed no running Editor, fixture/source/settings changes, or killed unknown processes; only ignored published binaries and an ordinary stale startup claim remained. Correct filter subsequently passed all 525 tests.
- Final live MCP and Editor suites are running against updated isolated copies.

### Final validation status

- Final unit/subsystem suite: **528 passed, 0 failed** on macOS/.NET 10.
- Final isolated Unity Editor suite: **71 passed, 2 skipped, 0 failed** on Unity 6000.0.68f1.
- Final isolated live MCP integration suite: **59 passed, 0 failed**, including verified domain replacement on clean recompilation. The final status-only cleanup additionally passed both live ready/busy integration tests.
- Windows/Linux execution has not been performed in this environment. Cross-platform behavior was reviewed and covered by platform-independent regression cases where possible.

### Review closure

The final independent reviewer inspected the last status fixes and found **no remaining actionable correctness findings**. The review/fix cycle and final validation are complete. `git diff --check` passes. Changes remain uncommitted, and the two original untracked log files remain untouched.
