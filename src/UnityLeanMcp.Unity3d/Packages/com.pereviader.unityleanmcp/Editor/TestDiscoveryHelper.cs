#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UnityLeanMcp
{
    internal static class TestDiscoveryHelper
    {
        internal class DiscoveredItem
        {
            public string FullName { get; set; } = string.Empty;
            public string FixtureFullName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string AssemblyName { get; set; } = string.Empty;
            public List<string> Categories { get; set; } = new List<string>();
            public string Mode { get; set; } = "editmode";
        }

        public static List<DiscoveredItem> DiscoverAllTests()
        {
            var discovered = new List<DiscoveredItem>();
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return discovered;
            }

            if (assemblies == null) return discovered;

            foreach (var asm in assemblies)
            {
                if (asm == null || asm.IsDynamic) continue;

                string asmName = asm.GetName().Name ?? string.Empty;
                if (asmName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    asmName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                    asmName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                    asmName.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) ||
                    asmName.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isEditor = asmName.Contains("Editor") ||
                    asm.GetReferencedAssemblies().Any(r => string.Equals(r.Name, "UnityEditor", StringComparison.OrdinalIgnoreCase));
                string defaultAsmMode = isEditor ? "editmode" : "playmode";

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
                    continue;
                }

                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null || !t.IsClass || t.IsAbstract) continue;

                    string fixtureMode = defaultAsmMode;
                    var classAttrs = SafeGetCustomAttributes(t);
                    foreach (var ca in classAttrs)
                    {
                        string caName = ca.GetType().Name;
                        if (caName == "UnityPlatformAttribute")
                        {
                            string platformStr = ca.ToString();
                            if (platformStr.IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0)
                                fixtureMode = "playmode";
                            else if (platformStr.IndexOf("EditMode", StringComparison.OrdinalIgnoreCase) >= 0)
                                fixtureMode = "editmode";
                        }
                    }

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
                        string methodMode = fixtureMode;

                        foreach (var attr in methodAttrs)
                        {
                            if (attr == null) continue;
                            string attrName = attr.GetType().Name;
                            if (attrName == "TestAttribute" ||
                                attrName == "UnityTestAttribute" ||
                                attrName == "TestCaseAttribute" ||
                                attrName == "TheoryAttribute" ||
                                attrName == "FactAttribute")
                            {
                                isTest = true;
                            }
                            else if (attrName == "UnityPlatformAttribute")
                            {
                                string platformStr = attr.ToString();
                                if (platformStr.IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0)
                                    methodMode = "playmode";
                                else if (platformStr.IndexOf("EditMode", StringComparison.OrdinalIgnoreCase) >= 0)
                                    methodMode = "editmode";
                            }
                        }

                        if (!isTest) continue;

                        var categories = new List<string>(fixtureCategories);
                        categories.AddRange(ExtractCategories(methodAttrs));

                        discovered.Add(new DiscoveredItem
                        {
                            FullName = $"{t.FullName}.{m.Name}",
                            FixtureFullName = t.FullName ?? string.Empty,
                            Name = m.Name,
                            AssemblyName = asmName,
                            Categories = categories,
                            Mode = methodMode
                        });
                    }
                }
            }

            return discovered;
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
                bool asmMatch = assemblyNames.Any(asm =>
                    string.Equals(asm, item.AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(asm + ".dll", item.AssemblyName, StringComparison.OrdinalIgnoreCase));
                if (!asmMatch) return false;
            }

            if (categoryNames != null && categoryNames.Length > 0)
            {
                foreach (var cat in categoryNames)
                {
                    if (string.IsNullOrWhiteSpace(cat)) continue;
                    if (cat.StartsWith("!"))
                    {
                        string negated = cat.Substring(1);
                        if (item.Categories.Contains(negated, StringComparer.OrdinalIgnoreCase))
                            return false;
                    }
                    else
                    {
                        if (!item.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase))
                            return false;
                    }
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
                        if (Regex.IsMatch(item.FullName, g))
                            return true;
                    }
                    catch { }
                }
            }

            return false;
        }
    }
}
