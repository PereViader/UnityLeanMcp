# Processes and transport

Read when changing Editor discovery/startup, process identity, filesystem IPC, sockets, or MCP stdio. For result ownership and publication ordering, use [operation lifecycle](operation-lifecycle.md).

## Decisions

### Process discovery and startup

Probe the project-bound socket before process heuristics. `UnityProcessManager` owns startup/shutdown orchestration; the identity store owns sidecar persistence and validated process acquisition; path resolution remains separate.

- Serialize auto-start across hosts with a persistent startup lock file held open through readiness. Recheck ownership after acquiring it. Never unlink the held file.
- Publish process identity before the PID pointer. Validate PID, start time, executable, and project root; a PID alone is unsafe after reuse.
- A held Unity project lock blocks conflicting startup even when its owner cannot be identified. It does not authorize terminating a process. Never delete Unity's own lockfile.
- Validate executable candidates by installed Editor layout and executable permissions, not filename alone. Configured directories, Hub installs, and PATH candidates use the same validation. Return path and diagnostic together in an immutable result.
- Default auto-start is interactive. `UNITY_BATCHMODE=true`, `1`, or `yes` explicitly selects `-batchmode -nographics`; do not infer this from CI/display variables.
- Put startup output in `Temp/unity_background_log.txt`. Scan new log content from the captured launch offset while monitoring a process this host started; do not use historical startup logs as current compilation evidence when attaching to a running Editor. Compilation failure reports diagnostics and leaves the Editor open.
- Internal stop operations protect GUI sessions unless explicitly forced. The public MCP catalog has no stop tool.
- Dispose process handles acquired internally. Injected process providers retain ownership of their supplied handles.

### Project-bound socket protocol

Each loopback TCP connection carries one line command and one response. Every command uses `PROJECT <base64 normalized project root> <command>`; the server validates the project on that same connection before dispatch. A discovered port or bare `PONG` is not project identity. Protocol project comparison is case-insensitive on Windows and ordinal on Unix.

Payload encoding must preserve backslashes, quotes, and line-control characters. Status decoding is centralized in `ProtocolCodec`; diagnostic formatters receive decoded content. Test requests use structured JSON, not CLI argument aliases.

## Learned pitfalls

### Atomic publication and contention

Windows readers without delete sharing can block destination replacement. Both `IOException` and `UnauthorizedAccessException` can represent transient contention, but neither always does; permissions and invalid paths can produce persistent failures. Keep failures visible and preserve pending terminal ownership rather than reporting false completion.

Operation-scoped names prevent collisions between different operations. They do not guarantee a destination is absent on retry. The Editor publisher uses native move-overwrite because the newer `File.Move(..., overwrite)` overload is unavailable on its Unity 2021.3 API surface. Best-effort shared-history writes must not delay authoritative result delivery.

Renaming a loaded DLL before publishing can work on Windows when its open handles allow delete sharing. It is conditional, not a universal way to replace locked files. Likewise, atomic rename does not imply a power-loss durability guarantee.

### Locks are not ownership

A Unity lockfile can be empty, stale, or unattributable. A detected held project lock blocks startup but cannot identify a process to kill. On Linux, `fcntl` record locks and `flock` locks are distinct; a successful .NET exclusive-open probe does not necessarily prove Unity's lock is absent. Never delete Unity's lockfile based on that probe.

An MCP startup claim must retain the same inode for its whole lifetime. Unlinking an open lock on POSIX allows another caller to create and lock a different inode while the original caller still holds its lock.

### Path identity

- `Path.GetFullPath` does not resolve symlinks. Process executable verification may need the final symlink target to match OS-reported identity.
- macOS commonly uses case-insensitive volumes but also supports case-sensitive ones. Do not infer universal filesystem equality from the OS. The current process identity helper uses case-insensitive comparison on Windows/macOS; project protocol validation deliberately uses ordinal comparison on Unix. Tests should expose this distinction rather than claim one policy fits every volume.
- `Path.IsPathRooted` uses the host OS's syntax. Diagnostic URI conversion must recognize Windows drive paths even when running on Unix.
- Windows quoted arguments need correct trailing-backslash handling; blindly appending a closing quote can swallow following arguments. Preserve filesystem roots when normalizing paths.
- Resolve relative `-projectPath` arguments against the target process's working directory, not the MCP host's directory. If that context is unavailable, do not invent ownership evidence.

### Process discovery pitfalls

A native executable named `Unity` may be a CLI, not the Editor. Validate the installed Editor layout without executing candidates. `UNITY_PATH` may name an installation directory rather than its executable.

A PID can be reused. The identity store also checks start time, executable, and project root. Linux start-time readings can differ in precision; the current tolerance is an identity comparison allowance, not an operation timeout or proof against every PID-reuse scenario. Restricted process inspection may require `/proc` executable or command-line evidence and can still be unavailable.

macOS `KERN_PROCARGS2` is `[argc][exec_path\0][padding][argv...][env...]`. Skip the executable path and padding, then read all `argc` arguments; otherwise the final project-path argument can be lost.

Dispose internally acquired `Process` objects, including candidates from discovery arrays. Capture PID values before disposal; querying a disposed wrapper can throw. Do not dispose process objects owned by injected providers.

### Transport details

A port can be reused by another project after an Editor crash. Project validation must accompany every command on its own connection, not just a previous probe. Open a new connection for each command; the server closes it after the response.

Do not enable `SO_REUSEADDR` on Windows listeners: Winsock permits overlapping binds unlike the intended POSIX rebinding use. Select the OS through managed APIs on listener threads.

Escape backslashes before interpreting line escapes or Windows paths can become control characters. Decode status prefixes centrally, including colon-delimited forms; fixed substring lengths and whitespace-first parsing can drop or retain payload characters. Match status tokens/prefixes rather than searching diagnostic text for words such as `busy`.

### Historical startup logs

A previous launch's compiler errors can abort attachment to a healthy Editor if the whole background log is scanned. Record the launch offset and read only new content while monitoring a process started by this host. If the file is shorter than that offset, read from the beginning because it was truncated. This protects the launch window; it is separate from the refresh-window Console diagnostic rules.

### MCP stdio and encoding

In server mode, stdout carries MCP protocol traffic; route diagnostic logging to stderr. Configure UTF-8 explicitly for the host console and redirected child streams rather than relying on platform defaults. Editor IPC writers use UTF-8 without a BOM so prefix-based readers do not encounter an invisible leading character.

Redirected stderr must be drained continuously: a full pipe can stall the child while the parent waits for a protocol reply. Keep only a bounded tail and include it with process-exit/EOF diagnostics. Discarding all stderr loses the actual startup error; retaining everything creates unbounded memory growth.

Externally killing an IDE-owned stdio server destroys that session's pipes. Some hosts do not recreate the child automatically; restart through the host's MCP controls or reload its session after rebuilding. Replacing the DLL does not update the process already running it. The historical Antigravity failure does not establish a universal respawn policy or client timeout.

## Code and history

[UnityProcessManager](../src/UnityLeanMcp.Mcp/UnityProcessManager.cs), [Program](../src/UnityLeanMcp.Mcp/Program.cs), [McpTestClient](../src/UnityLeanMcp.Mcp.Tests/McpTestClient.cs).

History anchors (inspect with `git show <commit>`): `a536956` (startup log offsets), `208924c` (UTF-8 streams), `879774d` (bounded stderr diagnostics), `6401631` (stdio child termination).
