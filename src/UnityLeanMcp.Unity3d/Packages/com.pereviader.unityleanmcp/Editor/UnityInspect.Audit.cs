#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        private struct LazyAuditTarget
        {
            private readonly GameObject _go;
            private readonly Transform _transform;
            private string _name;
            private string _path;

            public LazyAuditTarget(GameObject go, Transform transform)
            {
                _go = go;
                _transform = transform;
                _name = null;
                _path = null;
            }

            public string GetName()
            {
                if (_name == null)
                {
                    string name = "Unnamed";
                    try { name = _go.name; } catch { }
                    _name = name ?? "Unnamed";
                }
                return _name;
            }

            public string GetPath()
            {
                if (_path == null)
                {
                    _path = GetHierarchyPath(_transform);
                }
                return _path;
            }
        }

        #region Scene & Prefab Integrity Audit

        /// <summary>
        /// Performs a comprehensive, non-destructive integrity audit of loaded scenes and the active Prefab Stage,
        /// or a specific loaded scene, prefab asset, or folder (recursively auditing prefabs within that folder).
        /// Reports missing scripts, broken serialized ObjectReference fields, broken UnityEvent persistent listeners,
        /// error/missing shaders (via safe sharedMaterials/sharedMaterial, Graphic.material, and TMP_Text.fontSharedMaterial/fontSharedMaterials inspection), and scene hygiene issues
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
            CancellationToken effectiveToken = CreateEffectiveOperationToken(cancellationToken, out linkedCts);
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

                    if (MatchesLoadedSceneQuery(s, clean, cleanNameWithoutExt))
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
            CancellationToken effectiveToken = CreateEffectiveOperationToken(cancellationToken, out linkedCts);
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
                ? FormattableString.Invariant($"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\" and {sceneCount} loaded scene(s)")
                : (stage != null ? $"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\"" : FormattableString.Invariant($"{sceneCount} loaded scene(s)"));

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
            var seen = new HashSet<string>(StringComparer.Ordinal);

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
                ? FormattableString.Invariant($"Folder \"{folder}\" ({totalPrefabsFound} prefab(s))")
                : FormattableString.Invariant($"Folder \"{folder}\" ({prefabsScanned} of {totalPrefabsFound} prefab(s) scanned)");

            if (stoppedByFullReport)
            {
                report.TruncationNote = FormattableString.Invariant($"... (truncated: output or per-category issue limit reached; {prefabsScanned} of {totalPrefabsFound} prefab(s) scanned)");
            }
            else if (cappedByMaxPrefabs)
            {
                report.TruncationNote = FormattableString.Invariant($"... (truncated: reached maxPrefabs limit of {maxPrefabs}; {prefabsScanned} of {totalPrefabsFound} prefab(s) scanned; pass a higher maxPrefabs value to scan more)");
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
                var reusableComponents = new List<Component>();
                var reusableMaterials = new List<Material>();

                TraverseRootGameObjectsBfs(rootObjects, (current, go) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (report.IsReportFull)
                    {
                        return SceneBfsDecision.Stop;
                    }

                    report.GameObjectsScanned++;

                    bool isMainCameraTagged = false;
                    try { isMainCameraTagged = go.CompareTag("MainCamera"); } catch { }
                    if (isMainCameraTagged)
                    {
                        mainCameraTagCount++;
                    }

                    reusableComponents.Clear();
                    bool gotComponents = false;
                    try
                    {
                        go.GetComponents(reusableComponents);
                        gotComponents = true;
                    }
                    catch { }
                    if (!gotComponents) return SceneBfsDecision.Continue;

                    var target = new LazyAuditTarget(go, current);
                    List<int> missingIndices = null;

                    for (int c = 0; c < reusableComponents.Count; c++)
                    {
                        var comp = reusableComponents[c];
                        if (ReferenceEquals(comp, null) || comp == null)
                        {
                            if (missingIndices == null)
                            {
                                missingIndices = new List<int>();
                            }
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
                            audioListenerPaths.Add(target.GetPath());
                        }

                        if (IsComponentOfTypeName(compType, "EventSystem") && IsComponentActiveAndEnabled(comp))
                        {
                            eventSystemPaths.Add(target.GetPath());
                        }

                        AuditComponentMaterialsAndShaders(comp, compType, ref target, containerLabel, report, reusableMaterials);
                        AuditComponentSerializedProperties(comp, compType, ref target, containerLabel, report);
                    }

                    if (missingIndices != null && missingIndices.Count > 0)
                    {
                        string indicesStr = string.Join(", ", missingIndices.ConvertAll(idx => idx.ToString(CultureInfo.InvariantCulture)));
                        string scriptWord = missingIndices.Count == 1 ? "missing script" : "missing scripts";
                        report.TryAddMissingScript(
                            FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {missingIndices.Count} {scriptWord} at component index [{indicesStr}]"));
                    }

                    return report.IsReportFull ? SceneBfsDecision.Stop : SceneBfsDecision.Continue;
                });
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (audioListenerPaths.Count > 1)
            {
                report.TryAddSceneHygieneIssue(
                    FormattableString.Invariant($"  • Duplicate active AudioListener components ({audioListenerPaths.Count}) in {containerLabel}: [{string.Join(", ", audioListenerPaths)}]"));
            }

            if (eventSystemPaths.Count > 1)
            {
                report.TryAddSceneHygieneIssue(
                    FormattableString.Invariant($"  • Duplicate active EventSystem components ({eventSystemPaths.Count}) in {containerLabel}: [{string.Join(", ", eventSystemPaths)}]"));
            }

            if (isSceneScope && mainCameraTagCount == 0)
            {
                string camDetail = cameraCount > 0
                    ? FormattableString.Invariant($"{cameraCount} untagged Camera component(s) present")
                    : "no Camera components present";
                report.TryAddSceneHygieneIssue(
                    $"  • Missing \"MainCamera\" tag in {containerLabel} ({camDetail})");
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

            string[] segments = cleanPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "..", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (string.Equals(cleanPath, "Assets", StringComparison.Ordinal) ||
                string.Equals(cleanPath, "Packages", StringComparison.Ordinal))
            {
                return true;
            }

            if (!cleanPath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !cleanPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                return AssetDatabase.IsValidFolder(cleanPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathUnderFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folderPath)) return false;
            if (string.Equals(assetPath, folderPath, StringComparison.Ordinal)) return true;

            string prefix = folderPath.EndsWith("/", StringComparison.Ordinal) ? folderPath : folderPath + "/";
            return assetPath.StartsWith(prefix, StringComparison.Ordinal);
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

        #endregion
    }
}
