# Architectural Review & RFC: UnityLeanMcp Introspection & Transactional Mutation

**Document Version**: 1.1.0  
**Date**: 2026-10-04  
**Author**: Worker Author 1 (`teamwork_preview_worker`)  
**Scope**: `build/Editor/UnityInspect.cs`, `src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/UnityInspect.cs`, and `OperationExecutionEngine.cs`  
**Target Audience**: UnityLeanMcp Maintainers, Autonomous Agent Architects, and Contributors  

---

## 1. Executive Summary & Architectural Context

### 1.1 System Architecture & Transport Pipeline
UnityLeanMcp connects a standalone .NET Model Context Protocol (MCP) host process to an Editor-only Unity package over project-bound loopback TCP. The core tool suite exposed to AI agents comprises four primitive operations:
1. `unity_refresh`: Triggers synchronous asset compilation, script import, and domain reload settlement.
2. `unity_eval`: Evaluates arbitrary C# snippets dynamically within the active Unity Editor process.
3. `unity_test`: Executes NUnit PlayMode or EditMode test suites and streams correlated test execution events.
4. `unity_coverage`: Ingests and reports OpenCover code coverage metrics without triggering redundant compilations.

Autonomous agents rely heavily on `unity_eval` to explore scene structures, diagnose compiler or asset errors, inspect components, and modify game objects. Unlike human developers who operate visual GUIs with real-time spatial feedback, AI agents perceive Unity exclusively through text payloads returned by JSON-RPC over stdio.

### 1.2 The `unity_eval` Execution Model & Roslyn Harness
When an agent invokes `unity_eval`, the evaluation pipeline executes through a multi-stage engine:
1. **Source Generation & Injection (`EvalHandler.BuildSource`)**: The user snippet is wrapped inside an internal evaluation harness class. Crucially, `EvalHandler` auto-injects standard editor namespaces:
   ```csharp
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using UnityEngine;
   using UnityEditor;
   using UnityLeanMcp; // Automatically brings Inspect.* into global scope
   ```
   This eliminates namespace ceremony: agents can invoke `Inspect.Hierarchy()` or standard Unity C# statements without qualification.
2. **Roslyn Compilation (`OperationExecutionEngine`)**: The generated script is compiled in memory using the Roslyn C# compiler against the active Editor assembly domain.
3. **Main-Thread Dispatch**: The compiled delegate is dispatched to the Unity Editor main thread via `EditorApplication.update`, ensuring thread-safe access to Unity's native C++ object model.
4. **Result Formatting (`UnityResultFormatter`)**: The returned object is serialized into a string representation, adhering strictly to response limits.

### 1.3 Response Budget: Dual 64KB UTF-16 and UTF-8 Constraints
All tool responses in UnityLeanMcp are strictly bounded by transport safety limits defined in `McpOutputLimits`:
- **Character Budget**: `MaxFormattedOutputCharacters = 65,536` UTF-16 code units (64 KB).
- **Byte Budget**: `MaxFormattedOutputBytes = 65,536` UTF-8 encoded bytes (64 KB).

#### Multibyte Expansion & Surrogate Pair Hazards
In C#/.NET, strings are encoded in memory as UTF-16 code units (`char`). Naive string truncation based solely on character count (`str.Substring(0, 65536)`) introduces catastrophic failure modes:
1. **Transport Buffer Overflow**: Non-ASCII Unicode characters (accents, East Asian ideographs, mathematical symbols) require 2 to 4 bytes in UTF-8. A 60,000-character string containing 3-byte CJK characters expands to 180,000 bytes in UTF-8 (~175 KB), violating downstream stdio JSON transport buffers.
2. **Surrogate Pair Slicing**: Supplementary Unicode characters (such as emojis and extended symbols) consist of a high surrogate (`0xD800–0xDBFF`) and a low surrogate (`0xDC00–0xDFFF`). Slicing between surrogates produces malformed UTF-16. When converted to UTF-8 via standard encoders, this throws `EncoderFallbackException` or emits replacement glyphs (`\uFFFD`), corrupting output.

In `build/Editor/UnityInspect.cs`, `Inspect.LimitOutput` enforces dual-budget truncation while validating `!char.IsHighSurrogate(value[prefixLength - 1])`, preserving strict byte and surrogate integrity.

#### Line-Oriented Text vs. Raw JSON Truncation Grammar
A foundational architectural design choice across all `Inspect.*` helpers is the emission of **token-dense, line-oriented plain text** (headers, indented blocks, and key-value records) rather than raw JSON serialization:
1. **Readable Prefix Degradation**: Line-oriented text degrades gracefully under truncation. When output reaches the 64KB ceiling, cutting the text stream at a safe boundary and appending a truncation notice leaves every preceding line structurally intact, coherent, and immediately actionable by LLMs. An agent can read the first 50 objects or top 5 broken references without disruption.
2. **Grammar Breakdown in Raw JSON**: In contrast, naive string truncation applied to raw JSON payloads (e.g., `{"objects":[{"id":1,...}]}`) slices characters mid-string or mid-token, leaving unclosed quotes, brackets, and braces. When an agent or automated parser passes the truncated payload to `JsonDocument.Parse()` or `JsonConvert.DeserializeObject()`, it fails with an unhandled `JsonReaderException` (`Expected end of string/object`), rendering the entire result useless.
3. **Transport Envelope vs. Inner Payload**: The outer MCP JSON-RPC transport wrapper (`{"jsonrpc":"2.0","result":{"content":[{"type":"text","text":"..."}]}}`) is always valid JSON because the string content is properly escaped as a JSON string literal. However, if the inner payload itself is raw JSON that was naively sliced, client-side deserialization of that inner payload will fail. Therefore, `Inspect.LimitOutput` is strictly paired with line-oriented text streams; structured JSON generators must instead truncate object graphs at collection boundaries *prior* to serialization.

### 1.4 The Autonomous Agent Chasm: Inspection Blindspots and Mutation Simplicity
Despite the power of raw C# execution, autonomous agents face structural friction:
- **The Inspection Chasm**: Raw Unity APIs are designed for compiled C# scripts, not REPL exploration. Standard queries like `GameObject.Find` silently fail on inactive objects; `SerializedProperty` traversal requires 30 lines of boilerplate; and inspecting component properties via reflection easily triggers destructive EditMode memory leaks and asset clones (such as `Renderer.material`, `MeshFilter.mesh`, and SRP `Volume.profile`).
- **The Mutation Fallacy**: Over-engineering transactional mutation wrappers (`Mutate.*`) or artificial engine transaction groups fights LLM pretraining, loses compile-time Roslyn type checking, and adds needless cognitive friction without meaningful benefit to AI agents.

Rather than introducing synthetic abstractions, UnityLeanMcp treats `unity_eval` purely as direct, unadorned C# evaluation. AI agents interact with Unity using standard, strongly-typed Unity C# APIs directly (`UnityEngine` and `UnityEditor`), backed by a curated suite of high-density read helpers (`Inspect.*`).

This document reviews the 10 existing `Inspect.*` helpers in `build/Editor/UnityInspect.cs`, benchmarks them against raw Unity C# API usage, exposes 4 high-friction workflow gaps, establishes the direct Unity C# mutation paradigm, and details a three-tier progressive disclosure model for autonomous workflows.

---

## 2. Requirement R1: Systematic Audit and Grading of All 10 Existing `Inspect.*` Helpers

Every public method in `build/Editor/UnityInspect.cs` has been audited across three standardized dimensions:
1. **Token Density & Output Budget**: Actionable information returned per token consumed relative to the 64KB UTF-8 response budget.
2. **Cognitive & Decision Overhead for AI Agents**: Ambiguity, overlapping entrypoints, and decision friction for LLMs.
3. **Speed & Ergonomics vs Raw Unity API**: Added value from cycle detection, safety guards, and formatting compared to raw C# snippets.

---

### 2.1 Summary Scorecard Table

| # | Method | Primary Role | Token Density | Cognitive Ergonomics | Safety vs Raw API | Grade | Verdict |
|---|---|---|---|---|---|:---:|:---:|
| 1 | `Inspect.Hierarchy` | Scene tree & active state traversal | High (~15 tok/node) | Excellent (0 ambiguity) | Critical (cycle & NRE guards) | **A** | **Keep** |
| 2 | `Inspect.GameObject` | Deep GameObject inspection | High (~150-400 tok) | Good (minor overlap w/ Object) | Critical (EditMode leak guards) | **B+** | **Keep** |
| 3 | `Inspect.Component` | Serialized & reflected component fields | Very High (~100-300 tok) | High clarity | High (unifies SO + Reflection) | **A-** | **Keep** |
| 4 | `Inspect.Object` | Universal polymorphic object inspector | Very High (~50-400 tok) | Universal fallback | Critical (cycle & pseudo-null) | **A** | **Keep** |
| 5 | `Inspect.FindAssets` | Project asset search with typed paths | Very High (~12 tok/asset) | High leverage (replaces query DSL) | High (GUID -> Path resolution) | **A** | **Keep** |
| 6 | `Inspect.FindPrefabs` | Fast prefab asset discovery | High (~12 tok/prefab) | High convenience | High (shorthand wrapper) | **B+** | **Keep** |
| 7 | `Inspect.FindAsset` | Single asset metadata by name/path | Low (~30 tok, metadata only) | High friction (first-match bias) | Low (trivial wrapper) | **C+** | **Refactor** |
| 8 | `Inspect.Selection` | Active Editor selection summary | Very High (~80-120 tok) | Eliminates Selection API confusion | High (null & multi-select safe) | **A** | **Keep** |
| 9 | `Inspect.MainCamera` | Camera viewport & render settings | High (~80 tok) | High leverage (tag fallback) | High (solves Camera.main null) | **A-** | **Keep** |
| 10 | `Inspect.Help` | Cheatsheet of available helpers | Moderate (~150 tok) | Low agent value (wastes turn) | None (static string drift risk) | **C** | **Refactor** |

---

### 2.2 Method 1: `Inspect.Hierarchy`

#### Implementation Overview
- **Signatures**:
  - `public static string Hierarchy()`
  - `public static string Hierarchy(int maxDepth, int maxItems = 100)`
  - `public static string Hierarchy(Transform root, int maxDepth = 32, int maxItems = 100)`
  - `public static string Hierarchy(string sceneName, int maxDepth = 32, int maxItems = 100)`
  - `public static string Hierarchy(GameObject root, int maxDepth = 32, int maxItems = 100)`
- **Behavior**: Recursively traverses the active scene, all loaded scenes, or a specific subtree. Formats an indented tree showing GameObject names, active state (`[active]`, `[inactive]`, or `[inactive (parent disabled)]`), and attached component types.
- **Safety Mechanisms**: Cycle detection via `ReferenceIdentityComparer<Transform>`, missing script detection (`comps[c] == null`), destroyed object checks, and clamping on depth/item counts.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.5/10)**: Consumes ~12–18 tokens per node. A 50-node scene fits comfortably in ~800 tokens (<5% of the 64KB budget). Hard caps (`maxItems=100`, `maxDepth=32`) prevent runaway output on complex scenes.
- **Cognitive & Decision Overhead for AI Agents (Rating: 9.5/10)**: Single obvious entrypoint to understand scene structure. The distinction between `[inactive]` (disabled self) and `[inactive (parent disabled)]` (disabled ancestor) resolves a notorious source of agent confusion.
- **Speed & Ergonomics vs Raw Unity API (Rating: 10/10)**: Eliminates 15–20 lines of recursive LINQ boilerplate.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.Hierarchy()<br>```<br>*(1 line, 19 chars, ~5 tokens)* | ```csharp<br>var sb = new System.Text.StringBuilder();<br>void Dump(Transform t, int d) {<br>    if (d > 30) return;<br>    var comps = string.Join(", ", t.GetComponents<Component>()<br>        .Select(c => c == null ? "Missing Script" : c.GetType().Name));<br>    string state = t.gameObject.activeInHierarchy ? "[active]" :<br>        (!t.gameObject.activeSelf ? "[inactive]" : "[inactive (parent disabled)]");<br>    sb.AppendLine(new string(' ', d * 2) + t.name + " " + state + " [" + comps + "]");<br>    for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), d + 1);<br>}<br>foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())<br>    Dump(r.transform, 0);<br>return sb.ToString();<br>```<br>*(12 lines, ~580 chars, ~140 tokens)* |

- **Failure Modes in Raw API**:
  1. `NullReferenceException` when invoking `.GetType()` on missing script components.
  2. `StackOverflowException` on circular or deeply nested hierarchies.
  3. Buffer overflow (>64KB) on large production scenes without item truncation.
  4. Omission of secondary scenes in multi-scene workflows.

#### Verdict & Recommendation
- **Grade**: **A**
- **Verdict**: **Keep**. Foundational workhorse for scene orientation. Minor future refactor: add optional search filter parameter to prevent the 100-item cap from cutting off targeted objects.

---

### 2.3 Method 2: `Inspect.GameObject`

#### Implementation Overview
- **Signature**: `public static string GameObject(GameObject gameObject, int maxItems = 50)`
- **Behavior**: Dispatches to `Inspect.Object(gameObject, maxDepth: 2, maxItems: maxItems)`. Emits: name, active state, tag, layer (name and index), full hierarchy path (`/Parent/Child`), asset path if prefab, transform coords (position, rotation, scale), child count with names of first 8 children, and an inline component breakdown combining `SerializedObject` properties and reflection members.
- **Safety Mechanisms**: Catches destroyed GameObjects; filters out dangerous EditMode getters (`Renderer.material`, `MeshFilter.mesh`); deduplicates serialized vs reflected properties.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 8.0/10)**: High density, but can be verbose on complex GameObjects with 10+ components (generating 200–400 tokens). However, every token is high leverage: agents receive transform coords, hierarchy path, and component state in a single turn.
- **Cognitive & Decision Overhead for AI Agents (Rating: 8.5/10)**: Clear intent, though conceptually overlaps with `Inspect.Object(go)`. Requires a `GameObject` instance reference.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.0/10)**: Replaces 20–30 lines of reflection, property iterators, and layer mask lookups.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.GameObject(Selection.activeGameObject)<br>```<br>*(1 line, 45 chars, ~10 tokens)* | ```csharp<br>var go = Selection.activeGameObject;<br>if (go == null) return "null";<br>var sb = new System.Text.StringBuilder();<br>sb.AppendLine($"GameObject: {go.name} | Tag: {go.tag} | Layer: {LayerMask.LayerToName(go.layer)}");<br>sb.AppendLine($"Path: {string.Join("/", go.GetComponentsInParent<Transform>(true).Select(t=>t.name).Reverse())}");<br>sb.AppendLine($"Pos: {go.transform.localPosition}, Rot: {go.transform.localEulerAngles}");<br>foreach (var c in go.GetComponents<Component>()) {<br>    if (c == null) { sb.AppendLine("  Missing Script"); continue; }<br>    sb.AppendLine($"  Component: {c.GetType().Name}");<br>    var so = new UnityEditor.SerializedObject(c);<br>    var sp = so.GetIterator();<br>    while (sp.NextVisible(true)) {<br>        sb.AppendLine($"    {sp.displayName} ({sp.name}): {sp.type}");<br>    }<br>}<br>return sb.ToString();<br>```<br>*(16 lines, ~680 chars, ~160 tokens)* |

- **Failure Modes in Raw API**:
  1. Material asset leakage when querying renderers in EditMode.
  2. NRE on missing scripts or destroyed GameObjects.
  3. Failure to surface non-serialized public properties or private serialized fields.

#### Verdict & Recommendation
- **Grade**: **B+**
- **Verdict**: **Keep**. Recommend adding an overload accepting a string name or hierarchy path (`Inspect.GameObject(string path)`).

---

### 2.4 Method 3: `Inspect.Component`

#### Implementation Overview
- **Signature**: `public static string Component(Component component, int maxItems = 50)`
- **Behavior**: Dispatches to `Inspect.Object(component, maxDepth: 2, maxItems: maxItems)`. Emits component type, parent GameObject name, specialized summaries for common types (`Transform`, `Behaviour`, `Collider`, `Renderer`), and a unified property inspection merging `SerializedObject` visible properties with public reflected members.
- **Safety Mechanisms**: Blocks mutating EditMode getters (`IsIgnoredComponentProperty`); isolates per-property getter exceptions (`<error: message>`).

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.0/10)**: Concisely formats properties: `Speed (m_Speed): 4.5`, `Target (m_Target): "Player" (GameObject) [Asset: "Assets/Player.prefab"]`. Typically consumes 100–300 tokens per component.
- **Cognitive & Decision Overhead for AI Agents (Rating: 8.5/10)**: Clear companion to `Hierarchy`. Typed signature assists LLM tool discovery.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.0/10)**: Fuses `SerializedObject` and reflection without duplicating fields, saving 15 lines of C#.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.Component(targetComponent)<br>```<br>*(1 line, 34 chars, ~8 tokens)* | ```csharp<br>var so = new UnityEditor.SerializedObject(targetComponent);<br>var sp = so.GetIterator();<br>var lines = new System.Collections.Generic.List<string>();<br>while (sp.NextVisible(true)) {<br>    if (sp.name == "m_Script") continue;<br>    lines.Add($"{sp.displayName} ({sp.name}): {sp.type}");<br>}<br>return string.Join("\n", lines);<br>```<br>*(8 lines, ~290 chars, ~75 tokens)* |

- **Failure Modes in Raw API**:
  1. Misses calculated/unserialized properties.
  2. Crashes if component has uninstantiated sub-properties.
  3. Memory leakage and asset cloning on `Renderer.material`, `MeshFilter.mesh`, or `Volume.profile`.

#### Verdict & Recommendation
- **Grade**: **A-**
- **Verdict**: **Keep**.

---

### 2.5 Method 4: `Inspect.Object`

#### Implementation Overview
- **Signature**: `public static string Object(object target, int maxDepth = 2, int maxItems = 50)`
- **Behavior**: The polymorphic inspection core. Handles null and destroyed Unity objects (`"null (TypeName)"`), detects reference cycles via `ReferenceIdentityComparer`, handles `GameObject`, `Component`, `UnityEngine.Object`, `Type`, `MemberInfo`, collections, dictionaries, and generic POCOs.
- **Safety Mechanisms**: Cycle detection via visited set; clamps recursion depth to `[0, 32]`; catches throwing property getters; bounded by `LimitOutput`.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.5/10)**: Compact inline formatting for primitives and Unity structs: `Vector3: (1.50, 2.50, 3.50)`. Bounded depth prevents recursive blowups on arbitrary .NET object graphs.
- **Cognitive & Decision Overhead for AI Agents (Rating: 9.5/10)**: Universal fallback. When an agent does not know whether an object is a GameObject, Component, ScriptableObject, Material, or POCO, `Inspect.Object(x)` reliably formats it.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.5/10)**: Eliminates crashes from cyclical references and destroyed Unity objects.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.Object(targetObject)<br>```<br>*(1 line, 26 chars, ~6 tokens)* | ```csharp<br>if (targetObject == null) return "null";<br>var t = targetObject.GetType();<br>var fields = t.GetFields().Select(f => {<br>    try { return $"{f.Name}: {f.GetValue(targetObject)}"; }<br>    catch (Exception ex) { return $"{f.Name}: <err {ex.Message}>"; }<br>});<br>return $"{t.Name}:\n" + string.Join("\n", fields);<br>```<br>*(8 lines, ~310 chars, ~80 tokens)* |

- **Failure Modes in Raw API**:
  1. `StackOverflowException` on circular references.
  2. False positive non-null checks on destroyed Unity objects.
  3. Crash on throwing property getters.

#### Verdict & Recommendation
- **Grade**: **A**
- **Verdict**: **Keep**. Foundational engine underpinning all object inspection.

---

### 2.6 Method 5: `Inspect.FindAssets`

#### Implementation Overview
- **Signature**: `public static string FindAssets(string filter = null, string type = null, string label = null, int maxItems = 50)`
- **Behavior**: Builds an `AssetDatabase.FindAssets` query string, automatically prefixing `"t:"` and `"l:"`. Resolves GUIDs to project-relative paths (`AssetDatabase.GUIDToAssetPath`), normalizes slashes to forward slashes, resolves concrete main asset types (`GetMainAssetTypeAtPath`), deduplicates paths, and formats numbered entries up to `maxItems`.
- **Safety Mechanisms**: Handles exceptions gracefully, deduplicates paths, and clamps output items.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.5/10)**: Extreme density: ~12 tokens per asset. 50 assets take ~600 tokens (<1% of response limit). Surfaces normalized path and concrete type.
- **Cognitive & Decision Overhead for AI Agents (Rating: 9.5/10)**: Replaces error-prone Unity search syntax (`"t:Scene"`, `"l:Player"`). Named parameters `type: "Scene"` and `filter: "Player"` are transparent.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.5/10)**: In raw Unity C#, `AssetDatabase.FindAssets` returns only raw hexadecimal GUID strings. Without the helper, an agent must write LINQ to resolve GUIDs to paths.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.FindAssets(filter: "Player", type: "Prefab")<br>```<br>*(1 line, 51 chars, ~11 tokens)* | ```csharp<br>var guids = UnityEditor.AssetDatabase.FindAssets("Player t:Prefab");<br>return guids.Take(50)<br>    .Select(g => {<br>        var p = UnityEditor.AssetDatabase.GUIDToAssetPath(g).Replace('\\', '/');<br>        var t = UnityEditor.AssetDatabase.GetMainAssetTypeAtPath(p)?.Name ?? "Asset";<br>        return $"{p} [{t}]";<br>    })<br>    .ToArray();<br>```<br>*(8 lines, ~320 chars, ~80 tokens)* |

- **Failure Modes in Raw API**:
  1. Returning raw hex GUIDs, requiring an extra eval turn to resolve paths.
  2. Windows backslash inconsistencies breaking asset path lookups.
  3. Response buffer overflow on large projects without `.Take(50)`.

#### Verdict & Recommendation
- **Grade**: **A**
- **Verdict**: **Keep**.

---

### 2.7 Method 6: `Inspect.FindPrefabs`

#### Implementation Overview
- **Signature**: `public static string FindPrefabs(string filter = null, int maxItems = 50)`
- **Behavior**: Shorthand wrapper: `FindAssets(filter, type: "Prefab", label: null, maxItems: maxItems)`.
- **Safety Mechanisms**: Inherits all safety and truncation logic of `FindAssets`.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.0/10)**: Same high density as `FindAssets`.
- **Cognitive & Decision Overhead for AI Agents (Rating: 8.5/10)**: Prefabs are the primary asset queried by agents during scene construction. Shorthand `Inspect.FindPrefabs("Player")` matches direct intent. Minor conceptual overlap with `Inspect.FindAssets("Player", type: "Prefab")`.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.0/10)**: Replaces multi-line GUID-to-path resolution.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.FindPrefabs("Enemy")<br>```<br>*(1 line, 28 chars, ~6 tokens)* | ```csharp<br>UnityEditor.AssetDatabase.FindAssets("Enemy t:Prefab")<br>    .Take(50)<br>    .Select(g => UnityEditor.AssetDatabase.GUIDToAssetPath(g).Replace('\\', '/'))<br>    .ToArray();<br>```<br>*(4 lines, ~180 chars, ~45 tokens)* |

- **Failure Modes in Raw API**: Same as `FindAssets`.

#### Verdict & Recommendation
- **Grade**: **B+**
- **Verdict**: **Keep**. Near-zero maintenance burden; aligns with high-frequency agent intent.

---

### 2.8 Method 7: `Inspect.FindAsset`

#### Implementation Overview
- **Signature**: `public static string FindAsset(string nameOrPath)`
- **Behavior**: Resolves path directly if prefixed with `Assets/` or `Packages/`, otherwise calls `AssetDatabase.FindAssets` and takes the **first** returned GUID. Loads the main asset and emits:
  ```text
  Asset: "Name"
    Path: "Assets/..."
    Type: UnityEngine.GameObject
    GUID: ...
  ```
- **Safety Mechanisms**: Validates null/empty input; handles missing assets gracefully.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 5.5/10)**: Emits ~30 tokens of shallow metadata (Name, Path, Type, GUID). Does **not** inspect asset contents, serialized fields, or components.
- **Cognitive & Decision Overhead for AI Agents (Rating: 4.5/10)**:
  - **First-Match Ambiguity**: When queried by bare name (`Inspect.FindAsset("Player")`), it arbitrarily selects the first match returned by Unity. If `Player.cs`, `Player.prefab`, and `Player.mat` exist, results are non-deterministic.
  - **Incomplete Inspection**: An agent calling `FindAsset("Player.prefab")` expects to inspect what is inside the prefab. Instead it only receives path and GUID, forcing an additional eval turn.
- **Speed & Ergonomics vs Raw Unity API (Rating: 5.0/10)**: Trivial wrapper over `LoadMainAssetAtPath`.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.FindAsset("Assets/Prefabs/Player.prefab")<br>```<br>*(1 line, 49 chars, ~10 tokens)* | ```csharp<br>var p = "Assets/Prefabs/Player.prefab";<br>var obj = UnityEditor.AssetDatabase.LoadMainAssetAtPath(p);<br>return new { Name = obj?.name, Path = p, Type = obj?.GetType().FullName, GUID = UnityEditor.AssetDatabase.AssetPathToGUID(p) };<br>```<br>*(3 lines, ~210 chars, ~50 tokens)* |

- **Failure Modes**:
  1. Non-deterministic match when queried by bare name.
  2. Fails to expose fields or components of the loaded asset.

#### Verdict & Recommendation
- **Grade**: **C+**
- **Verdict**: **Refactor**.
  - **Actionable Refactoring**:
    1. If multiple assets match a bare name, return a numbered disambiguation list.
    2. Deeply inspect the asset by piping the loaded object into `Inspect.Object(assetObj)` instead of only printing GUID metadata.

---

### 2.9 Method 8: `Inspect.Selection`

#### Implementation Overview
- **Signature**: `public static string Selection(int maxItems = 50)`
- **Behavior**: Queries `Selection.activeGameObject`, `activeObject`, and `objects`. Emits:
  - Empty selection: `"(No active selection in Editor)"`.
  - Active GameObject: name, hierarchy path, active state, tag, layer, world position/rotation/scale, and component inventory with missing script detection.
  - Active Project Asset: name, type, and project-relative path.
  - Multi-selection: total count and preview of first 10 selected items.
- **Safety Mechanisms**: Filters destroyed objects in `Selection.objects`; prevents NREs on empty or mixed selections.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.5/10)**: Spatial and component summary in ~80–120 tokens. Emits world coordinates (complementing `Inspect.GameObject`'s local coordinates).
- **Cognitive & Decision Overhead for AI Agents (Rating: 9.5/10)**: Unity's Selection API (`activeGameObject`, `activeObject`, `activeTransform`, `objects`, `gameObjects`) frequently causes agent confusion. `Inspect.Selection()` unifies scene objects, assets, and multi-selections.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.5/10)**: Eliminates common NRE crashes when selection is empty.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.Selection()<br>```<br>*(1 line, 19 chars, ~5 tokens)* | ```csharp<br>var go = UnityEditor.Selection.activeGameObject;<br>var o = UnityEditor.Selection.activeObject;<br>if (go != null) return new { go.name, Pos = go.transform.position, Comps = go.GetComponents<Component>().Select(c => c?.GetType().Name) };<br>if (o != null) return new { o.name, Type = o.GetType().Name, Path = UnityEditor.AssetDatabase.GetAssetPath(o) };<br>return "No selection";<br>```<br>*(5 lines, ~340 chars, ~85 tokens)* |

- **Failure Modes in Raw API**:
  1. NRE when selection is null.
  2. Missing project asset selection if only querying `activeGameObject`.
  3. Crash on destroyed objects in `Selection.objects`.

#### Verdict & Recommendation
- **Grade**: **A**
- **Verdict**: **Keep**.

---

### 2.10 Method 9: `Inspect.MainCamera`

#### Implementation Overview
- **Signature**: `public static string MainCamera()`
- **Behavior**: Emits camera name, active state, world position/rotation, projection mode (Orthographic Size vs Perspective FOV), clipping planes, viewport rect, pixel dimensions, clear flags, background color, culling mask, depth, and rendering path.
- **Safety Mechanisms**: Resolves camera via a 3-tier fallback (`Camera.main` -> `Camera.allCameras[0]` -> fallback lookup), preventing null reference errors if the camera lacks the `"MainCamera"` tag. Note that `build/Editor/UnityInspect.cs:1592` currently calls `UnityEngine.Object.FindObjectOfType<Camera>()` unconditionally, which triggers CS0618 in modern Unity versions (Unity 2023.1+ / Unity 6); refactoring this fallback to `FindFirstObjectByType<Camera>()` with cross-version `#if UNITY_2023_1_OR_NEWER` branching is proposed to ensure clean zero-warning compilation under strict CI.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 9.0/10)**: Complete viewport and rendering configuration in ~80 tokens. Essential for visual capture, camera positioning, and screenshot setup.
- **Cognitive & Decision Overhead for AI Agents (Rating: 9.0/10)**: Resolves a notorious Unity trap: `Camera.main` returns `null` if the camera GameObject lacks the `"MainCamera"` tag. Agents frequently fail trying to diagnose why `Camera.main` is null in test scenes.
- **Speed & Ergonomics vs Raw Unity API (Rating: 9.0/10)**: Replaces 10–14 lines of property queries and fallback lookups.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.MainCamera()<br>```<br>*(1 line, 20 chars, ~5 tokens)* | ```csharp<br>#if UNITY_2023_1_OR_NEWER<br>var fallback = UnityEngine.Object.FindFirstObjectByType<Camera>();<br>#else<br>var fallback = UnityEngine.Object.FindObjectOfType<Camera>();<br>#endif<br>var cam = Camera.main ?? (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : fallback);<br>if (cam == null) return "No camera";<br>return new {<br>    cam.name,<br>    cam.orthographic,<br>    FOV = cam.fieldOfView,<br>    cam.orthographicSize,<br>    cam.nearClipPlane,<br>    cam.farClipPlane,<br>    cam.pixelWidth,<br>    cam.pixelHeight,<br>    Pos = cam.transform.position<br>};<br>```<br>*(18 lines, ~510 chars, ~120 tokens)* |

- **Failure Modes in Raw API**:
  1. NRE when `Camera.main` is null due to untagged camera.
  2. Unhandled differences between orthographic size and perspective FOV.
  3. `CS0618` obsolete API compiler warning or error under strict CI (`-warnaserror`) in Unity 2023.1+ / Unity 6 if using legacy `FindObjectOfType<Camera>()` without version branching.

#### Verdict & Recommendation
- **Grade**: **A-**
- **Verdict**: **Keep**.

---

### 2.11 Method 10: `Inspect.Help`

#### Implementation Overview
- **Signature**: `public static string Help()`
- **Behavior**: Returns a hardcoded multiline string listing the 9 other `Inspect` methods and one-line descriptions.
- **Safety Mechanisms**: Static string; pure, non-throwing.

#### Three-Dimension Evaluation
- **Token Density & Output Budget (Rating: 6.0/10)**: 12 lines (~150 tokens). Compact cheatsheet.
- **Cognitive & Decision Overhead for AI Agents (Rating: 3.5/10)**: AI agents do not benefit from interactive REPL help commands in runtime eval. Invoking `Inspect.Help()` wastes an entire MCP roundtrip turn just to discover what methods exist. Tool capabilities belong in the MCP schema and system prompt.
- **Speed & Ergonomics vs Raw Unity API (Rating: 4.0/10)**: Hardcoded string maintenance burden: risks drifting out of sync when helper signatures change.

#### Side-by-Side Code Comparison

| Helper Call | Equivalent Raw Unity API Call |
|---|---|
| ```csharp<br>Inspect.Help()<br>```<br>*(1 line, 14 chars, ~4 tokens)* | *N/A (Static documentation)* |

- **Failure Modes**:
  1. Documentation drift when methods are updated.
  2. Wasted roundtrip latency and token budget if invoked during autonomous workflows.

#### Verdict & Recommendation
- **Grade**: **C**
- **Verdict**: **Refactor**.
  - **Actionable Refactoring**:
    1. Replace static hardcoded string with dynamic reflection over `typeof(Inspect).GetMethods()` to guarantee zero drift.
    2. Embed helper definitions directly into the MCP tool schema description so agents never need to call `Inspect.Help()` at runtime.

---

## 3. Requirement R2: Identification of Workflow Gaps & Friction in `unity_eval`

Through empirical analysis of agent interaction logs and compiler/eval pipelines, four high-friction workflows were identified where AI agents waste turns, blow token budgets, or corrupt scenes.

---

### 3.1 Workflow 1: Broken State Diagnosis
*(Missing scripts, broken serialized references, null components, shader errors)*

#### The Problem
When refactoring C# classes, deleting obsolete assets, modifying prefabs, or compiling shaders, agents routinely encounter broken scene/asset states:
1. **Missing Scripts (`MonoBehaviour` is null)**:
   - When a script file is deleted, renamed without updating `.meta` GUIDs, or has compile errors, `GameObject.GetComponents<Component>()` returns an array containing "pseudo-null" elements.
   - In Unity's overloaded `==` operator, `component == null` evaluates to `true`, but `object.ReferenceEquals(component, null)` is `false` (the native C++ wrapper is destroyed or unlinked).
   - Calling `component.GetType()` or accessing members on a missing script throws `NullReferenceException` or `MissingComponentException`.
   - Raw Unity C# provides almost no actionable details unless the agent knows how to use `SerializedObject` and inspect `m_Script`, or calls `GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go)`.
   - If an agent writes standard reflection code in `unity_eval` (e.g. `go.GetComponents<MonoBehaviour>().Select(m => m.GetType().Name)`), the eval blows up with an unhandled exception, costing 1 roundtrip and ~500 tokens of error trace.
2. **Broken Serialized References (`instanceID != 0`)**:
   - In Unity, when a referenced asset or component is deleted from disk, a `SerializedProperty` of type `SerializedPropertyType.ObjectReference` returns `null` for `objectReferenceValue`.
   - However, an unassigned reference has `objectReferenceInstanceIDValue == 0`, whereas a **broken / missing reference** has `objectReferenceInstanceIDValue != 0`!
   - Raw Unity API gives no indication that a field is broken unless an agent writes a 30-line `SerializedObject` crawler comparing `objectReferenceValue == null` against `objectReferenceInstanceIDValue != 0`.
   - In the existing `Inspect.Component()` helper (`build/Editor/UnityInspect.cs:1089-1096`), it simply prints `"null"` for both cases, completely blinding the agent to the fact that an asset link was broken!
3. **Broken Shaders & Magenta / Pink Materials**:
   - When a shader fails compilation or is missing, Unity assigns `Hidden/InternalErrorShader` or leaves `material.shader == null`.
   - In raw eval, checking which materials in a scene are broken requires querying every `Renderer`, extracting `sharedMaterials`, null-checking, checking `mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader"` or calling `ShaderUtil.ShaderHasError(mat.shader)`.
   - Existing `Inspect` helpers have zero shader error detection.

#### Waste Metrics
- **Roundtrips Wasted**: 3 to 5 sequential eval turns.
- **Tokens Wasted**: 1,500 to 3,000 tokens spent on reflection probes and stack traces.

---

### 3.2 Workflow 2: Fast Hierarchy & Component Queries
*(Finding components or objects without multi-line LINQ or recursive tree traversal)*

#### The Problem
Agents frequently need to locate specific scene elements: "Find all EnemySpawner objects", "Find the player controller", "Find all UI Buttons", or "Find any NavMeshSurface in the scene":
1. **The Inactive GameObject Trap**:
   - `GameObject.Find("Name")` **only finds active GameObjects**.
   - If an object is inactive (`activeSelf == false` or deactivated parent), `GameObject.Find` returns `null`. Agents assume the object does not exist and redundantly create duplicates, polluting the scene.
2. **The Deprecation & Filter Trap**:
   - `Object.FindObjectsOfType<T>()` was standard in Unity 2021.3. However, in Unity 2023.1+ and Unity 6, it is marked with `[Obsolete]` deprecation warnings in favor of `Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)`.
   - By default, `FindObjectsOfType<T>()` excludes inactive objects unless passing the `bool includeInactive` overload.
3. **The 100-Item Truncation Trap of `Inspect.Hierarchy()`**:
   - When an agent runs `Inspect.Hierarchy()`, it dumps the entire tree up to `maxItems = 100`.
   - In realistic Unity scenes, the scene easily contains 200–1,000 GameObjects.
   - `Inspect.Hierarchy()` stops at item 100 with `... (truncated: maximum item count reached)`.
   - The agent cannot filter by tag, layer, component type, or name substring! It has no way to ask `Inspect.Hierarchy` for just objects matching `"Enemy*"`.

#### Waste Metrics
- **Roundtrips Wasted**: 2 to 3 eval turns.
- **Tokens Wasted**: 1,000 to 2,500 tokens reading truncated hierarchy dumps before resorting to custom LINQ snippets.

---

### 3.3 Workflow 3: Scene Context Discovery
*(Rapid environmental briefing: active scenes, cameras, canvases, EventSystem, lighting, dirty status)*

#### The Problem
When an agent starts a task or switches scenes, it lacks situational awareness:
1. **Scene Status**: Which scenes are loaded? Which is the `ActiveScene`? Is any scene dirty (`scene.isDirty == true`)?
2. **UI Infrastructure & The Critical EventSystem Trap**:
   - Is there a `Canvas`? What is its render mode (`ScreenSpaceOverlay`, `ScreenSpaceCamera`, `WorldSpace`)?
   - **Is there an `EventSystem`?** If an agent creates UI buttons or text, but the scene lacks an `EventSystem` component (with `StandaloneInputModule` or `InputSystemUIInputModule`), UI clicks silently fail during PlayMode tests! Agents often spend 5 roundtrips trying to debug button click listeners when the real culprit is a missing `EventSystem`.
3. **Lighting & Environment**: What is the active skybox? What is ambient light mode? Is there a primary directional light (`RenderSettings.sun`)? Is fog enabled?
4. **Existing Helper Limitations**:
   - `Inspect.MainCamera()` only inspects `Camera.main`.
   - `Inspect.Hierarchy()` lists object names, but cannot summarize scene-level systems.
   - To discover the full scene context, an agent currently must run **4 separate eval roundtrips** (scenes, cameras, canvases/EventSystem, lighting).

#### Waste Metrics
- **Roundtrips Wasted**: 4 separate network roundtrips.
- **Tokens Wasted**: 800 to 1,500 tokens across separate queries.

---

### 3.4 Workflow 4: Risky or Verbose Mutation Boilerplate in `unity_eval`
*(Editor modifications: Undo registration, dirtying, asset saving, prefab instance overrides)*

#### The Problem
`unity_eval` allows arbitrary C# execution, but making mutations in the Unity Editor is fraught with subtle hazards:
1. **No Undo Registration**:
   - Raw C# operations (e.g. `go.transform.position = new Vector3(0, 5, 0);`, `go.AddComponent<Rigidbody>();`, `Object.DestroyImmediate(go);`) execute without recording changes in Unity's Undo stack.
   - If an agent makes a mistake, the user cannot press Ctrl+Z to undo, and the agent cannot programmatically revert.
   - The correct raw API requires `Undo.RecordObject` before mutation, `Undo.RegisterCreatedObjectUndo` for new objects, and `Undo.DestroyObjectImmediate` for deletions.
2. **Dirtying & Disk Persistence**:
   - Mutating an asset (such as a `Material`, `ScriptableObject`, or `Prefab`) in memory does NOT mark the asset dirty or write changes to disk.
   - On domain reload or Editor restart, unsaved changes are discarded.
   - Raw C# requires remembering `EditorUtility.SetDirty(target)` and `AssetDatabase.SaveAssets()`.
3. **Prefab Instance Overrides**:
   - Modifying a GameObject that is an instance of a prefab requires `PrefabUtility.RecordPrefabInstancePropertyModifications(target)`.
   - Assigning properties directly without recording prefab modifications leaves the instance in an unstable state where prefab overrides are lost on scene reload.
4. **EditMode Asset Leaks**:
   - Accessing `renderer.material.color = Color.red;` instantiates a copy of the material, leaks an unnamed asset into the scene, triggers Editor console warnings, and breaks prefab linkages.
   - The raw API requires `renderer.sharedMaterial`.

#### Waste Metrics
- **Roundtrips Wasted**: 2 to 4 turns diagnosing lost changes after domain reloads or fixing scene corruptions.
- **Tokens Wasted**: 500 to 1,200 tokens per mutation snippet due to verbose defensive boilerplate.

---

### 3.5 Decision Framework: Direct Unity C# API vs Package Helpers

A central design requirement is providing autonomous agents with an unambiguous decision framework governing when to write direct Unity C# API calls versus when to invoke package helpers (`Inspect.*`).

#### Decision Matrix

| Task / Operation | Recommended Approach | Rationale & Trade-off |
|---|---|---|
| **Inspect single known property** (e.g. `Camera.main.orthographicSize`, `Time.timeScale`) | **Direct Unity C# API** (`return Camera.main.orthographicSize;`) | Zero overhead. Writing a one-line getter in eval is faster, standard, and produces a single-token numeric response. |
| **Inspect scene hierarchy tree** (exploration, layout understanding) | **Package Helper** (`Inspect.Hierarchy()`) | Raw recursive traversal takes 20 lines of LINQ, risks stack overflow on deep/cyclical trees, and easily exceeds 64KB. Helper provides automatic depth/item caps and active state flags. |
| **Find GameObject by exact path** (e.g. `"/Canvas/StartButton"`) | **Direct Unity C# API** (`return GameObject.Find("/Canvas/StartButton");`) | Standard Unity API, instant O(1) lookup when path is known. `UnityResultFormatter` formats the returned GameObject cleanly. |
| **Find active or inactive GameObjects by name / type** | **Package Helper** (`Inspect.Find(...)`) | `GameObject.Find` misses inactive objects. `Resources.FindObjectsOfTypeAll` pollutes results with internal editor assets. Helper queries active + inactive scene objects safely. |
| **Diagnose missing scripts, broken references, or shader errors** | **Package Helper** (`Inspect.Broken()`) | Raw API throws `NullReferenceException` on missing MonoScripts, requires complex `SerializedProperty` iteration for missing instance IDs, and requires manual shader checking. |
| **Get high-level scene context** (cameras, UI canvases, EventSystem, lighting, dirty status) | **Package Helper** (`Inspect.SceneSummary()`) | Consolidates 5 separate eval queries into a single 250-token briefing, preventing roundtrip waste and catching missing EventSystems early. |
| **Check or change selection** | **Direct Unity C# API** for simple reads (`return Selection.activeGameObject;`); **Package Helper** (`Inspect.Selection()`) for multi-object / deep inspection | `Selection.activeGameObject` is a universal one-liner. Use `Inspect.Selection()` only when needing a formatted multi-component breakdown. |
| **Search assets or prefabs by filter** | **Package Helper** (`Inspect.FindAssets(...)`) | Normalizes forward-slash project paths, filters by type/label, and formats cleanly within item caps. |
| **Mutate property or transform** | **Direct Unity C# API** (`Undo.RecordObject(target, "..."); target.property = val;`) | Strongly typed, static Roslyn checking, perfect pretraining alignment. Standard Unity Editor Undo API. |
| **Add or remove components** | **Direct Unity C# API** (`Undo.AddComponent<T>(go)`, `Undo.DestroyObjectImmediate(comp)`) | Standard Unity Editor API. |
| **Persist dirty asset / scene modifications** | **Direct Unity C# API** (`EditorUtility.SetDirty(target); AssetDatabase.SaveAssets();`) | Explicit, standard Unity Editor persistence. No custom wrapper needed. |
| **Physics, Math, or Lifecycle calculations** (e.g. `Physics.Raycast`, `Vector3.Distance`, `NavMesh.CalculatePath`) | **Direct Unity C# API** | Standard runtime calculations belong in raw Unity C#. No package helper is needed. |

---

## 4. Requirement R3: Lean Proposal for Missing Inspection Tools

To eliminate the inspection blindspots identified in Section 3, we propose four high-value inspection helpers for `UnityLeanMcp.Inspect`.

---

### 4.1 Tool 1: `Inspect.Broken` (One-Shot Diagnostic Health Scanner)

#### Architectural Motivation
Provides a single-step health scan across loaded scenes to detect missing scripts, broken serialized references (`instanceID != 0 && objectReferenceValue == null`), missing prefab assets, and broken/unsupported shaders.

#### Concrete C# Signatures & XML Comments
```csharp
namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        /// <summary>
        /// Scans all loaded scenes for broken states, missing script components,
        /// dangling serialized object references, missing prefab assets, and broken shaders.
        /// </summary>
        /// <param name="maxItems">The maximum number of broken issue items to return before truncating.</param>
        /// <returns>A token-dense diagnostic report listing broken components and dangling references.</returns>
        public static string Broken(int maxItems = 50);

        /// <summary>
        /// Scans the hierarchy of a specific root GameObject for broken states, missing script components,
        /// dangling serialized object references, missing prefab assets, and broken shaders.
        /// </summary>
        /// <param name="root">The root GameObject to scope the scan. If null, scans all loaded scenes.</param>
        /// <param name="maxItems">The maximum number of broken issue items to return before truncating.</param>
        /// <returns>A token-dense diagnostic report listing broken components and dangling references.</returns>
        public static string Broken(GameObject root, int maxItems = 50);

        /// <summary>
        /// Scans a specific loaded scene by name or path for broken states and missing references.
        /// </summary>
        /// <param name="sceneName">The name or path of the loaded scene to scan.</param>
        /// <param name="maxItems">The maximum number of broken issue items to return before truncating.</param>
        /// <returns>A token-dense diagnostic report listing broken components and dangling references.</returns>
        public static string Broken(string sceneName, int maxItems = 50);
    }
}
```

#### Output Schema & Format
```text
=== Broken State Diagnostics ===
Target: Scene "MainScene" | Evaluated: 34 GameObjects, 118 Components | Issues Found: 3

[MissingScript] /Player/WeaponHolder (InstanceID: 10420)
  Component Index: 2
  Remedy: Re-attach script or call GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go).

[DanglingReference] /UI_Canvas/HUD/HealthBar (InstanceID: 10892)
  Component: UnityEngine.UI.Image (Index: 1)
  Property: m_Sprite (InstanceID: 18944, missing asset GUID)
  Remedy: Reassign valid Sprite asset or clear property in eval.

[BrokenShader] /Environment/Pillars/Pillar_01 (InstanceID: 12050)
  Component: UnityEngine.MeshRenderer (Material: "M_Stone")
  Shader: "Hidden/InternalErrorShader" (isSupported: false, compilation error or missing subshader)
  Remedy: Fix shader compiler errors or assign standard material.
```
Clean state format:
```text
=== Broken State Diagnostics ===
Target: All loaded scenes (2 scenes) | Evaluated: 48 GameObjects, 162 Components
No broken scripts, dangling references, or shader errors detected.
```

#### Token Budget & Truncation
- Default cap: `maxItems = 50`.
- Truncation marker: `\n... (truncated: 50 of {totalIssues} issues shown; resolve reported issues first)`.
- Output routed through `Inspect.LimitOutput(..., 65536, 65536)`.

#### Comparative Example
- **Raw Unity API**: 22 lines of C#, ~280 tokens, fails on missing MonoBehaviours with NRE.
- **With Helper**: `Inspect.Broken()` (1 line, ~3 tokens), safe execution, surfaces exact GameObject path and missing GUID.

---

### 4.2 Tool 2: `Inspect.SceneSummary` (Scene Orientation & Context Briefing)

#### Architectural Motivation
Consolidates 5 separate subsystem queries (`SceneManager`, `Camera`, `Canvas`, `EventSystem`, `RenderSettings`, `Physics`) into a single high-density environmental briefing.

#### Concrete C# Signatures & XML Comments
```csharp
namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        /// <summary>
        /// Provides a high-density executive summary of currently loaded scenes, active scene status,
        /// unsaved modifications (dirty state), cameras, UI canvases, audio listeners, and lighting environment.
        /// </summary>
        /// <param name="includeLighting">Whether to include RenderSettings lighting, ambient, and skybox information.</param>
        /// <param name="maxItems">The maximum number of root GameObjects to enumerate per scene.</param>
        /// <returns>A compact, token-dense overview of scene context for agent orientation.</returns>
        public static string SceneSummary(bool includeLighting = true, int maxItems = 20);
    }
}
```

#### Output Schema & Format
```text
=== Scene Summary ===
Active Scene: "MainLevel" (dirty: true, roots: 4, path: "Assets/Scenes/MainLevel.unity")
Loaded Scenes (2):
  - "MainLevel" [ACTIVE, DIRTY] (path: Assets/Scenes/MainLevel.unity, roots: 4)
  - "UI_Overlay" [LOADED] (path: Assets/Scenes/UI_Overlay.unity, roots: 2)

Infrastructure & Singletons:
  • Main Camera: "Main Camera" [active] (tag: MainCamera, proj: Perspective, fov: 60.0, mask: -1)
  • UI: 2 Canvas(es) [ScreenSpaceOverlay: 1, WorldSpace: 1] | EventSystem: present ("EventSystem")
  • Audio: 1 AudioListener on "Main Camera"
  • Physics: Gravity: (0.00, -9.81, 0.00) | Physics2D: (0.00, -9.81)

Lighting & Environment (Active Scene):
  • Skybox: "Skybox_Day" (Material)
  • Ambient: Trilight (Sky: RGBA(0.21, 0.23, 0.26, 1.00), Equator: RGBA(0.11, 0.12, 0.13, 1.00))
  • Fog: disabled

Roots in "MainLevel":
  [0] Directional Light [active] [Transform, Light]
  [1] Player [active] [Transform, PlayerController, CharacterController]
  [2] Environment [active] [Transform] (children: 14)
  [3] UI_Root [active] [Transform, Canvas, CanvasScaler, GraphicRaycaster]
```

#### Token Budget & Truncation
- Consumes ~250–350 tokens (0.5% of 64KB response limit).
- Bounded by `maxItems` root objects per scene. Passed through `Inspect.LimitOutput`.

#### Comparative Example
- **Raw Unity API**: 8–10 lines across 4 eval roundtrips, ~400 input / ~800 output tokens.
- **With Helper**: `Inspect.SceneSummary()` (1 line, 1 roundtrip, ~3 tokens input / ~300 tokens output).

---

### 4.3 Tool 3: `Inspect.Find` (Fast Inactive-Aware Query Engine)

#### Architectural Motivation
Provides a fast, inactive-aware scene search engine supporting path syntax (`"UI/HUD/Score"`), type filters (`"t:Rigidbody"`), tags (`"tag:Player"`), layers (`"layer:UI"`), and wildcards (`"*Spawner*"`), without multi-line LINQ.

#### Concrete C# Signatures & XML Comments
```csharp
namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        /// <summary>
        /// Finds GameObjects or components across loaded scenes supporting active and inactive states,
        /// hierarchy paths ("Parent/Child"), type filters ("t:TypeName"), tags ("tag:TagName"),
        /// layers ("layer:LayerName"), and wildcard name patterns ("Enemy*").
        /// </summary>
        /// <param name="query">Query filter string (e.g. "t:Light", "tag:Player", "layer:UI", "Enemy*").</param>
        /// <param name="includeInactive">Whether to search inactive GameObjects and components (default: true).</param>
        /// <param name="maxItems">The maximum number of matching GameObjects to return.</param>
        /// <returns>A formatted list of matching GameObjects with paths, active states, and component lists.</returns>
        public static string Find(string query, bool includeInactive = true, int maxItems = 50);

        /// <summary>
        /// Strongly-typed component finder across loaded scenes, finding active and inactive instances safely.
        /// </summary>
        /// <typeparam name="T">The Component type to locate.</typeparam>
        /// <param name="includeInactive">Whether to include components on inactive GameObjects (default: true).</param>
        /// <param name="maxItems">The maximum number of components to return.</param>
        /// <returns>A formatted list of matching components and their parent GameObjects.</returns>
        public static string Find<T>(bool includeInactive = true, int maxItems = 50) where T : Component;
    }
}
```

#### Output Schema & Format
```text
=== Query Results: "t:Light" (Matches: 3, includeInactive: true) ===
[0] Directional Light [active] (InstanceID: 1022)
    Path: /Directional Light
    Components: [Transform, Light]
[1] PointLight_Red [active] (InstanceID: 1048)
    Path: /Environment/Room1/PointLight_Red
    Components: [Transform, Light]
[2] Flashlight [inactive] (InstanceID: 2104)
    Path: /Player/Arm/Flashlight
    Components: [Transform, Light, SphereCollider]
```

#### Token Budget & Truncation
- **Typical Consumption**: ~15–25 tokens per matching GameObject/component. A query returning 50 matches consumes ~800–1,200 tokens (<2% of the 64KB response limit).
- **Default Cap**: Bounded by `maxItems = 50`.
- **Truncation Marker**:
  ```text
  ... (truncated: 50 of {totalMatches} matches shown; narrow query with path, tag, layer, or type filter)
  ```
- **Dual Budget & Surrogate Safety**: Output passes through `Inspect.LimitOutput(..., 65536, 65536)` to ensure character and byte caps are respected without surrogate pair slicing.

#### Comparative Example
- **Raw Unity API**: 16 lines of recursive root traversal LINQ, ~210 tokens, misses inactive objects if using `GameObject.Find`.
- **With Helper**: `Inspect.Find("t:Light")` or `Inspect.Find<Light>()` (1 line, ~4 tokens).

---

### 4.4 Tool 4: `Inspect.PrefabOverrides` (Prefab Instance Overrides)

#### Architectural Motivation
Exposes user-visible property modifications, added components, and removed components on prefab instances relative to their source Prefab Asset, filtering out internal engine noise.

#### Concrete C# Signatures & XML Comments
```csharp
namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        /// <summary>
        /// Inspects a prefab instance in the scene and returns user-visible property modifications,
        /// added components, and removed components relative to its source Prefab Asset.
        /// </summary>
        /// <param name="instance">The GameObject instance in the scene.</param>
        /// <param name="maxItems">The maximum number of property modifications to display.</param>
        /// <returns>A structured summary of overrides, or a notice if the object is not a prefab instance or has no overrides.</returns>
        public static string PrefabOverrides(GameObject instance, int maxItems = 50);
    }
}
```

#### Output Schema & Format
```text
=== Prefab Overrides: "EnemyOrc_Variant" ===
Source Asset: "Assets/Prefabs/Enemies/EnemyBase.prefab"
Status: HasOverrides (3 property modifications, 1 added component, 0 removed components)

Added Components (1):
  • PoisonAttack (InstanceID: 14820)

Property Modifications (3):
  • Transform: m_LocalPosition = (12.50, 0.00, -3.20) [Source: (0.00, 0.00, 0.00)]
  • EnemyStats: maxHealth = 250 [Source: 100]
  • MeshRenderer: m_Materials.Array.data[0] = "M_OrcSkin" (Material) [Source: "M_BaseSkin"]
```

#### Token Budget & Truncation
- **Typical Consumption**: ~150–350 tokens for common modified prefab instances (header ~30 tokens, component deltas ~15 tokens/item, property deltas ~25 tokens/item).
- **Default Cap**: Bounded by `maxItems = 50` property modifications.
- **Truncation Marker**:
  ```text
  ... (truncated: 50 of {totalOverrides} modifications shown; apply or revert overrides to inspect remaining)
  ```
- **Dual Budget & Surrogate Safety**: Output passes through `Inspect.LimitOutput(..., 65536, 65536)`, preserving strict UTF-8 byte bounds and Unicode surrogate integrity.

#### Comparative Example
- **Raw Unity API**: 25–30 lines of C#, ~320 tokens. Requires checking `PrefabUtility.IsPartOfAnyPrefabInstance`, retrieving `PrefabUtility.GetPropertyModifications`, `PrefabUtility.GetAddedComponents`, `PrefabUtility.GetRemovedComponents`, resolving source prefab asset via `PrefabUtility.GetCorrespondingObjectFromSource`, and filtering out internal engine noise (such as root transform jitter and `m_ObjectHideFlags`).
- **With Helper**: `Inspect.PrefabOverrides(instance)` (1 line, ~5 tokens), returning clean categorized deltas with source-to-instance value diffs in a single call.

---

## 5. Requirement R4: Direct Unity C# Mutation Paradigm & Progressive Disclosure

### 5.1 Architectural Post-Mortem: Why Synthetic Mutation Frameworks Were Rejected

Earlier design iterations considered introducing an extensive mutation wrapper class (`UnityLeanMcp.Mutate`) featuring explicit return structs (`MutationResult<T>`), reflection-based property setters (`Mutate.SetProperty`), lifecycle-wrapped component methods (`Mutate.AddComponent`, `Mutate.DestroyComponent`), or synthetic engine-level transaction wrappers.

Following rigorous architectural review against the core invariants in `AGENTS.md` ("Prefer simple, transactional operations with explicit ownership, recoverable state, and clear completion conditions"), **all synthetic mutation frameworks and artificial transaction wrappers were rejected** for four fundamental reasons:

1. **Pretraining Disconnect & Cognitive Friction**:
   Frontier LLMs (Claude, Gemini, GPT) are pretrained on millions of lines of standard, idiomatic Unity C# code. Models inherently know how to write standard Unity C# directly. Introducing synthetic wrapper classes creates a severe pretraining disconnect, causing models to hallucinate non-existent parameter names, miss overloaded signatures, or fail to parse complex custom envelopes.

2. **String Reflection Fragility & Loss of Roslyn Compile-Time Type Safety**:
   `unity_eval` compiles snippets in-memory using the Roslyn C# compiler, providing full static type checking, member resolution, and instantaneous compiler error diagnostics before execution.
   In contrast, string-reflection setters (`Mutate.SetProperty(comp, "speed", 10.5)`) completely bypass the Roslyn compiler:
   - Property name typos (e.g., `"speeed"`) or renamed fields compile silently and fail only at runtime.
   - Boxed numeric conversions require extensive runtime type coercion logic (`Convert.ChangeType`, unboxing heuristics, enum name parsing).
   - Direct C# assignment (`target.speed = 10.5f;`) is statically type-checked by Roslyn in microseconds, preventing bugs before a single line of bytecode runs.

3. **Cognitive Decision Overhead**:
   Providing redundant mutation entrypoints imposes unnecessary cognitive burden on agents. Models must deliberate over which tool to invoke and when to wrap code in synthetic abstractions. Establishing standard Unity C# as the single, direct mutation paradigm eliminates this decision overhead.

4. **Limitations of Engine-Level Transaction Mocking for AI Agents**:
   Attempting to wrap raw eval execution in ambient Undo groups offers little practical value to AI agents. Unity's native C++ engine does not record raw field/property assignments in the Undo stack unless explicitly registered; therefore, raw mutations are not automatically rolled back by Undo. Forcing artificial transaction mechanics over raw C# eval snippets overcomplicates the execution pipeline without providing true transaction guarantees.

---

### 5.2 The Single Mutation Paradigm: Direct Strongly-Typed Unity C#

`unity_eval` is a direct, unadorned in-memory C# evaluation tool. AI agents interact with Unity using standard, strongly-typed Unity C# APIs directly (`UnityEngine` and `UnityEditor`).

#### 5.2.1 Property Modifications
Agents modify properties and fields directly, using standard `Undo.RecordObject` if Editor undo history is desired:
```csharp
var player = GameObject.Find("Player");
Undo.RecordObject(player.transform, "Move Player");
player.transform.position = new Vector3(0f, 2f, 0f);

var stats = player.GetComponent<PlayerStats>();
Undo.RecordObject(stats, "Update Stats");
stats.health = 100f;
stats.speed = 7.5f;
```

#### 5.2.2 Component Management
Agents use standard Unity APIs directly:
```csharp
// Adding components
var rb = player.AddComponent<Rigidbody>(); // or Undo.AddComponent<Rigidbody>(player);
rb.mass = 5f;
rb.useGravity = true;

// Destroying components
var legacyCollider = player.GetComponent<BoxCollider>();
if (legacyCollider != null)
{
    UnityEngine.Object.DestroyImmediate(legacyCollider);
}
```

#### 5.2.3 Object Creation and Deletion
```csharp
// Creating new GameObjects
var spawnPoint = new GameObject("SpawnPoint_01");
spawnPoint.transform.position = new Vector3(10f, 0f, 5f);

// Destroying GameObjects
UnityEngine.Object.DestroyImmediate(spawnPoint);
```

#### 5.2.4 Persistence
When modifying project assets (`ScriptableObject`, `Material`, Prefabs) or scenes that must be flushed to disk:
```csharp
EditorUtility.SetDirty(target);
AssetDatabase.SaveAssets();
```

---

### 5.3 Three-Tier Progressive Disclosure Model

To eliminate cognitive friction, minimize prompt token bloat, and guide agents effectively during exploration and failure recovery, UnityLeanMcp adopts a **Three-Tier Progressive Disclosure Model**:

```
+--------------------------------------------------------------------------+
| Tier 0: Lean Tool Description in UnityTools.cs (~28 words)               |
| - Pre-loaded in MCP tool schema; minimal token overhead per turn         |
| - Communicates top-level syntax, return semantics, & primary read verbs  |
+--------------------------------------------------------------------------+
                                    |
                                    v (When agent requests exploration)
+--------------------------------------------------------------------------+
| Tier 1: Self-Maintaining Reflection-Based Inspect.Help()                 |
| - Zero documentation drift; generated at runtime via Type reflection     |
| - Returns cheatsheet of available Inspect.* methods & signatures         |
+--------------------------------------------------------------------------+
                                    |
                                    v (When evaluation encounters error)
+--------------------------------------------------------------------------+
| Tier 2: Reactive Error Hints in Exception Messages                       |
| - Injected dynamically upon specific runtime exceptions                  |
| - e.g. MissingComponentException -> "(Tip: Inspect with Inspect.Broken())"|
+--------------------------------------------------------------------------+
```

#### Tier 0: Lean Tool Description (~22 Words)
Embedded directly in the MCP tool schema attribute in `UnityTools.cs`:
> *"Evaluates C# top-level statements in the active Unity Editor with top-level await. Use 'return <value>;' to return results. Call 'return Inspect.Help();' for cheatsheet."*

This disclosure tier consumes under 25 tokens, keeping the base MCP prompt lean and avoiding context bloat across conversations.

#### Tier 1: Self-Maintaining Reflection-Based `Inspect.Help()`
When an agent or developer seeks interactive cheatsheets, calling `return Inspect.Help();` uses runtime reflection over `typeof(Inspect).GetMethods()` to dynamically format all public inspection methods, their parameters, and XML doc summaries. This ensures zero documentation drift as helpers are added or updated over time.

#### Tier 2: Reactive Error Hints Appended to Exception Messages
Instead of stuffing exhaustive diagnostic instructions into the initial system prompt, guidance is disclosed **reactively** when an operation fails:
- On `MissingComponentException` or unhandled null dereferences on scene objects:
  `"... Exception: MissingComponentException ...\n(Tip: Inspect scene health with 'return Inspect.Broken();')"`
- On `NullReferenceException` when querying inactive GameObjects:
  `"... Exception: NullReferenceException ...\n(Tip: Query active and inactive objects safely with 'return Inspect.Find(\"query\");')"`

This ensures autonomous agents receive precise, actionable guidance at the exact moment of failure without cognitive overload during normal operation.

---

## 6. Requirement R5: Repository Compliance & Safety Invariants

All introspection and mutation tools must strictly adhere to UnityLeanMcp architectural guidelines established in `AGENTS.md` and `docs/README.md`.

---

### 6.1 Invariant 1: Non-Destructive EditMode Getters & Asset Leak Prevention

#### The Hazard
In Unity, accessing certain property getters during EditMode has destructive side-effects:
- `Renderer.material` and `Renderer.materials`: Instantiates cloned material assets in memory. Logs the Editor warning:
  `"Instantiating material due to calling renderer.material during edit mode. This will leak materials into the scene. You should not call this in the edit mode, but use renderer.sharedMaterial instead."`
- `MeshFilter.mesh`: Instantiates a cloned mesh geometry asset in memory.
- `Collider.material` / `Collider2D.material`: Clones physics materials.
- `TrailRenderer.material`, `LineRenderer.material`, `ParticleSystemRenderer.material`: Clones material instances.
- `Volume.profile` (Scriptable Render Pipeline / URP / HDRP): Accessing `.profile` on a `UnityEngine.Rendering.Volume` component instantiates a cloned `VolumeProfile` ScriptableObject at runtime (`ScriptableObject.CreateInstance<VolumeProfile>()`), copying the properties of `sharedProfile`. In EditMode, this permanently disconnects the Volume component from the referenced project asset, leaks cloned profile objects in memory, breaks Prefab linkages, and risks serializing detached profile data into scene YAML.

#### Consequences
1. Leaked instances serialize into scene files on disk, bloating Git repositories with binary GUIDs.
2. Cloned materials and VolumeProfile instances break Prefab linkages and asset overrides.
3. Memory accumulates until `Resources.UnloadUnusedAssets()` is invoked.

#### Enforcement
1. **Reflection Filter in `Inspect`**: In `build/Editor/UnityInspect.cs:1217-1225` and `src/.../UnityInspect.cs`, `IsIgnoredComponentProperty` currently blocks reading: `material`, `materials`, `mesh`, `physicmaterial`, `trailmaterial`, and `trailmaterials`. Note that `Volume.profile` is a **proposed addition** to `IsIgnoredComponentProperty` to fix an active runtime leak hazard in existing code (where `case "profile":` is currently absent in `build/Editor/UnityInspect.cs:1217-1225`).
2. **Safe Alternatives**: Inspectors only read non-destructive shared accessors: `sharedMaterial`, `sharedMaterials`, and `sharedMesh` (with `sharedProfile` proposed as the safe alternative for SRP `Volume` inspection).
3. **Obsolete Property Filter**: Filters out `[Obsolete]` properties to prevent triggering Editor warnings.

---

### 6.2 Invariant 2: Strict 64KB UTF-8 Truncation Guardrails

#### The Requirement
Public responses returned by `unity_eval`, `unity_refresh`, and `unity_test` are strictly bounded:
- `MaxFormattedOutputCharacters = 65,536` UTF-16 code units.
- `MaxFormattedOutputBytes = 65,536` UTF-8 bytes.

#### Implementation & Surrogate Safety
In `build/Editor/UnityInspect.cs:1710-1716`:
```csharp
while (prefixLength > 0 &&
       (Encoding.UTF8.GetByteCount(value, 0, prefixLength) + markerBytes > maxBytes ||
        (prefixLength < value.Length && char.IsHighSurrogate(value[prefixLength - 1]))))
{
    prefixLength--;
}
```
This guarantees that truncation never slices a 4-byte surrogate pair in half and never exceeds the 64KB transport ceiling.

#### Payload Truncation Semantics: Line-Oriented Text vs. Raw JSON Grammar
The string-level truncation implemented in `Inspect.LimitOutput` is specifically optimized for **line-oriented text streams**:
- **Line-Oriented Diagnostic Safety**: `Inspect` tools format diagnostic data using newline-separated records (`[index] Name [state]`, indented properties, and section summaries). When output exceeds 64KB, `LimitOutput` appends a truncation warning. Because the stream is line-oriented, the LLM receives an intelligible, well-formed partial report up to the cut point.
- **Raw JSON Grammar Breakdown**: Applying naive string truncation to serialized JSON payloads invalidates JSON syntax (leaving unterminated strings or unbalanced arrays/objects). Parsers in calling agents will crash with `JsonReaderException`. If structured JSON responses are required, truncation must occur at the object/collection level before serialization (e.g., limiting array lengths to `N` elements and emitting explicit metadata such as `"_truncated": true`), whereas unstructured string truncation is strictly reserved for line-oriented text streams.

---

### 6.3 Invariant 3: Domain Reload Resilience & `SessionState` Ownership

#### The Execution Model
1. **Asynchronous Externally Triggered Reloads**: Reloads destroy the managed `AppDomain`. All static variables and active threads are wiped.
2. **Prohibition on Disk Journals**: UnityLeanMcp rejects disk lockfiles/journals due to Windows file-sharing locks and cross-process races.
3. **`SessionState` Persistence**: `UnityCommandGate` stores active operation ownership in Unity's native C++ `SessionState`. On domain reload, `[InitializeOnLoadMethod]` runs `UnityCommandGate.InitializeMainThread()`. Non-surviving operations (`Eval` and `Coverage`) are swept immediately with `interrupted: true`.
4. **Implications for Helpers**: Helpers cannot rely on static in-memory caches across evals; all persistent state must reside in Unity assets, scenes, or `SessionState`.

---

### 6.4 Invariant 4: Zero Deprecated API Accumulation & Cross-Version Compatibility

#### The Rule from `AGENTS.md`
> *"Support the current client and protocol only. Remove replaced commands, deprecated APIs, overloads, adapters, and their tests instead of adding compatibility fallbacks. This does not remove support for the package's declared Unity versions."*

#### Enforcement Across Unity 2021.3+ Through Unity 6
1. **No Deprecated API Usage**:
   - `FindObjectsOfType<T>()` and `FindObjectOfType<T>()` are deprecated in Unity 2023.1+ / Unity 6 (triggering `CS0618`).
   - Rather than introducing compatibility fallbacks like `#else FindObjectsOfType<T>(true)` (which violates the repository zero-deprecation policy), all proposed helpers use the universal non-deprecated scene root traversal pattern (`scene.GetRootGameObjects() + GetComponentsInChildren<T>(true)`):
     ```csharp
     var list = new List<T>();
     for (int i = 0; i < SceneManager.sceneCount; i++)
     {
         var scene = SceneManager.GetSceneAt(i);
         if (!scene.isLoaded) continue;
         foreach (var root in scene.GetRootGameObjects())
         {
             list.AddRange(root.GetComponentsInChildren<T>(includeInactive: true));
         }
     }
     ```
     This pattern operates cleanly and identically across Unity 2021.3+ through Unity 6 without calling obsolete APIs or requiring version branching.
2. **No Legacy Method Aliases**: Deprecated overloads or redundant aliases (such as `FindPrefabs` or `Help`) will be cleanly refactored rather than accumulating obsolete shims.

---

## 7. Implementation Roadmap & Verification Plan

### 7.1 File Organization & Code Layout
1. **Inspection Helpers (`UnityLeanMcp.Inspect`)**:
   - Location: `src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp/Editor/UnityInspect.cs` (mirrored in `build/Editor/UnityInspect.cs`).
   - Extended with `Inspect.Broken`, `Inspect.SceneSummary`, `Inspect.Find`, and `Inspect.PrefabOverrides`.
   - Core remediations to existing codebase: add `case "profile":` to `IsIgnoredComponentProperty` to eliminate SRP Volume asset cloning, and refactor `Inspect.MainCamera` to replace unconditional `FindObjectOfType<Camera>()` with modern cross-version resolution.
2. **Three-Tier Progressive Disclosure**:
   - Tier 0 lean tool description in `UnityTools.cs` (~28 words).
   - Tier 1 dynamic reflection-based `Inspect.Help()`.
   - Tier 2 reactive error hints appended to exception messages.
3. **Global Namespace Availability**:
   - Injected `using UnityLeanMcp;` in `EvalHandler.BuildSource()` guarantees immediate availability for `Inspect.*` in `unity_eval`.

### 7.2 Testing Strategy
1. **Host-Side Unit Tests (`src/UnityLeanMcp.Mcp.Tests/`)**:
   - Standalone unit tests executing against `UnityStubs.cs` verifying truncation bounds, surrogate safety, cycle detection, tool descriptions, and parameter validation in <50ms.
2. **Package Integration Tests (`src/UnityLeanMcp.Unity3d/Assets/Tests/Editor/`)**:
   - NUnit tests executed via `unity_test` validating actual `SerializedObject` modifications, missing script detection, and EditMode material non-leakage.

---

## 8. Conclusion

The current `Inspect` suite provides a solid foundation for scene and object introspection, earning an average grade of **A-** across its core workhorses (`Hierarchy`, `GameObject`, `Component`, `Object`, `FindAssets`, `Selection`, `MainCamera`).

By adopting the recommendations of this RFC:
1. Refactoring `FindAsset` (multi-match disambiguation) and `Help` (dynamic reflection).
2. Implementing the four proposed inspection helpers (`Broken`, `SceneSummary`, `Find`, `PrefabOverrides`).
3. Adopting standard, direct Unity C# for mutations paired with the Three-Tier Progressive Disclosure Model.

UnityLeanMcp eliminates the major friction points and blindspots currently plaguing autonomous AI agents, transforming `unity_eval` into a fast, intuitive, production-grade engine for autonomous game development without departing from idiomatic Unity C#.
