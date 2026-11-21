#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AssembliesType = UnityEditor.Compilation.AssembliesType;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;

namespace UnityLeanMcp
{
    internal static class TestDiscoveryHelper
    {
        private const string DefaultUncategorizedName = "Uncategorized";

        internal class DiscoveredItem
        {
            public string FullName { get; set; } = string.Empty;
            public string FixtureFullName { get; set; } = string.Empty;
            public string MethodFullName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string AssemblyName { get; set; } = string.Empty;
            public List<string> Categories { get; set; } = new List<string>();
            public string Mode { get; set; } = "editmode";
        }

        internal interface ITestTreeNode
        {
            string Name { get; }
            string FullName { get; }
            bool IsSuite { get; }
            bool HasChildren { get; }
            bool IsFixtureSuite { get; }
            Type FixtureType { get; }
            MethodInfo MethodInfo { get; }
            IReadOnlyList<string> Categories { get; }
            IEnumerable<ITestTreeNode> Children { get; }
        }

        internal static Func<Assembly, ITestTreeNode> NUnitTreeBuilderOverride;

        public static List<DiscoveredItem> DiscoverAllTests()
        {
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return new List<DiscoveredItem>();
            }

            var assemblyModes = BuildCompilationPipelineModeMap();
            return DiscoverAllTests(assemblies, assemblyModes);
        }

        internal static List<DiscoveredItem> DiscoverAllTests(IEnumerable<Assembly> assemblies)
        {
            return DiscoverAllTests(assemblies, assemblyModeOverrides: null);
        }

        internal static List<DiscoveredItem> DiscoverAllTests(
            IEnumerable<Assembly> assemblies,
            IReadOnlyDictionary<string, string> assemblyModeOverrides)
        {
            var discovered = new List<DiscoveredItem>();
            if (assemblies == null) return discovered;

            var treeBuilder = NUnitTreeBuilderOverride ?? TryBuildNUnitTree;

            foreach (var asm in assemblies)
            {
                if (asm == null || asm.IsDynamic) continue;

                string asmName;
                string defaultAsmMode;
                AssemblyName[] referencedAssemblies;
                try
                {
                    asmName = StripDllExtension(asm.GetName().Name ?? string.Empty);
                    if (IsSystemOrFrameworkAssembly(asmName))
                    {
                        continue;
                    }

                    referencedAssemblies = asm.GetReferencedAssemblies();
                    if (assemblyModeOverrides != null &&
                        assemblyModeOverrides.TryGetValue(asmName, out var overrideMode) &&
                        !string.IsNullOrWhiteSpace(overrideMode))
                    {
                        defaultAsmMode = overrideMode;
                    }
                    else
                    {
                        bool isEditor = asmName.Contains("Editor") ||
                            referencedAssemblies.Any(r => string.Equals(r.Name, "UnityEditor", StringComparison.OrdinalIgnoreCase));
                        defaultAsmMode = isEditor ? "editmode" : "playmode";
                    }
                }
                catch
                {
                    continue;
                }

                if (ShouldUseNUnitTreeBuilder(asmName, referencedAssemblies, treeBuilder != TryBuildNUnitTree))
                {
                    try
                    {
                        var rootNode = treeBuilder(asm);
                        if (rootNode != null)
                        {
                            CollectTestsFromTree(
                                rootNode,
                                asmName,
                                defaultAsmMode,
                                currentFixtureFullName: string.Empty,
                                inheritedCategories: Array.Empty<string>(),
                                discovered);
                            continue;
                        }
                    }
                    catch
                    {
                        // Fall back to reflection discovery for this assembly.
                    }
                }

                DiscoverAssemblyByReflection(asm, asmName, defaultAsmMode, discovered);
            }

            return discovered;
        }

        internal static Dictionary<string, string> BuildCompilationPipelineModeMap()
        {
            var modeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                if (playerAssemblies != null)
                {
                    for (int i = 0; i < playerAssemblies.Length; i++)
                    {
                        string name = StripDllExtension(playerAssemblies[i]?.name);
                        if (!string.IsNullOrEmpty(name))
                        {
                            modeMap[name] = "playmode";
                        }
                    }
                }

                var editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                if (editorAssemblies != null)
                {
                    for (int i = 0; i < editorAssemblies.Length; i++)
                    {
                        string name = StripDllExtension(editorAssemblies[i]?.name);
                        if (!string.IsNullOrEmpty(name) && !modeMap.ContainsKey(name))
                        {
                            modeMap[name] = "editmode";
                        }
                    }
                }
            }
            catch
            {
                // Off main thread or CompilationPipeline unavailable: fall back to heuristic.
            }

            return modeMap;
        }

        private static bool IsSystemOrFrameworkAssembly(string asmName)
        {
            return asmName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) ||
                asmName.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asmName, "nunit.framework", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUseNUnitTreeBuilder(
            string asmName,
            AssemblyName[] referencedAssemblies,
            bool hasOverride)
        {
            if (hasOverride)
            {
                return true;
            }

            if (referencedAssemblies == null) return false;
            for (int i = 0; i < referencedAssemblies.Length; i++)
            {
                string refName = referencedAssemblies[i]?.Name;
                if (string.Equals(refName, "nunit.framework", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refName, "UnityEngine.TestRunner", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CollectTestsFromTree(
            ITestTreeNode node,
            string asmName,
            string currentMode,
            string currentFixtureFullName,
            IReadOnlyList<string> inheritedCategories,
            List<DiscoveredItem> discovered)
        {
            if (node == null) return;

            string nextMode = currentMode;
            if (node.FixtureType != null)
            {
                nextMode = ApplyUnityPlatformOverride(SafeGetCustomAttributes(node.FixtureType), nextMode);
            }

            if (node.MethodInfo != null)
            {
                nextMode = ApplyUnityPlatformOverride(SafeGetCustomAttributes(node.MethodInfo), nextMode);
            }

            var mergedCategories = MergeCategories(inheritedCategories, node.Categories);

            if (node.IsSuite)
            {
                string nextFixtureFullName = currentFixtureFullName;
                if (node.IsFixtureSuite && !string.IsNullOrEmpty(node.FullName))
                {
                    nextFixtureFullName = node.FullName;
                }

                var children = node.Children;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        CollectTestsFromTree(
                            child,
                            asmName,
                            nextMode,
                            nextFixtureFullName,
                            mergedCategories,
                            discovered);
                    }
                }

                return;
            }

            if (node.HasChildren)
            {
                var children = node.Children;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        CollectTestsFromTree(
                            child,
                            asmName,
                            nextMode,
                            currentFixtureFullName,
                            mergedCategories,
                            discovered);
                    }
                }

                return;
            }

            string fullName = node.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return;
            }

            string fixtureFullName = currentFixtureFullName;
            if (string.IsNullOrEmpty(fixtureFullName))
            {
                fixtureFullName = node.FixtureType?.FullName ?? DeriveFixtureFromFullName(fullName);
            }

            string methodFullName;
            if (!string.IsNullOrEmpty(fixtureFullName) && node.MethodInfo != null)
            {
                methodFullName = fixtureFullName + "." + node.MethodInfo.Name;
            }
            else
            {
                methodFullName = StripParameters(fullName);
            }

            discovered.Add(new DiscoveredItem
            {
                FullName = fullName,
                FixtureFullName = fixtureFullName,
                MethodFullName = methodFullName,
                Name = string.IsNullOrEmpty(node.Name) ? fullName : node.Name,
                AssemblyName = asmName,
                Categories = mergedCategories,
                Mode = nextMode
            });
        }

        private static string DeriveFixtureFromFullName(string fullName)
        {
            string withoutParams = StripParameters(fullName);
            int lastDot = withoutParams.LastIndexOf('.');
            return lastDot > 0 ? withoutParams.Substring(0, lastDot) : string.Empty;
        }

        private static string StripParameters(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return string.Empty;
            int parenIdx = fullName.IndexOf('(');
            return parenIdx > 0 ? fullName.Substring(0, parenIdx) : fullName;
        }

        private static string StripDllExtension(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return string.Empty;
            }

            string trimmed = assemblyName.Trim();
            return trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(0, trimmed.Length - 4)
                : trimmed;
        }

        private static List<string> MergeCategories(IReadOnlyList<string> inherited, IReadOnlyList<string> own)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddFilteredCategories(inherited, merged, seen);
            AddFilteredCategories(own, merged, seen);

            return merged;
        }

        private static void AddFilteredCategories(
            IReadOnlyList<string> source,
            List<string> target,
            HashSet<string> seen)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                string cat = source[i]?.Trim();
                if (!string.IsNullOrEmpty(cat) &&
                    !string.Equals(cat, DefaultUncategorizedName, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(cat))
                {
                    target.Add(cat);
                }
            }
        }

        private static string ApplyUnityPlatformOverride(object[] attributes, string currentMode)
        {
            if (attributes == null) return currentMode;
            string mode = currentMode;
            for (int i = 0; i < attributes.Length; i++)
            {
                var attr = attributes[i];
                if (attr != null && attr.GetType().Name == "UnityPlatformAttribute")
                {
                    string platformStr = attr.ToString();
                    if (platformStr.IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0)
                        mode = "playmode";
                    else if (platformStr.IndexOf("EditMode", StringComparison.OrdinalIgnoreCase) >= 0)
                        mode = "editmode";
                }
            }
            return mode;
        }

        private static void DiscoverAssemblyByReflection(
            Assembly asm,
            string asmName,
            string defaultAsmMode,
            List<DiscoveredItem> discovered)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types != null ? ex.Types.Where(t => t != null).Select(t => t!).ToArray() : null;
            }
            catch
            {
                return;
            }

            if (types == null) return;

            foreach (var t in types)
            {
                if (t == null || !t.IsClass || t.IsAbstract) continue;

                var classAttrs = SafeGetCustomAttributes(t);
                string fixtureMode = ApplyUnityPlatformOverride(classAttrs, defaultAsmMode);
                var fixtureCategories = ExtractCategories(classAttrs);

                MethodInfo[] methods;
                try
                {
                    methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                if (methods == null) continue;

                foreach (var m in methods)
                {
                    if (m == null) continue;

                    var methodAttrs = SafeGetCustomAttributes(m);
                    if (methodAttrs == null || methodAttrs.Length == 0) continue;

                    bool isTest = false;
                    foreach (var attr in methodAttrs)
                    {
                        if (attr == null) continue;
                        string attrName = attr.GetType().Name;
                        if (attrName == "TestAttribute" ||
                            attrName == "UnityTestAttribute" ||
                            attrName == "TestCaseAttribute" ||
                            attrName == "TestCaseSourceAttribute" ||
                            attrName == "TheoryAttribute" ||
                            attrName == "FactAttribute")
                        {
                            isTest = true;
                            break;
                        }
                    }

                    if (!isTest) continue;

                    string methodMode = ApplyUnityPlatformOverride(methodAttrs, fixtureMode);
                    var categories = MergeCategories(fixtureCategories, ExtractCategories(methodAttrs));
                    string fullName = $"{t.FullName}.{m.Name}";

                    discovered.Add(new DiscoveredItem
                    {
                        FullName = fullName,
                        FixtureFullName = t.FullName ?? string.Empty,
                        MethodFullName = fullName,
                        Name = m.Name,
                        AssemblyName = asmName,
                        Categories = categories,
                        Mode = methodMode
                    });
                }
            }
        }

        private sealed class NUnitReflectionMetadata
        {
            public Type BuilderType;
            public MethodInfo BuildMethod;
            public PropertyInfo TestNameProp;
            public PropertyInfo TestFullNameProp;
            public PropertyInfo TestIsSuiteProp;
            public PropertyInfo TestHasChildrenProp;
            public PropertyInfo TestTestsProp;
            public PropertyInfo TestTypeInfoProp;
            public PropertyInfo TestMethodProp;
            public PropertyInfo TestPropertiesProp;
            public PropertyInfo TypeInfoTypeProp;
            public PropertyInfo MethodInfoProp;
            public PropertyInfo PropertyBagIndexer;
        }

        private static readonly object s_NUnitMetadataLock = new object();
        private static bool s_NUnitMetadataInitialized;
        private static NUnitReflectionMetadata s_NUnitMetadata;

        private static NUnitReflectionMetadata GetNUnitMetadata()
        {
            lock (s_NUnitMetadataLock)
            {
                if (s_NUnitMetadataInitialized)
                {
                    return s_NUnitMetadata;
                }

                s_NUnitMetadataInitialized = true;
                try
                {
                    Assembly nunitAsm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a != null && !a.IsDynamic &&
                            string.Equals(a.GetName().Name, "nunit.framework", StringComparison.OrdinalIgnoreCase));

                    if (nunitAsm == null)
                    {
                        return null;
                    }

                    var builderType = nunitAsm.GetType("NUnit.Framework.Api.DefaultTestAssemblyBuilder", throwOnError: false);
                    var iTestType = nunitAsm.GetType("NUnit.Framework.Interfaces.ITest", throwOnError: false);
                    var iTypeInfoType = nunitAsm.GetType("NUnit.Framework.Interfaces.ITypeInfo", throwOnError: false);
                    var iMethodInfoType = nunitAsm.GetType("NUnit.Framework.Interfaces.IMethodInfo", throwOnError: false);
                    var iPropertyBagType = nunitAsm.GetType("NUnit.Framework.Interfaces.IPropertyBag", throwOnError: false);

                    if (builderType == null || iTestType == null || iTypeInfoType == null || iMethodInfoType == null || iPropertyBagType == null)
                    {
                        return null;
                    }

                    var buildMethod = builderType.GetMethod("Build", new[] { typeof(Assembly), typeof(IDictionary<string, object>) });
                    if (buildMethod == null)
                    {
                        return null;
                    }

                    var meta = new NUnitReflectionMetadata
                    {
                        BuilderType = builderType,
                        BuildMethod = buildMethod,
                        TestNameProp = iTestType.GetProperty("Name"),
                        TestFullNameProp = iTestType.GetProperty("FullName"),
                        TestIsSuiteProp = iTestType.GetProperty("IsSuite"),
                        TestHasChildrenProp = iTestType.GetProperty("HasChildren"),
                        TestTestsProp = iTestType.GetProperty("Tests"),
                        TestTypeInfoProp = iTestType.GetProperty("TypeInfo"),
                        TestMethodProp = iTestType.GetProperty("Method"),
                        TestPropertiesProp = iTestType.GetProperty("Properties"),
                        TypeInfoTypeProp = iTypeInfoType.GetProperty("Type"),
                        MethodInfoProp = iMethodInfoType.GetProperty("MethodInfo"),
                        PropertyBagIndexer = iPropertyBagType.GetProperty("Item", new[] { typeof(string) })
                    };

                    if (meta.TestNameProp == null ||
                        meta.TestFullNameProp == null ||
                        meta.TestIsSuiteProp == null ||
                        meta.TestHasChildrenProp == null ||
                        meta.TestTestsProp == null ||
                        meta.TestTypeInfoProp == null ||
                        meta.TestMethodProp == null ||
                        meta.TestPropertiesProp == null ||
                        meta.TypeInfoTypeProp == null ||
                        meta.MethodInfoProp == null ||
                        meta.PropertyBagIndexer == null)
                    {
                        return null;
                    }

                    s_NUnitMetadata = meta;
                    return s_NUnitMetadata;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static ITestTreeNode TryBuildNUnitTree(Assembly asm)
        {
            var meta = GetNUnitMetadata();
            if (meta == null) return null;

            object builder = Activator.CreateInstance(meta.BuilderType);
            if (builder == null) return null;

            object rootTest = meta.BuildMethod.Invoke(builder, new object[] { asm, new Dictionary<string, object>() });
            return rootTest != null ? new NUnitTestTreeNode(rootTest, meta) : null;
        }

        private sealed class NUnitTestTreeNode : ITestTreeNode
        {
            private readonly object _test;
            private readonly NUnitReflectionMetadata _meta;

            public NUnitTestTreeNode(object test, NUnitReflectionMetadata meta)
            {
                _test = test;
                _meta = meta;
            }

            public string Name => _meta.TestNameProp.GetValue(_test) as string ?? string.Empty;
            public string FullName => _meta.TestFullNameProp.GetValue(_test) as string ?? string.Empty;
            public bool IsSuite => _meta.TestIsSuiteProp.GetValue(_test) is bool b && b;
            public bool HasChildren => _meta.TestHasChildrenProp.GetValue(_test) is bool b && b;

            private object RawTypeInfo => _meta.TestTypeInfoProp.GetValue(_test);
            private object RawMethod => _meta.TestMethodProp.GetValue(_test);

            public bool IsFixtureSuite => IsSuite && RawTypeInfo != null && RawMethod == null;

            public Type FixtureType
            {
                get
                {
                    var ti = RawTypeInfo;
                    return ti != null ? _meta.TypeInfoTypeProp.GetValue(ti) as Type : null;
                }
            }

            public MethodInfo MethodInfo
            {
                get
                {
                    var mi = RawMethod;
                    return mi != null ? _meta.MethodInfoProp.GetValue(mi) as MethodInfo : null;
                }
            }

            public IReadOnlyList<string> Categories
            {
                get
                {
                    var bag = _meta.TestPropertiesProp.GetValue(_test);
                    if (bag == null) return Array.Empty<string>();
                    if (_meta.PropertyBagIndexer.GetValue(bag, new object[] { "Category" }) is IList rawList && rawList.Count > 0)
                    {
                        var list = new List<string>(rawList.Count);
                        for (int i = 0; i < rawList.Count; i++)
                        {
                            if (rawList[i] is string cat && !string.IsNullOrWhiteSpace(cat))
                            {
                                list.Add(cat);
                            }
                        }
                        return list;
                    }
                    return Array.Empty<string>();
                }
            }

            public IEnumerable<ITestTreeNode> Children
            {
                get
                {
                    if (!(_meta.TestTestsProp.GetValue(_test) is IEnumerable tests))
                    {
                        yield break;
                    }

                    foreach (var child in tests)
                    {
                        if (child != null)
                        {
                            yield return new NUnitTestTreeNode(child, _meta);
                        }
                    }
                }
            }
        }

        private static object[] SafeGetCustomAttributes(MemberInfo member)
        {
            try
            {
                return member.GetCustomAttributes(inherit: false);
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        private static List<string> ExtractCategories(object[] attributes)
        {
            var categories = new List<string>();
            if (attributes == null) return categories;

            foreach (var attr in attributes)
            {
                if (attr == null) continue;
                var attrType = attr.GetType();
                if (attrType.Name == "CategoryAttribute")
                {
                    try
                    {
                        var prop = attrType.GetProperty("Name");
                        if (prop != null)
                        {
                            string val = prop.GetValue(attr) as string;
                            if (!string.IsNullOrEmpty(val))
                                categories.Add(val);
                        }
                    }
                    catch { }
                }
            }
            return categories;
        }

        public static void DiscoverHints(
            string currentMode,
            string[] testNames,
            string[] groupNames,
            string[] categoryNames,
            string[] assemblyNames,
            out List<string> otherModeMatches,
            out List<string> nearMatches,
            List<DiscoveredItem> preloadedItems = null)
        {
            otherModeMatches = new List<string>();
            nearMatches = new List<string>();

            currentMode = (currentMode ?? "editmode").ToLowerInvariant();
            string otherMode = currentMode == "editmode" ? "playmode" : "editmode";

            var items = preloadedItems ?? DiscoverAllTests();
            if (items == null || items.Count == 0) return;

            // 1. Check if filters match tests in the opposite mode
            var otherModeItems = items.Where(i => string.Equals(i.Mode, otherMode, StringComparison.OrdinalIgnoreCase)).ToList();
            var matchedInOtherMode = otherModeItems
                .Where(item => MatchesFilters(item, testNames, groupNames, categoryNames, assemblyNames))
                .Select(item => item.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            if (matchedInOtherMode.Count > 0)
            {
                otherModeMatches = matchedInOtherMode;
            }

            // 2. Check for close / substring / near matches in current mode
            var currentModeItems = items.Where(i => string.Equals(i.Mode, currentMode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (currentModeItems.Count == 0 && otherModeMatches.Count == 0)
            {
                currentModeItems = items;
            }

            var candidates = new List<string>();

            if (testNames != null && testNames.Length > 0)
            {
                foreach (var tName in testNames)
                {
                    if (string.IsNullOrWhiteSpace(tName)) continue;
                    string clean = tName.Trim();

                    foreach (var item in currentModeItems)
                    {
                        if (item.FullName.EndsWith("." + clean, StringComparison.OrdinalIgnoreCase) ||
                            item.FullName.IndexOf(clean, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.FixtureFullName.IndexOf(clean, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            candidates.Add(item.FullName);
                        }
                    }
                }
            }
            else if (groupNames != null && groupNames.Length > 0)
            {
                foreach (var group in groupNames)
                {
                    if (string.IsNullOrWhiteSpace(group)) continue;
                    string core = group.Trim('^', '$', '.', '*');
                    if (string.IsNullOrWhiteSpace(core)) continue;

                    foreach (var item in currentModeItems)
                    {
                        if (item.FullName.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.FixtureFullName.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            candidates.Add(item.FullName);
                        }
                    }
                }
            }

            nearMatches = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        }

        private static bool MatchesFilters(
            DiscoveredItem item,
            string[] testNames,
            string[] groupNames,
            string[] categoryNames,
            string[] assemblyNames)
        {
            if (assemblyNames != null && assemblyNames.Length > 0)
            {
                string normalizedItemAsm = StripDllExtension(item.AssemblyName);
                bool asmMatch = assemblyNames.Any(asm =>
                    !string.IsNullOrWhiteSpace(asm) &&
                    string.Equals(asm.Trim(), normalizedItemAsm, StringComparison.OrdinalIgnoreCase));
                if (!asmMatch) return false;
            }

            if (categoryNames != null && categoryNames.Length > 0)
            {
                var itemCats = item.Categories;
                var positiveFilters = new List<string>();

                foreach (var cat in categoryNames)
                {
                    if (string.IsNullOrWhiteSpace(cat)) continue;
                    string trimmed = cat.Trim();
                    if (trimmed.StartsWith("!"))
                    {
                        string negated = trimmed.Substring(1);
                        if (MatchesCategoryFilter(itemCats, negated))
                            return false;
                    }
                    else
                    {
                        positiveFilters.Add(trimmed);
                    }
                }

                if (positiveFilters.Count > 0 && !positiveFilters.Any(pos => MatchesCategoryFilter(itemCats, pos)))
                {
                    return false;
                }
            }

            bool hasNameFilter = (testNames != null && testNames.Length > 0);
            bool hasGroupFilter = (groupNames != null && groupNames.Length > 0);

            if (!hasNameFilter && !hasGroupFilter)
            {
                return true;
            }

            if (hasNameFilter)
            {
                if (testNames.Any(t => string.Equals(t, item.FullName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            if (hasGroupFilter)
            {
                foreach (var g in groupNames)
                {
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    try
                    {
                        if (Regex.IsMatch(item.FullName, g) ||
                            (!string.IsNullOrEmpty(item.FixtureFullName) && Regex.IsMatch(item.FixtureFullName, g)) ||
                            (!string.IsNullOrEmpty(item.MethodFullName) && Regex.IsMatch(item.MethodFullName, g)))
                            return true;
                    }
                    catch { }
                }
            }

            return false;
        }

        private static bool MatchesCategoryFilter(IReadOnlyList<string> itemCategories, string pattern)
        {
            if (itemCategories == null || itemCategories.Count == 0 || string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            for (int i = 0; i < itemCategories.Count; i++)
            {
                string cat = itemCategories[i];
                if (string.IsNullOrEmpty(cat)) continue;

                if (string.Equals(cat, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                try
                {
                    if (Regex.IsMatch(cat, pattern, RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
    }
}
