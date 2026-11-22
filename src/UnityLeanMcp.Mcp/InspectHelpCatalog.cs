namespace UnityLeanMcp.Mcp;

internal static class InspectHelpCatalog
{
    internal const string ResourceUri = "unity://inspect/help";
    internal const string ResourceName = "inspect_help";
    internal const string ResourceTitle = "Unity Inspect Helpers Cheatsheet";
    internal const string ResourceMimeType = "text/plain";
    internal const string ResourceDescription = "Read-only cheatsheet of all built-in UnityLeanMcp.Inspect static helper signatures for unity_eval (scene hierarchy, GameObjects/components, assets/prefabs, UI Toolkit, visual capture, editor state). Read this before writing reflection or inspecting Unity state via unity_eval.";

    internal const string SignatureReference =
        "UnityLeanMcp Inspection Helpers (UnityLeanMcp.Inspect):\n" +
        "\n" +
        "Scene & Hierarchy:\n" +
        "• string Audit(GameObject root, int maxIssuesPerCategory = 100, CancellationToken cancellationToken = default)\n" +
        "• string Audit(string targetOrAssetPath = null, int maxPrefabs = 200, int maxIssuesPerCategory = 100, CancellationToken cancellationToken = default)\n" +
        "• string Find(string namePattern, bool includeInactive = true, int maxItems = 100)\n" +
        "• string FindByComponent(string typeName, bool includeInactive = true, int maxItems = 100)\n" +
        "• string Hierarchy()\n" +
        "• string Hierarchy(bool includeUiDetails)\n" +
        "• string Hierarchy(int maxDepth, int maxItems = 100, bool includeUiDetails = false)\n" +
        "• string Hierarchy(GameObject root, int maxDepth = 32, int maxItems = 100, bool includeUiDetails = false)\n" +
        "• string Hierarchy(string sceneName, int maxDepth = 32, int maxItems = 100, bool includeUiDetails = false)\n" +
        "• string Hierarchy(Transform root, int maxDepth = 32, int maxItems = 100, bool includeUiDetails = false)\n" +
        "• string PrefabStage()\n" +
        "• string PrefabStage(int maxDepth, int maxItems = 100)\n" +
        "• string SceneState()\n" +
        "• string UIDocument(int maxDepth = 32, int maxItems = 100)\n" +
        "• string UIDocument(GameObject gameObject, int maxDepth = 32, int maxItems = 100)\n" +
        "• string UIDocument(UIDocument document, int maxDepth = 32, int maxItems = 100)\n" +
        "• string UIDocument(VisualElement root, int maxDepth = 32, int maxItems = 100)\n" +
        "\n" +
        "Objects & Components:\n" +
        "• string Component(Component component, int maxItems = 50)\n" +
        "• string GameObject(GameObject gameObject, int maxItems = 50)\n" +
        "• string Object(object target, int maxDepth = 2, int maxItems = 50)\n" +
        "• string PrefabOverrides(GameObject instance, int maxItems = 50)\n" +
        "\n" +
        "Assets & Prefabs:\n" +
        "• string Dependencies(string assetPath, bool recursive = false, int maxItems = 100)\n" +
        "• string FindAsset(string nameOrPath)\n" +
        "• string FindAssets(string filter = null, string type = null, string label = null, int maxItems = 50)\n" +
        "• string FindPrefabs(string filter = null, int maxItems = 50)\n" +
        "• string Referencers(string targetAssetPath, string searchFolder = \"Assets\", int maxItems = 100)\n" +
        "\n" +
        "Visual Capture:\n" +
        "• string CaptureCamera(string outputPath = \"Temp/unity_capture.png\", int width = 1024, int height = 768, Camera camera = null)\n" +
        "• Task<string> CaptureGameView(string outputPath = \"Temp/unity_gameview.png\", int superSize = 1, CancellationToken cancellationToken = default)\n" +
        "• string CaptureSceneView(string outputPath = \"Temp/unity_sceneview.png\", int width = 1024, int height = 768)\n" +
        "• string CaptureTarget(GameObject target, string outputPath = \"Temp/unity_target.png\", int width = 1024, int height = 768, Vector3? viewDirection = null)\n" +
        "\n" +
        "Editor:\n" +
        "• string Console(ConsoleSeverity severity = ConsoleSeverity.All, string filter = null, int maxItems = 50, bool includeStackTrace = false)\n" +
        "• string MainCamera()\n" +
        "• string Selection(int maxItems = 50)";

    internal const string UsageFooter =
        "Usage in unity_eval:\n" +
        "• Pre-imported namespaces: `System`, `System.Collections.Generic`, `System.Linq`, `UnityEngine`, `UnityEditor`, and `UnityLeanMcp`. Add explicit `using` directives for other namespaces, and qualify or alias ambiguous names like `UnityEngine.Object` / `UnityEngine.Random`.\n" +
        "• Return values with `return Inspect.<Method>(...);` (e.g., `return Inspect.Hierarchy(maxDepth: 3);`).\n" +
        "• Visual capture guide: `await Inspect.CaptureGameView()` = player view (cameras + ScreenSpaceOverlay Canvas + UIDocument); `Inspect.CaptureCamera()` = single Camera; `Inspect.CaptureSceneView()` = Editor SceneView; `Inspect.CaptureTarget(go)` = auto-framed GameObject/prefab from its front (or pass `viewDirection`, e.g. `Vector3.up`, for custom world angles).\n" +
        "• Integrity audit guide: `Inspect.Audit(...)` checks missing scripts, broken `ObjectReference` fields, broken `UnityEvent` listeners, error shaders, and scene hygiene (duplicate `AudioListener`/`EventSystem`, missing `MainCamera` tag). Accepts `null` (active Prefab Stage + loaded scenes), a `GameObject` root, a loaded scene name/path, a prefab asset path, or a folder path (bounded by `maxPrefabs = 200`, `maxIssuesPerCategory = 100`, cooperative cancellation, and periodic unused-asset cleanup). Note: `Inspect.Audit` is a costly operation—always run it on the smallest appropriate subset (a specific `GameObject`, prefab path, or narrow subfolder).\n" +
        "• Defaults: DefaultMaxDepth = 32, DefaultMaxItems = 100, DefaultMaxOutputCharacters = 65536 (64 KiB).";

    internal const string FullHelpText = SignatureReference + "\n\n" + UsageFooter;
}
