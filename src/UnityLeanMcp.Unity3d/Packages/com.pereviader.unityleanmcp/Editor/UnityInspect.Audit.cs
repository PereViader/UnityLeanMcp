#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private static readonly Dictionary<Type, PropertyInfo> s_SharedMaterialPropCache = new Dictionary<Type, PropertyInfo>();

        #region Scene & Prefab Integrity Audit

        /// <summary>
        /// Performs a comprehensive, non-destructive integrity audit of loaded scenes and the active Prefab Stage,
        /// or a specific loaded scene, prefab asset, or folder (recursively auditing all prefabs within that folder).
        /// Reports missing scripts, broken serialized ObjectReference fields, broken UnityEvent persistent listeners,
        /// error/missing shaders (via safe sharedMaterials/sharedMaterial inspection), and scene hygiene issues
        /// (duplicate AudioListener or EventSystem instances, missing MainCamera tag).
        /// NOTE: This is a costly operation that traverses hierarchies and serialized properties across all objects in scope;
        /// run it on the smallest appropriate subset (a specific GameObject root, prefab asset path, or narrow subfolder) rather than the entire project.
        /// </summary>
        public static string Audit(string targetOrAssetPath = null)
        {
            if (string.IsNullOrWhiteSpace(targetOrAssetPath))
            {
                return AuditActiveEnvironment();
            }

            string clean = NormalizeAuditTarget(targetOrAssetPath);

            // 1. Check active Prefab Stage match
            PrefabStage stage = GetCurrentPrefabStageSafe();
            string cleanNameWithoutExt = Path.GetFileNameWithoutExtension(clean);
            if (stage != null && MatchesPrefabStageQuery(stage, clean, cleanNameWithoutExt))
            {
                var report = new AuditReport($"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\"");
                GameObject stageRoot = null;
                try { stageRoot = stage.prefabContentsRoot; } catch { }
                if (stageRoot == null)
                {
                    return $"Audit of Prefab Stage \"{GetPrefabStageAssetPath(stage)}\": (No root GameObject in Prefab Stage)";
                }

                AuditHierarchyRoots(new[] { stageRoot }, $"prefabStage: \"{GetPrefabStageAssetPath(stage)}\"", isSceneScope: false, report);
                return FormatAuditReport(report);
            }

            // 2. Check loaded scenes match
            int sceneCount = GetSceneCountSafe();
            for (int i = 0; i < sceneCount; i++)
            {
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

                    var report = new AuditReport($"Scene \"{s.name}\"");
                    GameObject[] roots = GetRootGameObjectsSafe(s);
                    AuditHierarchyRoots(roots, $"scene: \"{s.name}\"", isSceneScope: true, report);
                    return FormatAuditReport(report);
                }
            }

            // 3. Check folder path
            if (IsProjectFolder(clean))
            {
                return AuditFolderPrefabs(clean);
            }

            // 4. Check prefab asset path or search query
            if (TryResolvePrefabAssetPath(clean, targetOrAssetPath, out string resolvedPrefabPath, out string resolveError))
            {
                var report = new AuditReport($"Prefab Asset \"{resolvedPrefabPath}\"");
                if (!TryAuditPrefabAsset(resolvedPrefabPath, report, out string loadError))
                {
                    return loadError;
                }
                return FormatAuditReport(report);
            }

            return resolveError;
        }

        /// <summary>
        /// Performs a comprehensive, non-destructive integrity audit starting from a specific root GameObject and its hierarchy.
        /// Reports missing scripts, broken serialized ObjectReference fields, broken UnityEvent persistent listeners,
        /// error/missing shaders, and duplicate AudioListener or EventSystem instances within the subtree.
        /// NOTE: This is a costly operation; run it on the smallest appropriate GameObject subtree.
        /// </summary>
        public static string Audit(GameObject root)
        {
            if (ReferenceEquals(root, null)) return "(GameObject is null)";
            if (root == null) return "(GameObject is destroyed)";
            if (root.transform == null) return "(GameObject has no Transform)";

            string goName = "GameObject";
            try { goName = root.name; } catch { }

            string containerLabel = ResolveGameObjectContainerLabel(root);
            var report = new AuditReport($"GameObject \"{goName}\" ({containerLabel})");
            AuditHierarchyRoots(new[] { root }, containerLabel, isSceneScope: false, report);
            return FormatAuditReport(report);
        }

        private static string AuditActiveEnvironment()
        {
            PrefabStage stage = GetCurrentPrefabStageSafe();
            int sceneCount = GetSceneCountSafe();
            if (stage == null && sceneCount == 0)
            {
                return "(No scenes currently loaded)";
            }

            string scopeDescription = stage != null && sceneCount > 0
                ? $"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\" and {sceneCount} loaded scene(s)"
                : (stage != null ? $"Prefab Stage \"{GetPrefabStageAssetPath(stage)}\"" : $"{sceneCount} loaded scene(s)");

            var report = new AuditReport(scopeDescription);
            bool anyAudited = false;

            if (stage != null)
            {
                GameObject stageRoot = null;
                try { stageRoot = stage.prefabContentsRoot; } catch { }
                if (stageRoot != null)
                {
                    anyAudited = true;
                    string stagePath = GetPrefabStageAssetPath(stage);
                    AuditHierarchyRoots(new[] { stageRoot }, $"prefabStage: \"{stagePath}\"", isSceneScope: false, report);
                }
            }

            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch { continue; }

                if (!scene.isLoaded) continue;

                anyAudited = true;
                GameObject[] roots = GetRootGameObjectsSafe(scene);
                AuditHierarchyRoots(roots, $"scene: \"{scene.name}\"", isSceneScope: true, report);
            }

            if (!anyAudited)
            {
                return stage != null
                    ? "(No GameObjects found in Prefab Stage or loaded scenes)"
                    : "(No scenes currently loaded)";
            }

            return FormatAuditReport(report);
        }

        private static string AuditFolderPrefabs(string folder)
        {
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
            var report = new AuditReport($"Folder \"{folder}\" ({prefabPaths.Count} prefab(s))");

            for (int i = 0; i < prefabPaths.Count; i++)
            {
                string prefabPath = prefabPaths[i];
                if (!TryAuditPrefabAsset(prefabPath, report, out string loadError))
                {
                    report.BrokenReferences.Add($"  • (prefab: \"{prefabPath}\"): {loadError}");
                }
            }

            return FormatAuditReport(report);
        }

        private static bool TryAuditPrefabAsset(string prefabPath, AuditReport report, out string error)
        {
            error = null;

            PrefabStage stage = GetCurrentPrefabStageSafe();
            if (stage != null)
            {
                string stagePath = GetPrefabStageAssetPath(stage);
                if (string.Equals(stagePath, prefabPath, StringComparison.OrdinalIgnoreCase) && stage.prefabContentsRoot != null)
                {
                    AuditHierarchyRoots(new[] { stage.prefabContentsRoot }, $"prefab: \"{prefabPath}\"", isSceneScope: false, report);
                    return true;
                }
            }

            GameObject contentsRoot = null;
            bool loadedViaPrefabContents = false;
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

                AuditHierarchyRoots(new[] { contentsRoot }, $"prefab: \"{prefabPath}\"", isSceneScope: false, report);
                return true;
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
            AuditReport report)
        {
            var audioListenerPaths = new List<string>();
            var eventSystemPaths = new List<string>();
            int cameraCount = 0;
            int mainCameraTagCount = 0;

            if (rootObjects != null && rootObjects.Length > 0)
            {
                TraverseRootGameObjectsBfs(rootObjects, (current, go) =>
                {
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
                        report.MissingScripts.Add(
                            $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {missingIndices.Count} {scriptWord} at component index [{indicesStr}]");
                    }

                    return SceneBfsDecision.Continue;
                });
            }

            if (audioListenerPaths.Count > 1)
            {
                report.SceneHygieneIssues.Add(
                    $"  • Duplicate active AudioListener components ({audioListenerPaths.Count}) in {containerLabel}: [{string.Join(", ", audioListenerPaths)}]");
            }

            if (eventSystemPaths.Count > 1)
            {
                report.SceneHygieneIssues.Add(
                    $"  • Duplicate active EventSystem components ({eventSystemPaths.Count}) in {containerLabel}: [{string.Join(", ", eventSystemPaths)}]");
            }

            if (isSceneScope && mainCameraTagCount == 0)
            {
                string camDetail = cameraCount > 0
                    ? $"{cameraCount} untagged Camera component(s) present"
                    : "no Camera components present";
                report.SceneHygieneIssues.Add(
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
                report.MaterialAndShaderIssues.Add(
                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {accessorPath} (\"{matName}\") has missing/null Shader");
                return;
            }

            string shaderName = string.Empty;
            try { shaderName = shader.name ?? string.Empty; } catch { }

            if (string.Equals(shaderName, "Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase))
            {
                report.MaterialAndShaderIssues.Add(
                    $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {accessorPath} (\"{matName}\") uses error shader \"Hidden/InternalErrorShader\"");
                return;
            }

            bool hasShaderError = false;
            try { hasShaderError = ShaderUtil.ShaderHasError(shader); } catch { }
            if (hasShaderError)
            {
                report.MaterialAndShaderIssues.Add(
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
                                report.BrokenReferences.Add(
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
                    report.BrokenUnityEvents.Add(
                        $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] has {reason}{methodDetail}");
                    continue;
                }

                if (string.IsNullOrEmpty(methodName))
                {
                    report.BrokenUnityEvents.Add(
                        $"  • {goName} (path: \"{hierarchyPath}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] on target \"{targetObj.name}\" ({targetObj.GetType().Name}) has no method assigned");
                    continue;
                }

                Type targetType = targetObj.GetType();
                if (!HasMatchingPersistentListenerMethod(targetType, methodName, modeProp, argumentsProp))
                {
                    report.BrokenUnityEvents.Add(
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
                return LimitOutput(sb.ToString().TrimEnd());
            }

            sb.AppendLine($"Audit of {report.ScopeDescription}: {totalIssues} issue(s) found ({report.GameObjectsScanned} GameObject(s) scanned):");

            AppendAuditSection(sb, "Missing Scripts", report.MissingScripts);
            AppendAuditSection(sb, "Broken Object References", report.BrokenReferences);
            AppendAuditSection(sb, "Broken UnityEvent Listeners", report.BrokenUnityEvents);
            AppendAuditSection(sb, "Material & Shader Errors", report.MaterialAndShaderIssues);
            AppendAuditSection(sb, "Scene Hygiene Issues", report.SceneHygieneIssues);

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void AppendAuditSection(StringBuilder sb, string title, List<string> items)
        {
            if (items == null || items.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine($"{title} ({items.Count}):");
            for (int i = 0; i < items.Count; i++)
            {
                sb.AppendLine(items[i]);
            }
        }

        private sealed class AuditReport
        {
            public string ScopeDescription { get; }
            public int GameObjectsScanned { get; set; }
            public List<string> MissingScripts { get; } = new List<string>();
            public List<string> BrokenReferences { get; } = new List<string>();
            public List<string> BrokenUnityEvents { get; } = new List<string>();
            public List<string> MaterialAndShaderIssues { get; } = new List<string>();
            public List<string> SceneHygieneIssues { get; } = new List<string>();

            public int TotalIssues =>
                MissingScripts.Count +
                BrokenReferences.Count +
                BrokenUnityEvents.Count +
                MaterialAndShaderIssues.Count +
                SceneHygieneIssues.Count;

            public AuditReport(string scopeDescription)
            {
                ScopeDescription = scopeDescription;
            }
        }

        #endregion
    }
}
