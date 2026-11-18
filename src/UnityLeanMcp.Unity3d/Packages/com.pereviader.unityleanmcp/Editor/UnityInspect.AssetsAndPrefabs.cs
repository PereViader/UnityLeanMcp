#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        #region Asset & Prefab Search

        /// <summary>
        /// Searches project assets by filter, type, or label, returning normalized project-relative paths.
        /// </summary>
        public static string FindAssets(string filter = null, string type = null, string label = null, int maxItems = 50)
        {
            maxItems = Math.Max(1, maxItems);

            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                queryParts.Add(filter.Trim());
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                string cleanType = type.Trim();
                if (!cleanType.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanType = "t:" + cleanType;
                }
                queryParts.Add(cleanType);
            }
            if (!string.IsNullOrWhiteSpace(label))
            {
                string cleanLabel = label.Trim();
                if (!cleanLabel.StartsWith("l:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanLabel = "l:" + cleanLabel;
                }
                queryParts.Add(cleanLabel);
            }

            string searchQuery = string.Join(" ", queryParts);
            string[] guids = null;
            try
            {
                guids = AssetDatabase.FindAssets(searchQuery);
            }
            catch (Exception ex)
            {
                return $"(Asset search failed: {ex.Message})";
            }

            if (guids == null || guids.Length == 0)
            {
                return string.IsNullOrEmpty(searchQuery)
                    ? "(No assets found in project)"
                    : $"(No assets found matching query: '{searchQuery}')";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Assets found ({guids.Length}):");

            int count = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < guids.Length; i++)
            {
                string rawPath = null;
                try { rawPath = AssetDatabase.GUIDToAssetPath(guids[i]); } catch { }
                if (string.IsNullOrEmpty(rawPath)) continue;

                // Normalize path to project-relative with forward slashes
                string normalizedPath = rawPath.Replace('\\', '/');
                if (!seenPaths.Add(normalizedPath)) continue;

                if (count >= maxItems)
                {
                    sb.AppendLine(ItemTruncationMarker);
                    break;
                }

                count++;
                sb.AppendLine($"  {count}. {normalizedPath} [{GetAssetTypeName(normalizedPath)}]");
            }

            if (count == 0)
            {
                return string.IsNullOrEmpty(searchQuery)
                    ? "(No assets found in project)"
                    : $"(No assets found matching query: '{searchQuery}')";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Searches project prefabs by optional name/filter, returning normalized project-relative paths.
        /// </summary>
        public static string FindPrefabs(string filter = null, int maxItems = 50)
        {
            return FindAssets(filter, type: "Prefab", label: null, maxItems: maxItems);
        }

        /// <summary>
        /// Finds a single asset by name or path and returns detailed metadata.
        /// </summary>
        public static string FindAsset(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                return "(Missing asset name or path)";
            }

            try
            {
                string clean = nameOrPath.Trim().Replace('\\', '/');
                string targetPath = clean;

                if (!clean.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    !clean.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    string[] guids = AssetDatabase.FindAssets(clean);
                    string foundPath = FindFirstValidAssetPath(guids);
                    if (string.IsNullOrEmpty(foundPath))
                    {
                        return $"(Asset not found matching: \"{nameOrPath}\")";
                    }
                    targetPath = foundPath;
                }

                var assetObj = AssetDatabase.LoadMainAssetAtPath(targetPath);
                if (assetObj == null)
                {
                    return $"(Could not load asset at path: \"{targetPath}\")";
                }

                var sb = new StringBuilder();
                string assetName = "Unnamed";
                try { assetName = assetObj.name; } catch { }
                sb.AppendLine($"Asset: \"{assetName}\"");
                sb.AppendLine($"  Path: \"{targetPath}\"");
                sb.AppendLine($"  Type: {assetObj.GetType().FullName}");
                sb.AppendLine($"  GUID: {AssetDatabase.AssetPathToGUID(targetPath)}");

                return LimitOutput(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return $"(Asset search failed: {ex.Message})";
            }
        }

        private static string FindFirstValidAssetPath(string[] guids)
        {
            if (guids == null || guids.Length == 0)
            {
                return null;
            }

            for (int g = 0; g < guids.Length; g++)
            {
                string p = null;
                try { p = AssetDatabase.GUIDToAssetPath(guids[g]); } catch { }
                if (!string.IsNullOrEmpty(p))
                {
                    return p.Replace('\\', '/');
                }
            }

            return null;
        }

        private static bool TryResolveAssetPath(string inputPath, string missingDiagnostic, out string targetPath, out string error)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                targetPath = null;
                error = missingDiagnostic;
                return false;
            }

            string clean = inputPath.Trim().Replace('\\', '/');
            while (clean.StartsWith("./", StringComparison.Ordinal))
            {
                clean = clean.Substring(2);
            }
            while (clean.StartsWith("/", StringComparison.Ordinal) && clean.Length > 1)
            {
                clean = clean.TrimStart('/');
            }

            targetPath = clean;
            if (!clean.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !clean.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                string[] guids = null;
                try
                {
                    guids = AssetDatabase.FindAssets(clean);
                }
                catch (Exception ex)
                {
                    error = $"(Asset search failed: {ex.Message})";
                    return false;
                }

                string foundPath = FindFirstValidAssetPath(guids);
                if (string.IsNullOrEmpty(foundPath))
                {
                    error = $"(Asset not found matching: \"{inputPath}\")";
                    return false;
                }

                targetPath = foundPath;
            }

            UnityEngine.Object assetObj = null;
            try { assetObj = AssetDatabase.LoadMainAssetAtPath(targetPath); } catch { }

            string guid = null;
            try { guid = AssetDatabase.AssetPathToGUID(targetPath); } catch { }

            if (assetObj == null && string.IsNullOrEmpty(guid))
            {
                error = $"(Asset not found at path: \"{targetPath}\")";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Wraps AssetDatabase.GetDependencies with normalized paths, asset type annotations,
        /// and strict item/output truncation limits.
        /// </summary>
        public static string Dependencies(string assetPath, bool recursive = false, int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);

            if (!TryResolveAssetPath(assetPath, "(Missing asset path)", out string targetPath, out string error))
            {
                return error;
            }

            string[] rawDeps = null;
            try
            {
                rawDeps = AssetDatabase.GetDependencies(targetPath, recursive);
            }
            catch (Exception ex)
            {
                return $"(Failed to get dependencies: {ex.Message})";
            }

            if (rawDeps == null || rawDeps.Length == 0)
            {
                return $"(No dependencies found for asset: \"{targetPath}\")";
            }

            var depList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(targetPath);

            for (int i = 0; i < rawDeps.Length; i++)
            {
                string raw = rawDeps[i];
                if (string.IsNullOrEmpty(raw)) continue;
                string normalized = raw.Replace('\\', '/');
                if (seen.Add(normalized))
                {
                    depList.Add(normalized);
                }
            }

            if (depList.Count == 0)
            {
                return $"(No dependencies found for asset: \"{targetPath}\")";
            }

            return FormatNumberedAssetList("Dependencies found", depList, maxItems);
        }

        /// <summary>
        /// Queries project assets within searchFolder that depend on targetAssetPath,
        /// returning a compact, token-bounded list of referencing asset paths.
        /// </summary>
        public static string Referencers(string targetAssetPath, string searchFolder = "Assets", int maxItems = DefaultMaxItems)
        {
            maxItems = Math.Max(1, maxItems);

            if (!TryResolveAssetPath(targetAssetPath, "(Missing target asset path)", out string targetPath, out string error))
            {
                return error;
            }

            string folder = string.IsNullOrWhiteSpace(searchFolder) ? "Assets" : searchFolder.Trim().Replace('\\', '/');
            while (folder.StartsWith("./", StringComparison.Ordinal))
            {
                folder = folder.Substring(2);
            }
            while (folder.EndsWith("/", StringComparison.Ordinal) && folder.Length > 1)
            {
                folder = folder.Substring(0, folder.Length - 1);
            }

            bool isValidFolder = false;
            try
            {
                isValidFolder = AssetDatabase.IsValidFolder(folder) ||
                                string.Equals(folder, "Assets", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(folder, "Packages", StringComparison.OrdinalIgnoreCase) ||
                                Directory.Exists(folder);
            }
            catch { }

            if (!isValidFolder)
            {
                return $"(Folder not found: \"{folder}\")";
            }

            string[] guids = null;
            try
            {
                guids = AssetDatabase.FindAssets("", new[] { folder });
            }
            catch (Exception ex)
            {
                return $"(Asset search failed: {ex.Message})";
            }

            if (guids == null || guids.Length == 0)
            {
                return $"(No referencers found for asset: \"{targetPath}\")";
            }

            var referencers = new List<string>();
            var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seenCandidates.Add(targetPath);

            for (int i = 0; i < guids.Length; i++)
            {
                string rawPath = null;
                try { rawPath = AssetDatabase.GUIDToAssetPath(guids[i]); } catch { }
                if (string.IsNullOrEmpty(rawPath)) continue;

                string candidatePath = rawPath.Replace('\\', '/');
                if (!seenCandidates.Add(candidatePath)) continue;

                bool isFolder = false;
                try { isFolder = AssetDatabase.IsValidFolder(candidatePath); } catch { }
                if (isFolder) continue;

                string[] deps = null;
                try { deps = AssetDatabase.GetDependencies(candidatePath, false); } catch { }
                if (deps == null || deps.Length == 0) continue;

                for (int d = 0; d < deps.Length; d++)
                {
                    string dep = deps[d];
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (string.Equals(dep.Replace('\\', '/'), targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        referencers.Add(candidatePath);
                        break;
                    }
                }
            }

            if (referencers.Count == 0)
            {
                return $"(No referencers found for asset: \"{targetPath}\")";
            }

            return FormatNumberedAssetList("Referencers found", referencers, maxItems);
        }

        private static string FormatNumberedAssetList(string heading, List<string> paths, int maxItems)
        {
            paths.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.AppendLine($"{heading} ({paths.Count}):");

            int count = Math.Min(paths.Count, maxItems);
            for (int i = 0; i < count; i++)
            {
                string path = paths[i];
                sb.AppendLine($"  {i + 1}. {path} [{GetAssetTypeName(path)}]");
            }

            if (paths.Count > maxItems)
            {
                sb.AppendLine(ItemTruncationMarker);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static string GetAssetTypeName(string path)
        {
            Type assetType = null;
            try { assetType = AssetDatabase.GetMainAssetTypeAtPath(path); } catch { }
            return assetType != null ? assetType.Name : "Asset";
        }

        /// <summary>
        /// Inspects a prefab instance GameObject and returns its modifications, added components,
        /// and removed components relative to the source prefab asset.
        /// </summary>
        public static string PrefabOverrides(GameObject instance, int maxItems = 50)
        {
            if (ReferenceEquals(instance, null))
            {
                return "(GameObject is null)";
            }
            if (instance == null)
            {
                return "(GameObject is destroyed)";
            }

            bool isPrefab = false;
            try { isPrefab = PrefabUtility.IsPartOfPrefabInstance(instance); } catch { }
            if (!isPrefab)
            {
                return "(GameObject is not a prefab instance)";
            }

            maxItems = Math.Max(1, maxItems);

            string assetPath = ResolvePrefabAssetPath(instance);
            var validAdded = CollectValidAddedComponents(instance);
            var validRemoved = CollectValidRemovedComponents(instance);
            var validMods = CollectValidPropertyModifications(instance);

            var sb = new StringBuilder();
            string goName = "GameObject";
            try { goName = instance.name; } catch { }

            string sourcePath = !string.IsNullOrEmpty(assetPath) ? assetPath : "(unknown source)";
            sb.AppendLine($"Prefab Instance: \"{goName}\" (Source: {sourcePath})");

            int totalOverrides = validAdded.Count + validRemoved.Count + validMods.Count;
            if (totalOverrides == 0)
            {
                sb.AppendLine("  Status: No overrides (clean instance)");
                return LimitOutput(sb.ToString().TrimEnd(), DefaultMaxOutputCharacters, DefaultMaxOutputBytes);
            }

            sb.AppendLine($"  Status: {totalOverrides} override(s) ({validAdded.Count} added, {validRemoved.Count} removed, {validMods.Count} modified)");

            int itemCount = 0;
            bool itemLimitReached = false;

            if (validAdded.Count > 0)
            {
                sb.AppendLine("  Added Components:");
                for (int i = 0; i < validAdded.Count; i++)
                {
                    string compName = GetSafeTypeName(validAdded[i].instanceComponent, "Component");
                    if (AppendOverrideLine(sb, $"    + {compName}", maxItems, totalOverrides, ref itemCount, ref itemLimitReached))
                        break;
                }
            }

            if (!itemLimitReached && validRemoved.Count > 0)
            {
                sb.AppendLine("  Removed Components:");
                for (int i = 0; i < validRemoved.Count; i++)
                {
                    string compName = GetSafeTypeName(validRemoved[i].assetComponent, "Component");
                    if (AppendOverrideLine(sb, $"    - {compName}", maxItems, totalOverrides, ref itemCount, ref itemLimitReached))
                        break;
                }
            }

            if (!itemLimitReached && validMods.Count > 0)
            {
                sb.AppendLine("  Modified Properties:");
                for (int i = 0; i < validMods.Count; i++)
                {
                    var mod = validMods[i];
                    string targetType = GetSafeTypeName(mod.target, "Object");

                    string val = "null";
                    try
                    {
                        if (mod.objectReference != null)
                        {
                            val = $"{mod.objectReference.name} ({mod.objectReference.GetType().Name})";
                        }
                        else if (mod.value != null)
                        {
                            val = mod.value;
                        }
                    }
                    catch { }

                    if (AppendOverrideLine(sb, $"    ~ {targetType}.{mod.propertyPath} = {val}", maxItems, totalOverrides, ref itemCount, ref itemLimitReached))
                        break;
                }
            }

            return LimitOutput(sb.ToString().TrimEnd(), DefaultMaxOutputCharacters, DefaultMaxOutputBytes);
        }

        private static string ResolvePrefabAssetPath(GameObject instance)
        {
            UnityEngine.Object source = null;
            try { source = PrefabUtility.GetCorrespondingObjectFromSource(instance); } catch { }

            string assetPath = null;
            if (source != null)
            {
                try { assetPath = AssetDatabase.GetAssetPath(source); } catch { }
            }
            if (string.IsNullOrEmpty(assetPath))
            {
                try { assetPath = AssetDatabase.GetAssetPath(instance); } catch { }
            }
            return !string.IsNullOrEmpty(assetPath) ? assetPath.Replace('\\', '/') : null;
        }

        private static List<AddedComponent> CollectValidAddedComponents(GameObject instance)
        {
            List<AddedComponent> addedComponents = null;
            try { addedComponents = PrefabUtility.GetAddedComponents(instance); } catch { }

            var valid = new List<AddedComponent>();
            if (addedComponents != null)
            {
                for (int i = 0; i < addedComponents.Count; i++)
                {
                    var added = addedComponents[i];
                    if (added != null && added.instanceComponent != null)
                    {
                        valid.Add(added);
                    }
                }
            }
            return valid;
        }

        private static List<RemovedComponent> CollectValidRemovedComponents(GameObject instance)
        {
            List<RemovedComponent> removedComponents = null;
            try { removedComponents = PrefabUtility.GetRemovedComponents(instance); } catch { }

            var valid = new List<RemovedComponent>();
            if (removedComponents != null)
            {
                for (int i = 0; i < removedComponents.Count; i++)
                {
                    var removed = removedComponents[i];
                    if (removed != null && removed.assetComponent != null)
                    {
                        valid.Add(removed);
                    }
                }
            }
            return valid;
        }

        private static List<PropertyModification> CollectValidPropertyModifications(GameObject instance)
        {
            PropertyModification[] rawMods = null;
            try { rawMods = PrefabUtility.GetPropertyModifications(instance); } catch { }

            var valid = new List<PropertyModification>();
            if (rawMods != null)
            {
                for (int i = 0; i < rawMods.Length; i++)
                {
                    var mod = rawMods[i];
                    if (mod == null || mod.target == null) continue;
                    string path = mod.propertyPath;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path == "m_ObjectHideFlags" || path.StartsWith("m_ObjectHideFlags.")) continue;
                    if (path.StartsWith("m_CorrespondingSourceObject") || path.StartsWith("m_PrefabInstance") || path.StartsWith("m_PrefabAsset")) continue;
                    if (path.StartsWith("<") || path.Contains("$")) continue;
                    valid.Add(mod);
                }
            }
            return valid;
        }

        private static string GetSafeTypeName(UnityEngine.Object obj, string fallback)
        {
            try
            {
                if (obj != null)
                {
                    return obj.GetType().Name;
                }
            }
            catch { }
            return fallback;
        }

        private static bool AppendOverrideLine(
            StringBuilder sb,
            string line,
            int maxItems,
            int totalOverrides,
            ref int itemCount,
            ref bool itemLimitReached)
        {
            sb.AppendLine(line);
            itemCount++;
            if (itemCount >= maxItems && itemCount < totalOverrides)
            {
                sb.AppendLine($"    {ItemTruncationMarker}");
                itemLimitReached = true;
                return true;
            }
            return false;
        }

        #endregion
    }
}
