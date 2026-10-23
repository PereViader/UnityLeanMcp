[![Test and publish](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityLeanMcp/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityLeanMcp?include_prereleases)](https://github.com/PereViader/UnityLeanMcp/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityleanmcp?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityleanmcp/)

# UnityLeanMcp

A lean, native **Model Context Protocol (MCP)** server that connects AI coding agents (Antigravity, Claude Code, Cursor, VS Code, Codex) directly to the Unity Editor without polluting the context window.

By communicating with a running Unity Editor via loopback TCP sockets and exposing standard JSON-RPC stdio MCP tools, UnityLeanMcp enables sub-second compilation feedback, instant test execution, and dynamic C# evaluation without shell quoting issues, slow batchmode restarts, or heavy token overhead.

---

## MCP Tools

UnityLeanMcp provides 4 focused, token-optimized MCP tools:

| Tool | Description |
| :--- | :--- |
| **`unity_refresh`** | Refreshes AssetDatabase and returns compiler and asset import diagnostics (including shader errors, import failures, and missing scripts). C# compiler errors persist across no-op refreshes. Normal refreshes are fast when unchanged; set `clean: true` only when a more expensive full script recompilation is needed to recover from a stale or corrupted compiler cache. |
| **`unity_eval`** | Evaluates C# top-level statements in-memory against the active Unity Editor with top-level `await` and `return <value>;`. Pre-imports `Inspect.*` for token-bounded scene/object introspection (call `return Inspect.Help();` for cheatsheet). Include `using UnityEngine;` for Unity types. |
| **`unity_test`** | Runs EditMode/PlayMode tests with failure diagnostics. Automatically suggests near-matches and cross-mode hints if filters match 0 tests. Supports `coverage: true` to record in-memory code coverage without disk snapshots. |
| **`unity_coverage`** | Queries in-memory code coverage for specified files or directories (recursively evaluating all contained C# scripts) from the latest test run without re-running tests. Reports line coverage percentages and lists uncovered line spans for each file. |

> **Auto-Start & Warm Instance**: If Unity is not running when an operation is requested, UnityLeanMcp automatically starts the Unity Editor in normal interactive mode first and keeps it running for subsequent commands.

### `Inspect` Helpers (`unity_eval`)

Top-level statements evaluated with `unity_eval` automatically import `UnityLeanMcp.Inspect`. These helpers provide token-bounded, recursion-safe scene and object introspection designed for AI agents without polluting the context window. Output is capped at 64 KB and avoids mutating getters (such as `Renderer.material`, using safe `sharedMaterial` instead to avoid scene leaks):

| Category | Method | Description |
| :--- | :--- | :--- |
| **Scene & Hierarchy** | `Inspect.Hierarchy([root], [maxDepth], [maxItems], [includeUiDetails])` | Formatted hierarchy tree of active/loaded scenes, open Prefab Stage, a root `GameObject`/`Transform`, or a scene/prefab by name (supports `includeUiDetails: true` for compact uGUI rect, canvas, text, and raycast tokens). |
| | `Inspect.UIDocument([root], [maxDepth], [maxItems])` | Traverses UI Toolkit `UIDocument` instances or `VisualElement` trees with element types, classes, layout rects, and text content. |
| | `Inspect.PrefabStage([maxDepth], [maxItems])` | Inspects active Prefab Stage (asset path, mode, root GameObject, dirty state, auto-save status, and contents hierarchy). |
| | `Inspect.Find(namePattern, [includeInactive], [maxItems])` | Finds GameObjects across loaded scenes by exact name or wildcard glob pattern (e.g. `*Player*`). |
| | `Inspect.MissingScripts([maxItems])` | Detects GameObjects with missing or unassigned MonoBehaviour scripts. |
| | `Inspect.SceneState()` | Summarizes loaded scenes, active scene, active Prefab Stage (if open), root object count, and dirty state. |
| **Objects & Components** | `Inspect.GameObject(gameObject, [maxItems])` | Detailed inspection of active state, tag, layer, components, and children (`RectTransform` and `Canvas` aware). |
| | `Inspect.Component(component, [maxItems])` | Serialized fields and properties without instantiating copies or mutating state. |
| | `Inspect.Object(target, [maxDepth], [maxItems])` | General reflection-based inspection of arbitrary C# objects or collections. |
| | `Inspect.PrefabOverrides(instance, [maxItems])` | Lists added/removed components and property deltas on a prefab instance relative to the source asset. |
| **Assets & Prefabs** | `Inspect.FindAsset(nameOrPath)` | Resolves asset details (type, GUID, main path) by name or relative path. |
| | `Inspect.FindAssets([filter], [type], [label], [maxItems])` | Searches project assets via AssetDatabase filters (`filter`, `t:type`, `l:label`). |
| | `Inspect.FindPrefabs([filter], [maxItems])` | Finds prefabs in the project matching an optional filter. |
| | `Inspect.Dependencies(assetPath, [recursive], [maxItems])` | Enumerates asset dependencies with normalized paths, type annotations, and truncation limits. |
| | `Inspect.Referencers(targetAssetPath, [searchFolder], [maxItems])` | Queries project assets in a folder that reference or depend on the target asset. |
| **Editor & Tests** | `Inspect.Selection([maxItems])` | Inspects currently selected GameObjects, assets, or active object in the Editor. |
| | `Inspect.MainCamera()` | Properties of `Camera.main` (transform, clear flags, culling mask, FOV, clipping planes). |
| | `Inspect.Tests([filter], [maxItems])` | Enumerates available EditMode and PlayMode tests with an optional name filter. |
| **Utilities** | `Inspect.Help()` | Returns the complete runtime cheatsheet of all available `Inspect.*` signatures. |

Call these directly inside `unity_eval`:

```csharp
// Inspect scene hierarchy up to depth 3
return Inspect.Hierarchy(maxDepth: 3);

// Inspect the currently active Prefab Stage
return Inspect.PrefabStage();

// Find GameObjects by wildcard pattern
return Inspect.Find("*Player*");

// Print the full runtime cheatsheet
return Inspect.Help();
```

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

### Environment Variables & CLI Options

UnityLeanMcp can be configured via command-line options and environment variables for CI/CD pipelines, Docker containers, or non-standard project layouts:

| Variable / Option | Default | Description |
| :--- | :--- | :--- |
| **`--project`**, **`-p <path>`** | Discovered | CLI option specifying the path to the Unity project root directory. |
| **`UNITY_LEAN_MCP_PROJECT_ROOT`** | Discovered | Environment variable specifying the Unity project root directory. Precedence is `--project` > `UNITY_LEAN_MCP_PROJECT_ROOT` > parent directory discovery. |
| **`UNITY_BATCHMODE`** | `false` | When set to `true`, `1`, or `yes`, auto-started Unity instances launch headlessly with `-batchmode -nographics` (ideal for headless CI/CD). Default auto-start is interactive GUI. |
| **`UNITY_PATH`** (or `UNITY_EDITOR`) | Discovered | Path to a custom Unity Editor executable or its installation directory, overriding automatic Unity Hub and `PATH` detection. |
| **`UNITY_LEAN_MCP_POLL_INTERVAL_MS`** | `500` | Polling backoff cap in milliseconds for long-running Editor operations. |

When neither `--project` nor `UNITY_LEAN_MCP_PROJECT_ROOT` is provided, UnityLeanMcp resolves the project root by walking parent directories of the current working directory (`cwd`) until finding `Assets` and `ProjectSettings`.

Example MCP client configuration with custom environment variables and CLI options:

```json
{
  "mcpServers": {
    "unity-lean-mcp": {
      "command": "dotnet",
      "args": [
        "UnityLeanMcp.Mcp.dll",
        "--project",
        "/path/to/unity/project"
      ],
      "cwd": "/path/to/unity/project/Packages/com.pereviader.unityleanmcp/MCP~/",
      "env": {
        "UNITY_BATCHMODE": "true",
        "UNITY_PATH": "/opt/unity/Editor/Unity"
      }
    }
  }
}
```

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
