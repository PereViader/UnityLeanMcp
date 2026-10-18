# Remaining Core Review Fixes (Step 5 & Step 6)

Completed in previous session:
- **Fix Step 1**: Editor Server Bootstrap, Threading, Command Gate & Recovery
- **Fix Step 2**: Editor Compilation Tracking, Refresh & Console Diagnostics
- **Fix Step 3**: Editor C# Evaluation, Roslyn Compilation & Result Introspection
- **Fix Step 4**: Editor Test Runner Integration & Code Coverage

---

## Fix Step 5: MCP Host Process Discovery, Identity, Startup & Transport (`Mcp/Process & Transport`)

- [ ] **[HIGH] Relative `-projectPath` in `CommandLineTargetsProject` Resolves Against MCP Host CWD First** ([`UnityProcessManager.cs`](src/UnityLeanMcp.Mcp/UnityProcessManager.cs#L612-L636)):
  - Check `Path.IsPathRooted(projectPathArg)` before calling `FileUnityProcessIdentityStore.PathsEqual(projectPathArg, _pathResolver.ProjectRoot)`. Only resolve relative `projectPathArg` values (such as `-projectPath .` or `../Foo`) against the target process's verified CWD (`TryGetLinuxProcessCwd`), never against the MCP host's CWD.
- [ ] **[MEDIUM] Symlinked Project Paths Cause Infinite Readiness Wait Between Process Discovery and Socket Envelope** ([`ProjectCommandEnvelope.cs`](src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/ProjectCommandEnvelope.cs#L39-L44), [`UnityPathResolver.cs`](src/UnityLeanMcp.Mcp/UnityPathResolver.cs#L40), [`Program.cs`](src/UnityLeanMcp.Mcp/Program.cs#L52)):
  - Canonicalize `ProjectRoot` consistently across `UnityPathResolver` and `ProjectCommandEnvelope.Normalize` so symlinked project paths (or macOS `/var` -> `/private/var`) do not cause `IsUnityRunning` (`ResolveRealPath`) to return `true` while `ProjectCommandEnvelope` (`Path.GetFullPath`) rejects `PING`.
- [ ] **[MEDIUM] `ProtocolCodec.StripStatusPrefix` Truncates the First Word of Arbitrary Non-Prefixed Messages** ([`ProtocolCodec.cs`](src/UnityLeanMcp.Mcp/ProtocolCodec.cs#L144-L181)):
  - Only strip known status token prefixes (`ERROR`, `FAILURE`, `SUCCESS`, `INTERRUPTION`, `BUSY`, etc., followed by `:` or whitespace) instead of blindly stripping everything before the first space (`trimmed[(spaceIdx + 1)..]`) in any multi-word string.
- [ ] **[LOW] `TryReadWindows` Limited-Access Fallback** ([`UnityProcessCommandLineReader.cs`](src/UnityLeanMcp.Mcp/UnityProcessCommandLineReader.cs#L201-L206)):
  - Fall back to `WindowsProcessQueryLimitedInformation` (`0x1000`) when `OpenProcess` with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` fails, since `NtQueryInformationProcess(ProcessCommandLineInformation)` only requires `PROCESS_QUERY_LIMITED_INFORMATION`.
- [ ] **[LOW] `UnityProcessManager.WriteTextAtomically` Overwrite & Helper Consolidation** ([`UnityProcessManager.cs`](src/UnityLeanMcp.Mcp/UnityProcessManager.cs#L331-L365), [`UnityProcessIdentityStore.cs`](src/UnityLeanMcp.Mcp/UnityProcessIdentityStore.cs#L146-L211)):
  - Use `File.Move(temporaryPath, path, overwrite: true)` in `UnityProcessManager.WriteTextAtomically` (matching `FileUnityProcessIdentityStore.WriteTextAtomically`) and consolidate duplicate `ReadFileWithRetry` / `WriteTextAtomically` helpers.

---

## Fix Step 6: MCP Host Client Dispatch, Polling, Cancellation & Tool Surface (`Mcp/Client & Tools`)

- [ ] **[HIGH] Multibyte UTF-8 in Detailed Test Failures (and Eval Logs) Starves Reserved Summary / Result Capacity** ([`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L120-L134), [`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L680-L690), [`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L771-L772), [`McpOutputLimits.cs`](src/UnityLeanMcp.Mcp/McpOutputLimits.cs#L73-L75)):
  - Partition both UTF-16 character budgets and UTF-8 byte budgets when constructing sub-builders (`detailedOutput` / `summaryOutput` in `FormatTestFailures`, and `FormatLogs` in `UnityEvalAsync`) so multibyte UTF-8 in detailed failures or logs cannot exhaust the outer builder's 65,536-byte limit and drop reserved compact summaries or `Result:`/`Error:` sections.
- [ ] **[HIGH] Missing Newline in `UnityCoverageAsync` for Files With 100% Coverage Concatenates Output Lines** ([`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L414-L442)):
  - Append a newline (`\n`) after `output.Append(" points)");` when `file.UncoveredLines` is null or empty so 100%-covered files do not concatenate the next file's bullet (`• Assets/...`) onto the same line.
- [ ] **[HIGH] `DispatchMutatingCommandAsync` Same-ID Retry Handoff on `BUSY <kind> <sameOpId>`** ([`UnityClient.cs`](src/UnityLeanMcp.Mcp/UnityClient.cs#L685-L740)):
  - Check `resultFile` at the start of retry iterations in `DispatchMutatingCommandAsync` (matching `RefreshAsync`), and when `busyInfo.isBusy` is true with `busyInfo.opId == opId`, transition directly to `pollExecutor(opId, resultFile, cancellationToken)` instead of calling `WaitForActiveOperationAsync` (`POLL_REFRESH`).
- [ ] **[MEDIUM] `RefreshAsync` Cancellation Retention During Retry Backoff and Polling** ([`UnityClient.cs`](src/UnityLeanMcp.Mcp/UnityClient.cs#L242-L284), [`UnityClient.cs`](src/UnityLeanMcp.Mcp/UnityClient.cs#L329-L334)):
  - Track `cancellationMayBeNeeded` across ambiguous ack retry backoff in `RefreshAsync` (sending `CancelOperationAsync` if cancelled during `Task.Delay` after a sent command) and enable cancellation in `WaitForCompilationToSettleAsync`.
- [ ] **[MEDIUM] `s_StackTraceRegex` and `s_BareAtRegex` Support for Windows UNC Paths and Non-ASCII Relative First Segments** ([`DiagnosticFormatter.cs`](src/UnityLeanMcp.Mcp/DiagnosticFormatter.cs#L30-L36)):
  - Allow `[\\/]` (including UNC `\\server\share\...`) and Unicode characters in the relative first-segment character class in `s_StackTraceRegex` and `s_BareAtRegex`.
- [ ] **[MEDIUM] `GetCoverageAsync` Wire Payload Unescaping & `INTERRUPTION` Handling** ([`UnityClient.cs`](src/UnityLeanMcp.Mcp/UnityClient.cs#L866-L880)):
  - Call `ProtocolCodec.UnescapeLine(ProtocolCodec.StripStatusPrefix(response))` on `FAILURE`/`ERROR`/`INTERRUPTION` responses in `GetCoverageAsync`.
- [ ] **[MEDIUM] Malformed Markdown Link Guard When `FileUri` Exceeds Identifier Limit** ([`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L665-L668), [`UnityTools.cs`](src/UnityLeanMcp.Mcp/UnityTools.cs#L717-L725)):
  - Avoid wrapping a truncated `FileUri` (containing spaces/parentheses in `FailureIdentifierTruncationMarker`) inside `[path:line](fileUri)` Markdown link syntax; render plain `filePath:line` when `FileUri` exceeds the budget.
- [x] **[LOW] `PollIntervalMs` in `EvalAsync`, `int.TryParse` in `ParseCompilerDiagnostics`, and `OperationPoller` Terminal Cleanup Consistency** ([`UnityClient.cs`](src/UnityLeanMcp.Mcp/UnityClient.cs#L447), [`DiagnosticFormatter.cs`](src/UnityLeanMcp.Mcp/DiagnosticFormatter.cs#L454-L455), [`OperationPoller.cs`](src/UnityLeanMcp.Mcp/OperationPoller.cs#L196-L201), [`OperationPoller.cs`](src/UnityLeanMcp.Mcp/OperationPoller.cs#L290-L294)):
  - Use `PollIntervalMs = PollIntervalMs` instead of hardcoded `500` in `EvalAsync`; replace `int.Parse` with `int.TryParse` in `DiagnosticFormatter.ParseCompilerDiagnostics`; apply `spec.OnResultFound` return value and `DeleteResultFileSilently(spec)` consistently in `OperationPoller`.
