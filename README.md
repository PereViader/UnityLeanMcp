[![Test and publish](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityLeanMcp?include_prereleases)](https://github.com/PereViader/UnityLeanMcp/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityleanmcp?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityleanmcp/)

# UnityLeanMcp

A lean, native **Model Context Protocol (MCP)** server that connects AI coding agents (Antigravity, Claude Code, Cursor, VS Code, Codex) directly to the Unity Editor without polluting the context window.

By communicating with a running Unity Editor (or a headless background instance) via loopback TCP sockets and exposing standard JSON-RPC stdio MCP tools, UnityLeanMcp enables sub-second compilation feedback, instant test execution, and dynamic C# evaluation without shell quoting issues, slow batchmode restarts, or heavy token overhead.

---

## Overview & Key Capabilities

UnityLeanMcp provides 4 focused, token-optimized MCP tools:

1. **`unity_refresh`**: Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Optional `clean` flag forces clean rebuild by clearing compiler cache when recovering from stale/corrupted cache. Use to verify compilation after editing scripts. Note: unity_run_tests and unity_eval automatically refresh pending changes beforehand, so calling unity_refresh immediately before those tools is unnecessary.
2. **`unity_eval`**: Evaluates C# top-level script source code in-memory against the active Unity Editor. Write code directly as top-level statements without class or method wrappers. Supports top-level `await` and `return <value>;`. No default namespaces are pre-imported; include `using UnityEngine;` for Unity types.
3. **`unity_run_tests`**: Runs EditMode/PlayMode tests with failure diagnostics.
4. **`unity_stop`**: Safely terminates the background Unity Editor instance (to release project locks or recover from hangs).

---

## MCP Tools at a Glance

| Tool | Parameters | Description |
| :--- | :--- | :--- |
| **`unity_refresh`** | `clean` (optional bool, default `false`) | Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Set `clean: true` only to force clean rebuild by clearing compiler cache. Use to verify compilation after editing scripts. Note: unity_run_tests and unity_eval automatically refresh pending changes beforehand, so calling unity_refresh immediately before those tools is unnecessary. |
| **`unity_eval`** | `code` (string: raw C# text) | Evaluates C# top-level script source code in-memory. Supports top-level `await` and `return <value>;`. No default namespaces are pre-imported; include `using UnityEngine;` for Unity types. |
| **`unity_run_tests`** | `testNames`, `groupNames`, `categoryNames`, `assemblyNames`, `mode` (`all`, `editmode`, `playmode`), `failedOnly` | Runs EditMode/PlayMode tests with failure diagnostics. Supports string or array of strings for filter parameters. |
| **`unity_stop`** | `force` (optional bool, default `false`) | Safely terminates the background instance (used to release GUI locks or recover; do not stop routinely). |

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

For this checkout, the supported project configuration paths are `.vscode/mcp.json` for VS Code, `.cursor/mcp.json` for Cursor, and `.codex/config.toml` for Codex. The tracked VS Code, Cursor, and Claude Code files use paths relative to the repository root, so they can be used after cloning on Windows, macOS, or Linux. The Unity installer regenerates the JSON project files from the resolved package location; it uses the same relative form when the package is inside the repository and falls back to an absolute path when a package cache is outside the repository. Codex configuration is generated locally under `.codex/` and remains machine-specific because that directory is ignored.

### Manual MCP Server Configuration

If configuring manually, add the following to your MCP client configuration:

```json
{
  "mcpServers": {
    "unity-lean-mcp": {
      "command": "dotnet",
      "args": [
        "src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/MCP~/UnityLeanMcp.Mcp.dll",
        "--project",
        "src/UnityLeanMcp.Unity3d"
      ]
    }
  }
}
```

Run the host from the checkout root so the relative DLL and project paths resolve correctly. For a Unity project installed as a package outside this repository, use **Tools > UnityLeanMcp > Install MCP Configurations** so the installer can write the resolved package path.

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
