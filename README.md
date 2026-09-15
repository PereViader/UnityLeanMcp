[![Test and publish](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityLeanMcp?include_prereleases)](https://github.com/PereViader/UnityLeanMcp/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityleanmcp?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityleanmcp/)

# UnityLeanMcp

A lean, native **Model Context Protocol (MCP)** server that connects AI coding agents (Antigravity, Claude Code, Cursor, VS Code, Codex) directly to the Unity Editor without polluting the context window.

By communicating with a running Unity Editor (or a headless background instance) via loopback TCP sockets and exposing standard JSON-RPC stdio MCP tools, UnityLeanMcp enables sub-second compilation feedback, instant test execution, and dynamic C# evaluation without shell quoting issues, slow batchmode restarts, or heavy token overhead.

---

## Overview & Key Capabilities

UnityLeanMcp provides 4 focused, token-optimized MCP tools:

1. **`unity_refresh`**: Refreshes AssetDatabase and returns compiler diagnostics. Normal refreshes are fast when unchanged. Set `clean: true` only when a more expensive full script recompilation is needed to recover from a stale or corrupted compiler cache.
2. **`unity_eval`**: Evaluates C# top-level script source code in-memory against the active Unity Editor. Evaluation can mutate Unity state, so treat every call as potentially state-changing. Supports top-level `await` and `return <value>;`. No default namespaces are pre-imported; include `using UnityEngine;` for Unity types.
3. **`unity_run_tests`**: Runs EditMode/PlayMode tests with failure diagnostics.
4. **`unity_stop`**: Stops the running Unity instance when explicitly requested. `force: true` may discard unsaved changes in an interactive GUI Editor and requires explicit approval.

---

## MCP Tools at a Glance

| Tool | Parameters | Description |
| :--- | :--- | :--- |
| **`unity_refresh`** | `clean` (optional bool, default `false`) | Refreshes AssetDatabase and returns compiler diagnostics. Set `clean: true` only for a more expensive full script recompilation. |
| **`unity_eval`** | `code` (string: raw C# text) | Evaluates C# top-level script source code in-memory and may mutate Unity state. Supports top-level `await` and `return <value>;`. |
| **`unity_run_tests`** | `testNames`, `groupNames`, `categoryNames`, `assemblyNames` (optional string arrays), `mode` (`all`, `editmode`, `playmode`), `failedOnly` | Runs EditMode/PlayMode tests with failure diagnostics. `groupNames` uses .NET regular expressions. |
| **`unity_stop`** | `force` (optional bool, default `false`) | Stops the running instance. `force: true` may discard unsaved changes in an interactive GUI Editor. |

> **Auto-Start & Warm Instance**: If Unity is not running when an operation is requested, UnityLeanMcp automatically starts a headless background instance in batchmode first and keeps it warm for subsequent commands.

---

## Installation & Setup

### 1. Requirements
- **.NET 10.0 Runtime or SDK** (`dotnet`).
- **Unity**: Version 2021.3 or higher.

### 2. Install the Package

[Install from OpenUPM](https://openupm.com/packages/com.pereviader.unityleanmcp/#modal-manualinstallation):
```bash
openupm add com.pereviader.unityleanmcp
```

### 3. Install MCP Configurations

In the Unity Editor menu, select:
**Tools > UnityLeanMcp > Install MCP Configurations**

This automatically creates or updates the configuration files for:
- **Antigravity**: `.agents/plugins/unity-lean-mcp/mcp_config.json`
- **VS Code**: `.vscode/mcp.json`
- **Cursor**: `.cursor/mcp.json`
- **Claude Code**: `.mcp.json`
- **Codex**: `.codex/config.toml`

For this checkout, the supported project configuration paths are `.vscode/mcp.json` for VS Code, `.cursor/mcp.json` for Cursor, `.mcp.json` for Claude Code, `.agents/plugins/unity-lean-mcp/mcp_config.json` for Antigravity, and `.codex/config.toml` for Codex.
- **VS Code** (`.vscode/mcp.json`) and **Claude Code** (`.mcp.json`) use workspace root variables (`${workspaceFolder}` and `${CLAUDE_PROJECT_DIR:-.}`) in `cwd` so they remain portable and consistent in version control.
- **Antigravity** and **Cursor** require absolute paths in `cwd`; their generated machine-specific configuration files are intentionally excluded from version control.
- **Codex** uses an absolute path in `cwd` and sets `tool_timeout_sec = 1800`.
- All clients set `cwd` to the package `MCP~` folder and execute `dotnet` with `args: ["UnityLeanMcp.Mcp.dll"]`.

### Manual MCP Server Configuration

If configuring manually, add the following to your MCP client configuration (adjusting `cwd` as needed):

```json
{
  "mcpServers": {
    "unity-lean-mcp": {
      "command": "dotnet",
      "args": [
        "UnityLeanMcp.Mcp.dll"
      ],
      "cwd": "C:/Path/To/Your/Project/Packages/com.pereviader.unityleanmcp/MCP~/"
    }
  }
}
```

When `cwd` points to the `MCP~` folder, the MCP server automatically resolves the Unity project root from the current working directory. Use **Tools > UnityLeanMcp > Install MCP Configurations** in Unity to configure all installed IDEs and agent clients automatically.

### Release Versioning

`.env.shared` is the single source of truth for the Unity package release version. To prepare a release, update only its `VERSION` value using the existing scheme: `major.minor.patch` for releases or `major.minor.patch-preview.N` for previews. Do not edit the `version` field in the package manifest separately.

Run `bash ./build.sh` from the repository root. At build time, the version from `.env.shared` is placed on `package.json`.

---

## Testing & MCP Inspection

The MCP server communicates over standard input/output using JSON-RPC. To inspect, test, and interact with the tools interactively, you can use the official MCP Inspector:

```bash
npx @modelcontextprotocol/inspector dotnet src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/MCP~/UnityLeanMcp.Mcp.dll --project src/UnityLeanMcp.Unity3d
```

---

## License

MIT License. See [LICENSE.md](LICENSE.md) for details.
