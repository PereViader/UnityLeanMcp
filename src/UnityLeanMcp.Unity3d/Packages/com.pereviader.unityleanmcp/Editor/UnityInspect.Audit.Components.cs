#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private static readonly Dictionary<Type, PropertyInfo> s_SharedMaterialPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> s_GraphicMaterialPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> s_FontSharedMaterialPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> s_FontSharedMaterialsPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<string, Type> s_PersistentArgTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        private static void AuditComponentMaterialsAndShaders(
            Component comp,
            Type compType,
            ref LazyAuditTarget target,
            string containerLabel,
            AuditReport report,
            List<Material> reusableMaterials)
        {
            if (comp is Renderer renderer)
            {
                reusableMaterials.Clear();
                try { renderer.GetSharedMaterials(reusableMaterials); } catch { }
                for (int m = 0; m < reusableMaterials.Count; m++)
                {
                    var mat = reusableMaterials[m];
                    if (ReferenceEquals(mat, null) || mat == null) continue;
                    AuditMaterialShader(
                        mat,
                        FormattableString.Invariant($"{compType.Name}.sharedMaterials[{m}]"),
                        ref target,
                        containerLabel,
                        report);
                }
                return;
            }

            HashSet<Material> auditedMaterials = null;

            // Safely inspect sharedMaterial on non-Renderer components that expose it without touching arbitrary .material
            AuditSingleMaterialProperty(comp, compType, s_SharedMaterialPropCache, "sharedMaterial", ref target, containerLabel, report, ref auditedMaterials);

            // TMP_Text inherits from Graphic; inspect fontSharedMaterial/fontSharedMaterials first so they take precedence over Graphic.material during deduplication
            if (IsComponentOfFullTypeName(compType, "TMPro.TMP_Text"))
            {
                AuditSingleMaterialProperty(comp, compType, s_FontSharedMaterialPropCache, "fontSharedMaterial", ref target, containerLabel, report, ref auditedMaterials);
                AuditMaterialArrayProperty(comp, compType, s_FontSharedMaterialsPropCache, "fontSharedMaterials", ref target, containerLabel, report, ref auditedMaterials);
            }

            // UnityEngine.UI.Graphic.material is a non-instantiating getter (unlike Renderer.material or Collider.material)
            if (IsComponentOfFullTypeName(compType, "UnityEngine.UI.Graphic"))
            {
                AuditSingleMaterialProperty(comp, compType, s_GraphicMaterialPropCache, "material", ref target, containerLabel, report, ref auditedMaterials);
            }
        }

        private static void AuditSingleMaterialProperty(
            Component comp,
            Type compType,
            Dictionary<Type, PropertyInfo> propCache,
            string propertyName,
            ref LazyAuditTarget target,
            string containerLabel,
            AuditReport report,
            ref HashSet<Material> auditedMaterials)
        {
            PropertyInfo prop = GetCachedProperty(propCache, compType, propertyName, typeof(Material));
            if (prop == null) return;

            Material mat = null;
            try { mat = prop.GetValue(comp, null) as Material; } catch { }
            if (!TryMarkMaterialAudited(mat, ref auditedMaterials)) return;

            AuditMaterialShader(mat, $"{compType.Name}.{propertyName}", ref target, containerLabel, report);
        }

        private static void AuditMaterialArrayProperty(
            Component comp,
            Type compType,
            Dictionary<Type, PropertyInfo> propCache,
            string propertyName,
            ref LazyAuditTarget target,
            string containerLabel,
            AuditReport report,
            ref HashSet<Material> auditedMaterials)
        {
            PropertyInfo prop = GetCachedProperty(propCache, compType, propertyName, typeof(Material[]));
            if (prop == null) return;

            Material[] mats = null;
            try { mats = prop.GetValue(comp, null) as Material[]; } catch { }
            if (mats == null) return;

            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (!TryMarkMaterialAudited(mat, ref auditedMaterials)) continue;

                AuditMaterialShader(mat, FormattableString.Invariant($"{compType.Name}.{propertyName}[{m}]"), ref target, containerLabel, report);
            }
        }

        private static bool TryMarkMaterialAudited(Material mat, ref HashSet<Material> auditedMaterials)
        {
            if (ReferenceEquals(mat, null) || mat == null) return false;
            if (auditedMaterials == null)
            {
                auditedMaterials = new HashSet<Material>();
            }
            return auditedMaterials.Add(mat);
        }

        private static void AuditMaterialShader(
            Material mat,
            string accessorPath,
            ref LazyAuditTarget target,
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
                    $"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {accessorPath} (\"{matName}\") has missing/null Shader");
                return;
            }

            string shaderName = string.Empty;
            try { shaderName = shader.name ?? string.Empty; } catch { }

            if (string.Equals(shaderName, "Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase))
            {
                report.TryAddMaterialAndShaderIssue(
                    $"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {accessorPath} (\"{matName}\") uses error shader \"Hidden/InternalErrorShader\"");
                return;
            }

            bool hasShaderError = false;
            try { hasShaderError = ShaderUtil.ShaderHasError(shader); } catch { }
            if (hasShaderError)
            {
                report.TryAddMaterialAndShaderIssue(
                    $"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {accessorPath} (\"{matName}\") shader \"{shaderName}\" has compilation errors");
            }
        }

        private static void AuditComponentSerializedProperties(
            Component comp,
            Type compType,
            ref LazyAuditTarget target,
            string containerLabel,
            AuditReport report)
        {
            if (comp is Transform)
            {
                return;
            }

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

                        if (sp.isArray)
                        {
                            if (sp.arraySize == 0)
                            {
                                enterChildren = false;
                                continue;
                            }

                            SerializedProperty elem0 = null;
                            try { elem0 = sp.GetArrayElementAtIndex(0); } catch { }
                            if (elem0 == null ||
                                (elem0.propertyType != SerializedPropertyType.ObjectReference &&
                                 elem0.propertyType != SerializedPropertyType.Generic &&
                                 elem0.propertyType != SerializedPropertyType.ManagedReference &&
                                 elem0.propertyType != SerializedPropertyType.ExposedReference))
                            {
                                enterChildren = false;
                            }
                            continue;
                        }

                        // Check if this property is a UnityEvent (contains m_PersistentCalls.m_Calls)
                        if (sp.propertyType == SerializedPropertyType.Generic && !sp.isArray)
                        {
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
                                AuditUnityEventCalls(callsProp, compType.Name, propPath, ref target, containerLabel, report);
                                enterChildren = false;
                                continue;
                            }

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
                                    FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {compType.Name}.{propPath} is Missing (instanceID: {instanceId})"));
                            }
                            enterChildren = false;
                            continue;
                        }

                        if (sp.propertyType != SerializedPropertyType.ManagedReference &&
                            sp.propertyType != SerializedPropertyType.ExposedReference)
                        {
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
            ref LazyAuditTarget target,
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
                        ? FormattableString.Invariant($"missing target object (instanceID: {targetInstanceId})")
                        : "null target object";
                    string methodDetail = !string.IsNullOrEmpty(methodName) ? $" (method: \"{methodName}\")" : "";
                    report.TryAddBrokenUnityEvent(
                        FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] has {reason}{methodDetail}"));
                    continue;
                }

                if (string.IsNullOrEmpty(methodName))
                {
                    report.TryAddBrokenUnityEvent(
                        FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] on target \"{targetObj.name}\" ({targetObj.GetType().Name}) has no method assigned"));
                    continue;
                }

                Type targetType = targetObj.GetType();
                if (!HasMatchingPersistentListenerMethod(targetType, methodName, modeProp, argumentsProp))
                {
                    report.TryAddBrokenUnityEvent(
                        FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{i}] method \"{targetType.Name}.{methodName}\" not found on target \"{targetObj.name}\""));
                }

                if (modeProp != null && modeProp.intValue == 2) // PersistentListenerMode.Object
                {
                    AuditUnityEventObjectArgument(
                        callElement,
                        argumentsProp,
                        compTypeName,
                        eventPropertyPath,
                        i,
                        methodName,
                        ref target,
                        containerLabel,
                        report);
                }
            }
        }

        private static void AuditUnityEventObjectArgument(
            SerializedProperty callElement,
            SerializedProperty argumentsProp,
            string compTypeName,
            string eventPropertyPath,
            int callIndex,
            string methodName,
            ref LazyAuditTarget target,
            string containerLabel,
            AuditReport report)
        {
            SerializedProperty objectArgProp = null;
            if (argumentsProp != null)
            {
                objectArgProp = argumentsProp.FindPropertyRelative("m_ObjectArgument");
            }
            if (objectArgProp == null)
            {
                objectArgProp = callElement.FindPropertyRelative("m_Arguments.m_ObjectArgument");
            }
            if (objectArgProp == null)
            {
                return;
            }

            UnityEngine.Object objArg = null;
            int objArgInstanceId = 0;
            try { objArg = objectArgProp.objectReferenceValue; } catch { }
            try { objArgInstanceId = objectArgProp.objectReferenceInstanceIDValue; } catch { }

            if (objArg == null && objArgInstanceId != 0)
            {
                string methodDetail = !string.IsNullOrEmpty(methodName) ? $" (method: \"{methodName}\")" : "";
                report.TryAddBrokenUnityEvent(
                    FormattableString.Invariant($"  • {target.GetName()} (path: \"{target.GetPath()}\", {containerLabel}): {compTypeName}.{eventPropertyPath}[{callIndex}] has missing object argument (instanceID: {objArgInstanceId}){methodDetail}"));
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

        internal static Type ResolvePersistentArgumentType(string assemblyQualifiedTypeName)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedTypeName)) return null;

            lock (s_PersistentArgTypeCache)
            {
                if (s_PersistentArgTypeCache.TryGetValue(assemblyQualifiedTypeName, out Type cached))
                {
                    return cached;
                }
            }

            Type resolved = ResolvePersistentArgumentTypeUncached(assemblyQualifiedTypeName);

            lock (s_PersistentArgTypeCache)
            {
                s_PersistentArgTypeCache[assemblyQualifiedTypeName] = resolved;
            }

            return resolved;
        }

        private static Type ResolvePersistentArgumentTypeUncached(string assemblyQualifiedTypeName)
        {
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

        private static bool IsComponentOfFullTypeName(Type compType, string fullTypeName)
        {
            for (Type cur = compType; cur != null && cur != typeof(UnityEngine.Object) && cur != typeof(object); cur = cur.BaseType)
            {
                if (string.Equals(cur.FullName, fullTypeName, StringComparison.Ordinal))
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
    }
}
