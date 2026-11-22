#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        public const int DefaultAuditMaxPrefabs = 200;
        public const int DefaultAuditMaxIssuesPerCategory = DefaultMaxItems;
        private const int AuditUnloadUnusedAssetsBatchSize = 50;

        private static readonly Dictionary<Type, PropertyInfo> s_SharedMaterialPropCache = new Dictionary<Type, PropertyInfo>();

        #region Scene & Prefab Integrity Audit

        /// <summary>
        /// Performs a comprehensive, non-destructive integrity audit of loaded scenes and the active Prefab Stage,
        /// or a specific loaded scene, prefab asset, or folder (recursively auditing prefabs within that folder).
        /// Reports missing scripts, broken serialized ObjectReference fields, broken UnityEvent persistent listeners,
        /// error/missing shaders (via safe sharedMaterials/sharedMaterial inspection), and scene hygiene issues
        /// (duplicate AudioListener or EventSystem instances, missing MainCamera tag).
        /// Bounded by <paramref name="maxPrefabs"/> (for folder scans), <paramref name="maxIssuesPerCategory"/>,
        /// and cooperative cancellation (linked with the active eval operation token).
        /// </summary>
        public static string Audit(
            string targetOrAssetPath = null,
            int maxPrefabs = DefaultAuditMaxPrefabs,
            int maxIssuesPerCategory = DefaultAuditMaxIssuesPerCategory,
            CancellationToken cancellationToken = default)
        {
            maxPrefabs = Math.Max(1, maxPrefabs);
            maxIssuesPerCategory = Math.Max(1, maxIssuesPerCategory);

            CancellationTokenSource linkedCts = null;
            CancellationToken effectiveToken = CreateEffectiveAuditToken(cancellationToken, out linkedCts);
            try
            {
                effectiveToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(targetOrAssetPath))
                {
                    return AuditActiveEnvironment(maxIssuesPerCategory, effectiveToken);
                }

                string clean = NormalizeAuditTarget(targetOrAssetPath);

                // 1. Check active Prefab Stage match
                PrefabStage stage = GetCurrentPrefabStageSafe();
                string cleanNameWithoutExt = Path.GetFileNameWithoutExtension(clean);
                if (stage != null && MatchesPrefabStageQuery(stage, clean, cleanNameWithoutExt))
                {
                    var report = new AuditReport($"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\"", maxIssuesPerCategory);
                    GameObject stageRoot = null;
                    try { stageRoot = stage.prefabContentsRoot; } catch { }
                    if (stageRoot == null)
                    {
                        return $"Audit of Prefab Stage \"{GetPrefabStageAssetPath(stage)}\": (No root GameObject in Prefab Stage)";
                    }

                    AuditHierarchyRoots(new[] { stageRoot }, $"prefabStage: \"{GetPrefabStageAssetPath(stage)}\"", isSceneScope: false, report, effectiveToken);
                    return FormatAuditReport(report);
                }

                // 2. Check loaded scenes match
                int sceneCount = GetSceneCountSafe();
                for (int i = 0; i < sceneCount; i++)
                {
                    effectiveToken.ThrowIfCancellationRequested();

                    Scene s;
                    try { s = SceneManager.GetSceneAt(i); }
                    catch { continue; }

                    if (string.Equals(s.name, clean, StringComparison.OrdinalIgnoreCase) ||
                        (!clean.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(s.name, cleanNameWithoutExt, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(s.path) && string.Equals(s.path.Replace('\\', '/'), clean, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!s.isLoaded)
                        {
                            return $"(Scene \"{s.name}\" is not currently loaded)";
                        }

                        var report = new AuditReport($"Scene \"{s.name}\"", maxIssuesPerCategory);
                        GameObject[] roots = GetRootGameObjectsSafe(s);
                        AuditHierarchyRoots(roots, $"scene: \"{s.name}\"", isSceneScope: true, report, effectiveToken);
                        return FormatAuditReport(report);
                    }
                }

                // 3. Check folder path
                if (IsProjectFolder(clean))
                {
                    return AuditFolderPrefabs(clean, maxPrefabs, maxIssuesPerCategory, effectiveToken);
                }

                // 4. Check prefab asset path or search query
                if (TryResolvePrefabAssetPath(clean, targetOrAssetPath, out string resolvedPrefabPath, out string resolveError))
                {
                    var report = new AuditReport($"Prefab Asset \"{resolvedPrefabPath}\"", maxIssuesPerCategory);
                    if (!TryAuditPrefabAsset(resolvedPrefabPath, report, effectiveToken, out _, out string loadError))
                    {
                        return loadError;
                    }
                    return FormatAuditReport(report);
                }

                return resolveError;
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Performs a comprehensive, non-destructive integrity audit starting from a specific root GameObject and its hierarchy.
        /// Reports missing scripts, broken serialized ObjectReference fields, broken UnityEvent persistent listeners,
        /// error/missing shaders, and duplicate AudioListener or EventSystem instances within the subtree.
        /// Bounded by <paramref name="maxIssuesPerCategory"/> and cooperative cancellation.
        /// </summary>
        public static string Audit(
            GameObject root,
            int maxIssuesPerCategory = DefaultAuditMaxIssuesPerCategory,
            CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(root, null)) return "(GameObject is null)";
            if (root == null) return "(GameObject is destroyed)";
            if (root.transform == null) return "(GameObject has no Transform)";

            maxIssuesPerCategory = Math.Max(1, maxIssuesPerCategory);

            CancellationTokenSource linkedCts = null;
            CancellationToken effectiveToken = CreateEffectiveAuditToken(cancellationToken, out linkedCts);
            try
            {
                effectiveToken.ThrowIfCancellationRequested();

                string goName = "GameObject";
                try { goName = root.name; } catch { }

                string containerLabel = ResolveGameObjectContainerLabel(root);
                var report = new AuditReport($"GameObject \"{goName}\" ({containerLabel})", maxIssuesPerCategory);
                AuditHierarchyRoots(new[] { root }, containerLabel, isSceneScope: false, report, effectiveToken);
                return FormatAuditReport(report);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        private static CancellationToken CreateEffectiveAuditToken(
            CancellationToken callerToken,
            out CancellationTokenSource linkedCts)
        {
            CancellationToken activeToken = OperationExecutionEngine.GetActiveOperationToken();
            if (callerToken.CanBeCanceled && activeToken.CanBeCanceled && callerToken != activeToken)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, activeToken);
                return linkedCts.Token;
            }

            linkedCts = null;
            return callerToken.CanBeCanceled ? callerToken : activeToken;
        }

        private static string AuditActiveEnvironment(int maxIssuesPerCategory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PrefabStage stage = GetCurrentPrefabStageSafe();
            int sceneCount = GetSceneCountSafe();
            if (stage == null && sceneCount == 0)
            {
                return "(No scenes currently loaded)";
            }

            string scopeDescription = stage != null && sceneCount > 0
                ? $"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\" and {sceneCount} loaded scene(s)"
                : (stage != null ? $"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\"" : $"{sceneCount} loaded scene(s)");

            var report = new AuditReport(scopeDescription, maxIssuesPerCategory);
            bool anyAudited = false;

            if (stage != null)
            {
                GameObject stageRoot = null;
                try { stageRoot = stage.prefabContentsRoot; } catch { }
                if (stageRoot != null)
                {
                    anyAudited = true;
                    string stagePath = GetPrefabStageAssetPath(stage);
                    AuditHierarchyRoots(new[] { stageRoot }, $"prefabStage: \"{stagePath}\"", isSceneScope: false, report, cancellationToken);
                }
            }

            for (int s = 0; s < sceneCount; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (report.IsReportFull) break;

                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch { continue; }

                if (!scene.isLoaded) continue;

                anyAudited = true;
                GameObject[] roots = GetRootGameObjectsSafe(scene);
                AuditHierarchyRoots(roots, $"scene: \"{scene.name}\"", isSceneScope: true, report, cancellationToken);
            }

            if (!anyAudited)
            {
                return stage != null
                    ? "(No GameObjects found in Prefab Stage or loaded scenes)"
                    : "(No scenes currently loaded)";
            }

            return FormatAuditReport(report);
        }

        private static string AuditFolderPrefabs(
            string folder,
            int maxPrefabs,
            int maxIssuesPerCategory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] guids = null;
            try
            {
                guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            }
            catch (Exception ex)
            {
                return $"(Asset search failed for folder \"{folder}\": {ex.Message})";
            }

            var prefabPaths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (guids != null)
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    if ((i & 63) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    string rawPath = null;
                    try { rawPath = AssetDatabase.GUIDToAssetPath(guids[i]); } catch { }
                    if (string.IsNullOrEmpty(rawPath)) continue;

                    string normalized = rawPath.Replace('\\', '/');
                    if (!IsPathUnderFolder(normalized, folder)) continue;
                    if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                    if (seen.Add(normalized))
                    {
                        prefabPaths.Add(normalized);
                    }
                }
            }

            if (prefabPaths.Count == 0)
            {
                return $"(No prefab assets found in folder: \"{folder}\")";
            }

            prefabPaths.Sort(StringComparer.Ordinal);
            int totalPrefabsFound = prefabPaths.Count;
            int scanLimit = Math.Min(totalPrefabsFound, maxPrefabs);
            bool cappedByMaxPrefabs = totalPrefabsFound > maxPrefabs;

            var report = new AuditReport(string.Empty, maxIssuesPerCategory);
            int prefabsScanned = 0;
            int loadedSinceCleanup = 0;
            bool stoppedByFullReport = false;

            for (int i = 0; i < scanLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (report.IsReportFull)
                {
                    stoppedByFullReport = true;
                    break;
                }

                string prefabPath = prefabPaths[i];
                if (!TryAuditPrefabAsset(prefabPath, report, cancellationToken, out bool loadedViaPrefabContents, out string loadError))
                {
                    report.TryAddBrokenReference($"  • (prefab: \"{prefabPath}\"): {loadError}");
                }

                prefabsScanned++;

                if (loadedViaPrefabContents)
                {
                    loadedSinceCleanup++;
                    if (loadedSinceCleanup >= AuditUnloadUnusedAssetsBatchSize && i + 1 < scanLimit && !report.IsReportFull)
                    {
                        UnloadUnusedAssetsSafe();
                        loadedSinceCleanup = 0;
                    }
                }
            }

            report.ScopeDescription = prefabsScanned == totalPrefabsFound
                ? $"Folder \"{folder}\" ({totalPrefabsFound} prefab(s))"
                : $"Folder \"{folder}\" ({prefabsScanned} of {totalPrefabsFound} prefab(s) scanned)";

            if (stoppedByFullReport)
            {
                report.TruncationNote = $"... (truncated: output or per-category issue limit reached; {prefabsScanned} of {totalPrefabsFound} prefab(s) scanned)";
            }
            else if (cappedByMaxPrefabs)
            {
                report.TruncationNote = $"... (truncated: reached maxPrefabs limit of {maxPrefabs}; {prefabsScanned} of {totalPrefabsFound} prefab(s) scanned; pass a higher maxPrefabs value to scan more)";
            }

            return FormatAuditReport(report);
        }

        private static void UnloadUnusedAssetsSafe()
        {
            try
            {
                EditorUtility.UnloadUnusedAssetsImmediate();
            }
            catch { }
        }

        private static bool TryAuditPrefabAsset(
            string prefabPath,
            AuditReport report,
            CancellationToken cancellationToken,
            out bool loadedViaPrefabContents,
            out string error)
        {
            loadedViaPrefabContents = false;
            error = null;
            cancellationToken.ThrowIfCancellationRequested();

            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage != null)
            {
                string stagePath = GetPrefabStageAssetPath(stage);
                if (string.Equals(stagePath, prefabPath, StringComparison.OrdinalIgnoreCase) && stage.prefabContentsRoot != null)
                {
                    AuditHierarchyRoots(new[] { stage.prefabContentsRoot }, $"prefab: \"{prefabPath}\"", isSceneScope: false, report, cancellationToken);
                    return true;
                }
            }

            GameObject contentsRoot = null;
            try
            {
                try
                {
                    contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                    if (contentsRoot != null)
                    {
                        loadedViaPrefabContents = true;
                    }
                }
                catch { }

                if (contentsRoot == null)
                {
                    try
                    {
                        contentsRoot = AssetDatabase.LoadMainAssetAtPath(prefabPath) as GameObject;
                    }
                    catch { }
                }

                if (contentsRoot == null)
                {
                    error = $"(Could not load prefab asset at path: \"{prefabPath}\")";
                    return false;
                }

                AuditHierarchyRoots(new[] { contentsRoot }, $"prefab: \"{prefabPath}\"", isSceneScope: false, report, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"(Failed to audit prefab asset \"{prefabPath}\": {ex.Message})";
                return false;
            }
            finally
            {
                if (loadedViaPrefabContents && contentsRoot != null)
                {
                    try { PrefabUtility.UnloadPrefabContents(contentsRoot); } catch { }
                }
            }
        }

        private static void AuditHierarchyRoots(
            GameObject[] rootObjects,
            string containerLabel,
            bool isSceneScope,
            AuditReport report,
            CancellationToken cancellationToken)
        {
            var audioListenerPaths = new List<string>();
            var eventSystemPaths = new List<string>();
            int cameraCount = 0;
            int mainCameraTagCount = 0;

            if (rootObjects != null && rootObjects.Length > 0)
            {
                TraverseRootGameObjectsBfs(rootObjects, (current, go) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (report.IsReportFull)
                    {
                        return SceneBfsDecision.Stop;
                    }

                    report.GameObjectsScanned++;
                    string hierarchyPath = GetHierarchyPath(current);
                    string goName = "Unnamed";
                    try { goName = go.name; } catch { }

                    bool isMainCameraTagged = false;
                    try { isMainCameraTagged = string.Equals(go.tag, "MainCamera", StringComparison.Ordinal); } catch { }
                    if (isMainCameraTagged)
                    {
                        mainCameraTagCount++;
                    }

                    Component[] components = null;
                    try { components = go.GetComponents<Component>(); } catch { }
                    if (components == null) return SceneBfsDecision.Continue;

                    var missingIndices = new List<int>();

                    for (int c = 0; c < components.Length; c++)
                    {
                        var comp = components[c];
                        if (ReferenceEquals(comp, null) || comp == null)
                        {
                            missingIndices.Add(c);
                            continue;
                        }

                        Type compType = null;
                        try { compType = comp.GetType(); } catch { }
                        if (compType == null) continue;

                        if (comp is Camera)
                        {
                            cameraCount++;
                        }

                        if (IsComponentOfTypeName(compType, "AudioListener") && IsComponentActiveAndEnabled(comp))
                        {
                            audioListenerPaths.Add(hierarchyPath);
                        }

                        if (IsComponentOfTypeName(compType, "EventSystem") && IsComponentActiveAndEnabled(comp))
                        {
                            eventSystemPaths.Add(hierarchyPath);
                        }

                        AuditComponentMaterialsAndShaders(comp, compType, goName, hierarchyPath, containerLabel, report);
                        AuditComponentSerializedProperties(comp, compType, goName, hierarchyPath, containerLabel, report);
                    }

                    if (missingIndices.Count > 0)
                    {
                        string indicesStr = string.Join(", ", missingIndices);
                        string scriptWord = missingIndices.Count == 1 ? "missing script" : "missing scripts";
                        report.TryAddMissingScript(
                            $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {missingIndices.Count} {scriptWord} at component index [{indicesStr}]");
                    }

                    return report.IsReportFull ? SceneBfsDecision.Stop : SceneBfsDecision.Continue;
                });
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (audioListenerPaths.Count > 1)
            {
                report.TryAddSceneHygieneIssue(
                    $"  • Duplicate active AudioListener components ({audioListenerPaths.Count}) in {containerLabel}: [{string.Join(", ", audioListenerPaths)}]");
            }

            if (eventSystemPaths.Count > 1)
            {
                report.TryAddSceneHygieneIssue(
                    $"  • Duplicate active EventSystem components ({eventSystemPaths.Count}) in {containerLabel}: [{string.Join(", ", eventSystemPaths)}]");
            }

            if (isSceneScope && mainCameraTagCount == 0)
            {
                string camDetail = cameraCount > 0
                    ? $"{cameraCount} untagged Camera component(s) present"
                    : "no Camera components present";
                report.TryAddSceneHygieneIssue(
                    $"  • Missing \"MainCamera\" tag in {containerLabel} ({camDetail})");
            }
        }

        private static void AuditComponentMaterialsAndShaders(
            Component comp,
            Type compType,
            string goName,
            string hierarchyPath,
            string containerLabel,
            AuditReport report)
        {
            if (comp is Renderer renderer)
            {
                Material[] sharedMats = null;
                try { sharedMats = renderer.sharedMaterials; } catch { }
                if (sharedMats != null)
                {
                    for (int m = 0; m < sharedMats.Length; m++)
                    {
                        var mat = sharedMats[m];
                        if (ReferenceEquals(mat, null) || mat == null) continue;
                        AuditMaterialShader(mat, $"{compType.Name}.sharedMaterials[{m}]", goName, hierarchyPath, containerLabel, report);
                    }
                }
                return;
            }

            // Safely inspect sharedMaterial on non-Renderer components (e.g. Graphic / UI components) without touching .material
            PropertyInfo sharedMatProp = GetCachedProperty(s_SharedMaterialPropCache, compType, "sharedMaterial", typeof(Material));
            if (sharedMatProp != null)
            {
                Material sharedMat = null;
                try { sharedMat = sharedMatProp.GetValue(comp, null) as Material; } catch { }
                if (!ReferenceEquals(sharedMat, null) && sharedMat != null)
                {
                    AuditMaterialShader(sharedMat, $"{compType.Name}.sharedMaterial", goName, hierarchyPath, containerLabel, report);
                }
            }
        }

        private static void AuditMaterialShader(
            Material mat,
            string accessorPath,
            string goName,
            string hierarchyPath,
            string containerLabel,
            AuditReport report)
        {
            string matName = "Unnamed";
            try { matName = !string.IsNullOrEmpty(mat.name) ? mat.name : "Unnamed"; } catch { }

            Shader shader = null;
            try { shader = mat.shader; } catch { }

            if (ReferenceEquals(shader, null) || shader == null)
            {
                report.TryAddMaterialAndShaderIssue(
                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {accessorPath} (\"{matName}\") has missing/null Shader");
                return;
            }

            string shaderName = string.Empty;
            try { shaderName = shader.name ?? string.Empty; } catch { }

            if (string.Equals(shaderName, "Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase))
            {
                report.TryAddMaterialAndShaderIssue(
                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {accessorPath} (\"{matName}\") uses error shader \"Hidden/InternalErrorShader\"");
                return;
            }

            bool hasShaderError = false;
            try { hasShaderError = ShaderUtil.ShaderHasError(shader); } catch { }
            if (hasShaderError)
            {
                report.TryAddMaterialAndShaderIssue(
                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {accessorPath} (\"{matName}\") shader \"{shaderName}\" has compilation errors");
            }
        }

        private static void AuditComponentSerializedProperties(
            Component comp,
            Type compType,
            string goName,
            string hierarchyPath,
            string containerLabel,
            AuditReport report)
        {
            try
            {
                using (var so = new SerializedObject(comp))
                {
                    var sp = so.GetIterator();
                    bool enterChildren = true;

                    while (sp.NextVisible(enterChildren))
                    {
                        enterChildren = true;
                        string propPath = sp.propertyPath;
                        if (string.IsNullOrEmpty(propPath) || propPath == "m_Script")
                        {
                            enterChildren = false;
                            continue;
                        }

                        if (sp.propertyType == SerializedPropertyType.String)
                        {
                            enterChildren = false;
                            continue;
                        }

                        // Check if this property is a UnityEvent (contains m_PersistentCalls.m_Calls)
                        var callsProp = sp.FindPropertyRelative("m_PersistentCalls.m_Calls");
                        if (callsProp == null)
                        {
                            var persistentCallsProp = sp.FindPropertyRelative("m_PersistentCalls");
                            if (persistentCallsProp != null)
                            {
                                callsProp = persistentCallsProp.FindPropertyRelative("m_Calls");
                            }
                        }

                        if (callsProp != null && callsProp.isArray)
                        {
                            AuditUnityEventCalls(callsProp, compType.Name, propPath, goName, hierarchyPath, containerLabel, report);
                            enterChildren = false;
                            continue;
                        }

                        if (sp.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            UnityEngine.Object refVal = null;
                            int instanceId = 0;
                            try { refVal = sp.objectReferenceValue; } catch { }
                            try { instanceId = sp.objectReferenceInstanceIDValue; } catch { }

                            if (refVal == null && instanceId != 0)
                            {
                                report.TryAddBrokenReference(
                                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compType.Name}.{propPath} is Missing (instanceID: {instanceId})");
                            }
                            enterChildren = false;
                        }
                    }
                }
            }
            catch
            {
                // SerializedObject creation or traversal may not be supported on unusual/stubbed components
            }
        }

        private static void AuditUnityEventCalls(
            SerializedProperty callsProp,
            string compTypeName,
            string eventPropertyPath,
            string goName,
            string hierarchyPath,
            string containerLabel,
            AuditReport report)
        {
            int callCount = 0;
            try { callCount = callsProp.arraySize; } catch { }

            for (int i = 0; i < callCount; i++)
            {
                SerializedProperty callElement = null;
                try { callElement = callsProp.GetArrayElementAtIndex(i); } catch { }
                if (callElement == null) continue;

                var modeProp = callElement.FindPropertyRelative("m_Mode");
                if (modeProp != null && modeProp.intValue == 0) // PersistentListenerMode.EventDefined / Off
                {
                    var targetCheck = callElement.FindPropertyRelative("m_Target");
                    var methodCheck = callElement.FindPropertyRelative("m_MethodName");
                    bool hasTargetRef = targetCheck != null && (targetCheck.objectReferenceValue != null || targetCheck.objectReferenceInstanceIDValue != 0);
                    bool hasMethod = methodCheck != null && !string.IsNullOrEmpty(methodCheck.stringValue);
                    if (!hasTargetRef && !hasMethod)
                    {
                        continue;
                    }
                }

                var targetProp = callElement.FindPropertyRelative("m_Target");
                var methodNameProp = callElement.FindPropertyRelative("m_MethodName");
                var argumentsProp = callElement.FindPropertyRelative("m_Arguments");

                UnityEngine.Object targetObj = null;
                int targetInstanceId = 0;
                if (targetProp != null)
                {
                    try { targetObj = targetProp.objectReferenceValue; } catch { }
                    try { targetInstanceId = targetProp.objectReferenceInstanceIDValue; } catch { }
                }

                string methodName = methodNameProp != null ? (methodNameProp.stringValue ?? string.Empty) : string.Empty;

                if (targetObj == null)
                {
                    string reason = targetInstanceId != 0
                        ? $"missing target object (instanceID: {targetInstanceId})"
                        : "null target object";
                    string methodDetail = !string.IsNullOrEmpty(methodName) ? $" (method: \"{methodName}\")" : "";
                    report.TryAddBrokenUnityEvent(
                        $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] has {reason}{methodDetail}");
                    continue;
                }

                if (string.IsNullOrEmpty(methodName))
                {
                    report.TryAddBrokenUnityEvent(
                        $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] on target \"{targetObj.name}\" ({targetObj.GetType().Name}) has no method assigned");
                    continue;
                }

                Type targetType = targetObj.GetType();
                if (!HasMatchingPersistentListenerMethod(targetType, methodName, modeProp, argumentsProp))
                {
                    report.TryAddBrokenUnityEvent(
                        $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] method \"{targetType.Name}.{methodName}\" not found on target \"{targetObj.name}\"");
                }
            }
        }

        private static bool HasMatchingPersistentListenerMethod(
            Type targetType,
            string methodName,
            SerializedProperty modeProp,
            SerializedProperty argumentsProp)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName)) return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            int? mode = modeProp != null ? modeProp.intValue : (int?)null;
            string objectArgTypeName = null;
            if (argumentsProp != null)
            {
                var typeNameProp = argumentsProp.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName");
                if (typeNameProp != null)
                {
                    objectArgTypeName = typeNameProp.stringValue;
                }
            }

            for (Type current = targetType; current != null && current != typeof(object); current = current.BaseType)
            {
                MethodInfo[] methods = null;
                try { methods = current.GetMethods(flags | BindingFlags.DeclaredOnly); } catch { }
                if (methods == null) continue;

                for (int i = 0; i < methods.Length; i++)
                {
                    var m = methods[i];
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                    if (m.ReturnType != typeof(void)) continue;

                    var parameters = m.GetParameters();
                    if (IsMethodCompatibleWithPersistentMode(parameters, mode, objectArgTypeName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsMethodCompatibleWithPersistentMode(
            ParameterInfo[] parameters,
            int? mode,
            string objectArgTypeName)
        {
            if (parameters == null) return false;

            // PersistentListenerMode:
            // 0 = EventDefined (1 or more parameters, or any void method)
            // 1 = Void (0 parameters)
            // 2 = Object (1 parameter assignable from UnityEngine.Object / specified type)
            // 3 = Int (1 int parameter)
            // 4 = Float (1 float parameter)
            // 5 = String (1 string parameter)
            // 6 = Bool (1 bool parameter)
            if (!mode.HasValue || mode.Value == 0)
            {
                return true;
            }

            switch (mode.Value)
            {
                case 1: // Void
                    return parameters.Length == 0;
                case 2: // Object
                    if (parameters.Length != 1) return false;
                    Type paramType = parameters[0].ParameterType;
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(paramType)) return false;
                    if (!string.IsNullOrEmpty(objectArgTypeName))
                    {
                        Type expectedType = ResolvePersistentArgumentType(objectArgTypeName);
                        if (expectedType != null && !paramType.IsAssignableFrom(expectedType) && !expectedType.IsAssignableFrom(paramType))
                        {
                            return false;
                        }
                    }
                    return true;
                case 3: // Int
                    return parameters.Length == 1 && (parameters[0].ParameterType == typeof(int) || parameters[0].ParameterType.IsEnum);
                case 4: // Float
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(float);
                case 5: // String
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                case 6: // Bool
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(bool);
                default:
                    return true;
            }
        }

        private static Type ResolvePersistentArgumentType(string assemblyQualifiedTypeName)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedTypeName)) return null;
            try
            {
                Type resolved = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
                if (resolved != null) return resolved;

                int commaIdx = assemblyQualifiedTypeName.IndexOf(',');
                string fullTypeName = commaIdx > 0 ? assemblyQualifiedTypeName.Substring(0, commaIdx).Trim() : assemblyQualifiedTypeName.Trim();

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    try
                    {
                        resolved = assemblies[i].GetType(fullTypeName, throwOnError: false);
                        if (resolved != null) return resolved;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static bool IsComponentOfTypeName(Type compType, string shortTypeName)
        {
            for (Type cur = compType; cur != null && cur != typeof(UnityEngine.Object) && cur != typeof(object); cur = cur.BaseType)
            {
                if (string.Equals(cur.Name, shortTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsComponentActiveAndEnabled(Component comp)
        {
            if (comp == null) return false;
            if (comp is Behaviour beh)
            {
                try { return beh.isActiveAndEnabled; } catch { }
            }
            try
            {
                return comp.gameObject == null || comp.gameObject.activeInHierarchy;
            }
            catch
            {
                return true;
            }
        }

        private static string ResolveGameObjectContainerLabel(GameObject root)
        {
            try
            {
                string assetPath = AssetDatabase.GetAssetPath(root);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return $"prefab: \"{assetPath.Replace('\\', '/')}\"";
                }
            }
            catch { }

            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                try
                {
                    if (root == stage.prefabContentsRoot || root.transform.IsChildOf(stage.prefabContentsRoot.transform))
                    {
                        return $"prefabStage: \"{GetPrefabStageAssetPath(stage)}\"";
                    }
                }
                catch { }
            }

            try
            {
                if (root.scene.IsValid())
                {
                    return $"scene: \"{root.scene.name}\"";
                }
            }
            catch { }

            return "hierarchy";
        }

        private static string NormalizeAuditTarget(string rawTarget)
        {
            string clean = rawTarget.Trim().Replace('\\', '/');
            while (clean.StartsWith("./", StringComparison.Ordinal))
            {
                clean = clean.Substring(2);
            }
            while (clean.StartsWith("/", StringComparison.Ordinal) && clean.Length > 1)
            {
                clean = clean.TrimStart('/');
            }
            while (clean.EndsWith("/", StringComparison.Ordinal) && clean.Length > 1)
            {
                clean = clean.Substring(0, clean.Length - 1);
            }
            return clean;
        }

        private static bool IsProjectFolder(string cleanPath)
        {
            if (string.IsNullOrEmpty(cleanPath)) return false;
            if (string.Equals(cleanPath, "Assets", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanPath, "Packages", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                if (AssetDatabase.IsValidFolder(cleanPath)) return true;
            }
            catch { }

            try
            {
                if (Directory.Exists(cleanPath)) return true;
            }
            catch { }

            return false;
        }

        private static bool IsPathUnderFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folderPath)) return false;
            if (string.Equals(assetPath, folderPath, StringComparison.OrdinalIgnoreCase)) return true;

            string prefix = folderPath.EndsWith("/", StringComparison.Ordinal) ? folderPath : folderPath + "/";
            return assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolvePrefabAssetPath(
            string cleanQuery,
            string rawInput,
            out string resolvedPrefabPath,
            out string error)
        {
            resolvedPrefabPath = null;
            error = null;

            bool isExplicitProjectPath =
                cleanQuery.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                cleanQuery.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

            if (isExplicitProjectPath)
            {
                UnityEngine.Object assetObj = null;
                try { assetObj = AssetDatabase.LoadMainAssetAtPath(cleanQuery); } catch { }
                string guid = null;
                try { guid = AssetDatabase.AssetPathToGUID(cleanQuery); } catch { }

                bool existsOnDisk = false;
                try { existsOnDisk = File.Exists(cleanQuery); } catch { }

                if (assetObj == null && string.IsNullOrEmpty(guid) && !existsOnDisk)
                {
                    error = $"(Audit target not found: \"{rawInput}\")";
                    return false;
                }

                if (!cleanQuery.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                    !(assetObj is GameObject))
                {
                    error = $"(Asset at path \"{cleanQuery}\" is not a Prefab or loaded Scene)";
                    return false;
                }

                resolvedPrefabPath = cleanQuery;
                return true;
            }

            string searchQuery = cleanQuery.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(cleanQuery)
                : cleanQuery;

            string[] guids = null;
            try
            {
                guids = AssetDatabase.FindAssets($"{searchQuery} t:Prefab");
            }
            catch (Exception ex)
            {
                error = $"(Asset search failed: {ex.Message})";
                return false;
            }

            if (guids != null)
            {
                string firstCandidate = null;
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = null;
                    try { path = AssetDatabase.GUIDToAssetPath(guids[i]); } catch { }
                    if (string.IsNullOrEmpty(path)) continue;

                    string normalized = path.Replace('\\', '/');
                    if (firstCandidate == null) firstCandidate = normalized;

                    string fileName = Path.GetFileName(normalized);
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(normalized);
                    if (string.Equals(fileName, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileNameNoExt, searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPrefabPath = normalized;
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(firstCandidate))
                {
                    resolvedPrefabPath = firstCandidate;
                    return true;
                }
            }

            error = $"(Audit target \"{rawInput}\" was not found as a loaded scene, active Prefab Stage, folder, or prefab asset)";
            return false;
        }

        private static string FormatAuditReport(AuditReport report)
        {
            int totalIssues = report.TotalIssues;
            var sb = new StringBuilder();

            if (totalIssues == 0)
            {
                sb.AppendLine($"Audit of {report.ScopeDescription}: 0 issues found ({report.GameObjectsScanned} GameObject(s) scanned).");
                if (!string.IsNullOrEmpty(report.TruncationNote))
                {
                    sb.AppendLine();
                    sb.AppendLine(report.TruncationNote);
                }
                return LimitOutput(sb.ToString().TrimEnd());
            }

            string issueSummary = report.TotalSuppressed > 0
                ? $"{totalIssues} issue(s) reported ({report.TotalSuppressed} additional omitted)"
                : $"{totalIssues} issue(s) found";
            sb.AppendLine($"Audit of {report.ScopeDescription}: {issueSummary} ({report.GameObjectsScanned} GameObject(s) scanned):");

            AppendAuditSection(sb, "Missing Scripts", report.MissingScripts, report.MissingScriptsSuppressed, report.MaxIssuesPerCategory);
            AppendAuditSection(sb, "Broken Object References", report.BrokenReferences, report.BrokenReferencesSuppressed, report.MaxIssuesPerCategory);
            AppendAuditSection(sb, "Broken UnityEvent Listeners", report.BrokenUnityEvents, report.BrokenUnityEventsSuppressed, report.MaxIssuesPerCategory);
            AppendAuditSection(sb, "Material & Shader Errors", report.MaterialAndShaderIssues, report.MaterialAndShaderIssuesSuppressed, report.MaxIssuesPerCategory);
            AppendAuditSection(sb, "Scene Hygiene Issues", report.SceneHygieneIssues, report.SceneHygieneIssuesSuppressed, report.MaxIssuesPerCategory);

            if (!string.IsNullOrEmpty(report.TruncationNote))
            {
                sb.AppendLine();
                sb.AppendLine(report.TruncationNote);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void AppendAuditSection(
            StringBuilder sb,
            string title,
            List<string> items,
            int suppressedCount,
            int maxIssuesPerCategory)
        {
            if (items == null || items.Count == 0) return;

            sb.AppendLine();
            string countText = suppressedCount > 0
                ? $"{items.Count}+, {suppressedCount} omitted"
                : items.Count.ToString();
            sb.AppendLine($"{title} ({countText}):");
            for (int i = 0; i < items.Count; i++)
            {
                sb.AppendLine(items[i]);
            }

            if (suppressedCount > 0)
            {
                sb.AppendLine($"  ... ({suppressedCount} more omitted; maxIssuesPerCategory={maxIssuesPerCategory})");
            }
        }

        private sealed class AuditReport
        {
            public string ScopeDescription { get; set; }
            public int MaxIssuesPerCategory { get; }
            public int GameObjectsScanned { get; set; }
            public int EstimatedCharacterCount { get; private set; }
            public string TruncationNote { get; set; }

            public List<string> MissingScripts { get; } = new List<string>();
            public List<string> BrokenReferences { get; } = new List<string>();
            public List<string> BrokenUnityEvents { get; } = new List<string>();
            public List<string> MaterialAndShaderIssues { get; } = new List<string>();
            public List<string> SceneHygieneIssues { get; } = new List<string>();

            public int MissingScriptsSuppressed { get; private set; }
            public int BrokenReferencesSuppressed { get; private set; }
            public int BrokenUnityEventsSuppressed { get; private set; }
            public int MaterialAndShaderIssuesSuppressed { get; private set; }
            public int SceneHygieneIssuesSuppressed { get; private set; }

            public int TotalIssues =>
                MissingScripts.Count +
                BrokenReferences.Count +
                BrokenUnityEvents.Count +
                MaterialAndShaderIssues.Count +
                SceneHygieneIssues.Count;

            public int TotalSuppressed =>
                MissingScriptsSuppressed +
                BrokenReferencesSuppressed +
                BrokenUnityEventsSuppressed +
                MaterialAndShaderIssuesSuppressed +
                SceneHygieneIssuesSuppressed;

            public bool IsOutputBudgetReached => EstimatedCharacterCount >= DefaultMaxOutputCharacters;

            public bool AreAllCategoriesCapped =>
                MissingScripts.Count >= MaxIssuesPerCategory &&
                BrokenReferences.Count >= MaxIssuesPerCategory &&
                BrokenUnityEvents.Count >= MaxIssuesPerCategory &&
                MaterialAndShaderIssues.Count >= MaxIssuesPerCategory &&
                SceneHygieneIssues.Count >= MaxIssuesPerCategory;

            public bool IsReportFull => IsOutputBudgetReached || AreAllCategoriesCapped;

            public AuditReport(string scopeDescription, int maxIssuesPerCategory)
            {
                ScopeDescription = scopeDescription;
                MaxIssuesPerCategory = Math.Max(1, maxIssuesPerCategory);
            }

            public bool TryAddMissingScript(string message)
            {
                int suppressed = MissingScriptsSuppressed;
                bool added = TryAddToCategory(MissingScripts, ref suppressed, message);
                MissingScriptsSuppressed = suppressed;
                return added;
            }

            public bool TryAddBrokenReference(string message)
            {
                int suppressed = BrokenReferencesSuppressed;
                bool added = TryAddToCategory(BrokenReferences, ref suppressed, message);
                BrokenReferencesSuppressed = suppressed;
                return added;
            }

            public bool TryAddBrokenUnityEvent(string message)
            {
                int suppressed = BrokenUnityEventsSuppressed;
                bool added = TryAddToCategory(BrokenUnityEvents, ref suppressed, message);
                BrokenUnityEventsSuppressed = suppressed;
                return added;
            }

            public bool TryAddMaterialAndShaderIssue(string message)
            {
                int suppressed = MaterialAndShaderIssuesSuppressed;
                bool added = TryAddToCategory(MaterialAndShaderIssues, ref suppressed, message);
                MaterialAndShaderIssuesSuppressed = suppressed;
                return added;
            }

            public bool TryAddSceneHygieneIssue(string message)
            {
                int suppressed = SceneHygieneIssuesSuppressed;
                bool added = TryAddToCategory(SceneHygieneIssues, ref suppressed, message);
                SceneHygieneIssuesSuppressed = suppressed;
                return added;
            }

            private bool TryAddToCategory(List<string> list, ref int suppressedCount, string message)
            {
                if (IsOutputBudgetReached || list.Count >= MaxIssuesPerCategory)
                {
                    suppressedCount++;
                    return false;
                }

                list.Add(message);
                EstimatedCharacterCount += (message?.Length ?? 0) + 2;
                return true;
            }
        }

        #endregion
    }
}
