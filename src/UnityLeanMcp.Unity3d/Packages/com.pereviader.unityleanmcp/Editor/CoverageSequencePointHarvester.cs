#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor.Compilation;
using UnityEngine.TestTools;

namespace UnityLeanMcp
{
    /// <summary>
    /// Harvests native Unity sequence point coverage across project assemblies,
    /// collapses Mono async duplicate offsets, aggregates uncovered line spans,
    /// and demangles compiler-generated C# method names.
    /// </summary>
    internal static class CoverageSequencePointHarvester
    {
        internal static List<CoverageFileReport> HarvestCoverageReports(string projectRoot)
        {
            var normalizedFilters = new List<string> { "" };
            var normalizedPathCache = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectMatchedAssembliesAndSources(projectRoot, normalizedFilters, normalizedPathCache,
                out var matchedAssemblyNames, out var matchedSourceFiles);

            var pointsByFile = new Dictionary<string, List<CoveredSequencePoint>>(GetCoverageHandler.PathComparer);
            var visitedTypes = new HashSet<Type>();
            foreach (var asm in FindAssembliesToScan(projectRoot, matchedAssemblyNames))
            {
                Type[] types = GetLoadableTypes(asm);
                if (types == null) continue;
                foreach (var type in types)
                {
                    if (type != null && !type.IsNested)
                        ProcessType(type, projectRoot, normalizedFilters, pointsByFile, visitedTypes, normalizedPathCache);
                }
            }
            return BuildFileReports(pointsByFile, matchedSourceFiles);
        }

        private static void CollectMatchedAssembliesAndSources(
            string projectRoot,
            List<string> normalizedFilters,
            Dictionary<string, string> normalizedPathCache,
            out HashSet<string> matchedAssemblyNames,
            out HashSet<string> matchedSourceFiles)
        {
            matchedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            matchedSourceFiles = new HashSet<string>(GetCoverageHandler.PathComparer);
            foreach (var asm in CompilationPipeline.GetAssemblies())
            {
                if (asm.sourceFiles == null || asm.sourceFiles.Length == 0) continue;
                bool allPackageCache = true;
                foreach (var srcFile in asm.sourceFiles)
                {
                    if (string.IsNullOrEmpty(srcFile)) continue;
                    string normSrc = GetOrNormalizePath(srcFile, projectRoot, normalizedPathCache);
                    if (!normSrc.StartsWith("PackageCache/", GetCoverageHandler.PathComparison) &&
                        !normSrc.StartsWith("Library/PackageCache/", GetCoverageHandler.PathComparison))
                    {
                        allPackageCache = false;
                        break;
                    }
                }
                if (allPackageCache) continue;

                foreach (var srcFile in asm.sourceFiles)
                {
                    if (string.IsNullOrEmpty(srcFile)) continue;
                    string normSrc = GetOrNormalizePath(srcFile, projectRoot, normalizedPathCache);
                    if (GetCoverageHandler.MatchesAnyFilter(normSrc, normalizedFilters))
                    {
                        matchedAssemblyNames.Add(asm.name);
                        matchedSourceFiles.Add(normSrc);
                    }
                }
            }
        }

        private static List<System.Reflection.Assembly> FindAssembliesToScan(string projectRoot, HashSet<string> matchedAssemblyNames)
        {
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var assembliesToScan = new List<System.Reflection.Assembly>();
            foreach (var loaded in loadedAssemblies)
            {
                if (matchedAssemblyNames.Contains(loaded.GetName().Name))
                    assembliesToScan.Add(loaded);
            }
            if (assembliesToScan.Count > 0) return assembliesToScan;

            // If none matched via CompilationPipeline, scan project script assemblies as fallback.
            foreach (var loaded in loadedAssemblies)
            {
                try
                {
                    if (!loaded.IsDynamic && !string.IsNullOrEmpty(loaded.Location))
                    {
                        string loc = loaded.Location.Replace('\\', '/');
                        if (loc.Contains("/ScriptAssemblies/") || loc.StartsWith(projectRoot, GetCoverageHandler.PathComparison))
                            assembliesToScan.Add(loaded);
                    }
                }
                catch { }
            }
            return assembliesToScan;
        }

        private static Type[] GetLoadableTypes(System.Reflection.Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            catch { return null; }
        }

        private static List<CoverageFileReport> BuildFileReports(
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            HashSet<string> matchedSourceFiles)
        {
            var reports = new List<CoverageFileReport>();
            foreach (var kvp in pointsByFile.OrderBy(k => k.Key, GetCoverageHandler.PathComparer))
            {
                var points = kvp.Value;
                // Group points by line. A line is uncovered if any of its sequence points was not hit.
                var uncoveredLines = points.GroupBy(p => p.line)
                    .Where(g => g.Any(p => p.hitCount == 0))
                    .Select(g => (int)g.Key)
                    .OrderBy(l => l)
                    .ToArray();
                reports.Add(new CoverageFileReport
                {
                    path = kvp.Key,
                    totalPoints = points.Count,
                    coveredPoints = points.Count(p => p.hitCount > 0),
                    uncoveredLines = uncoveredLines,
                    uncoveredSpans = ComputeUncoveredSpans(points, collapseDuplicates: false)
                });
            }

            // Add any compiled source files matching the filters that had 0 sequence points (e.g. interfaces/enums).
            foreach (var srcFile in matchedSourceFiles)
            {
                if (!reports.Any(r => r.path.Equals(srcFile, GetCoverageHandler.PathComparison)))
                    reports.Add(GetCoverageHandler.CreateEmptyFileReport(srcFile));
            }
            return reports.OrderBy(r => r.path, GetCoverageHandler.PathComparer).ToList();
        }

        private static string GetOrNormalizePath(string rawPath, string projectRoot, Dictionary<string, string> cache)
        {
            if (cache == null) return GetCoverageHandler.NormalizePathRelativeToProject(rawPath, projectRoot);
            if (!cache.TryGetValue(rawPath, out string normFile))
            {
                normFile = GetCoverageHandler.NormalizePathRelativeToProject(rawPath, projectRoot);
                cache[rawPath] = normFile;
            }
            return normFile;
        }

        internal static List<MethodBase> GetDeclaredMethodsAndConstructors(Type type)
        {
            var result = new List<MethodBase>();
            if (type == null) return result;
            const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            const BindingFlags allFlags = instanceFlags | BindingFlags.Static;
            try
            {
                var methods = type.GetMethods(allFlags);
                if (methods != null) result.AddRange(methods);
            }
            catch { }
            try
            {
                // In .NET reflection, GetConstructors with BindingFlags.Static includes the static constructor (.cctor).
                // Restrict GetConstructors to instanceFlags so .ctor and .cctor are disjoint and .cctor is not duplicated.
                var constructors = type.GetConstructors(instanceFlags);
                if (constructors != null) result.AddRange(constructors);
            }
            catch { }
            try
            {
                var initializer = type.TypeInitializer;
                if (initializer != null) result.Add(initializer);
            }
            catch { }
            return result;
        }

        internal static List<MethodBase> GetAllMethodsAndConstructorsRecursive(Type type, HashSet<Type> visitedTypes = null)
        {
            var result = new List<MethodBase>();
            if (type == null) return result;
            if (visitedTypes == null) visitedTypes = new HashSet<Type>();
            if (!visitedTypes.Add(type)) return result;

            result.AddRange(GetDeclaredMethodsAndConstructors(type));
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Type[] nestedTypes = null;
            try { nestedTypes = type.GetNestedTypes(flags); } catch { }
            if (nestedTypes != null)
            {
                foreach (var nested in nestedTypes)
                    result.AddRange(GetAllMethodsAndConstructorsRecursive(nested, visitedTypes));
            }
            return result;
        }

        internal static void ProcessType(
            Type type, string projectRoot, List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            HashSet<Type> visitedTypes, Dictionary<string, string> normalizedPathCache = null)
        {
            normalizedPathCache ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in GetAllMethodsAndConstructorsRecursive(type, visitedTypes))
                ProcessMethod(m, projectRoot, filters, pointsByFile, normalizedPathCache);
        }

        internal static void ProcessMethod(
            MethodBase method, string projectRoot, List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            Dictionary<string, string> normalizedPathCache = null)
        {
            if (method == null) return;
            CoveredSequencePoint[] points = null;
            try { points = Coverage.GetSequencePointsFor(method); }
            catch { return; }
            if (points == null || points.Length == 0) return;

            var methodPoints = new List<CoveredSequencePoint>(points.Length);
            foreach (var sp in points)
            {
                // Ignore Roslyn hidden sequence point (0xfeefee) and invalid lines
                if (sp.line == 0xfeefee || sp.line == 0 || sp.line > int.MaxValue || string.IsNullOrEmpty(sp.filename))
                    continue;
                string normFile = GetOrNormalizePath(sp.filename, projectRoot, normalizedPathCache);
                if (!GetCoverageHandler.MatchesAnyFilter(normFile, filters))
                    continue;
                var point = sp;
                point.filename = normFile;
                if (point.method == null) point.method = method;
                methodPoints.Add(point);
            }
            if (methodPoints.Count == 0) return;

            var collapsed = CollapseDuplicateSequencePoints(methodPoints);
            for (int i = 0; i < collapsed.Count; i++)
            {
                var point = collapsed[i];
                if (!pointsByFile.TryGetValue(point.filename, out var list))
                {
                    list = new List<CoveredSequencePoint>();
                    pointsByFile[point.filename] = list;
                }
                list.Add(point);
            }
        }

        private readonly struct SequencePointLocationKey : IEquatable<SequencePointLocationKey>
        {
            public readonly MethodBase Method;
            public readonly string FilePath;
            public readonly uint Line;
            public readonly uint Column;

            public SequencePointLocationKey(MethodBase method, string filePath, uint line, uint column)
            {
                Method = method;
                FilePath = filePath ?? string.Empty;
                Line = line;
                Column = column;
            }

            public bool Equals(SequencePointLocationKey other) =>
                ReferenceEquals(Method, other.Method) && Line == other.Line && Column == other.Column &&
                string.Equals(FilePath, other.FilePath, GetCoverageHandler.PathComparison);

            public override bool Equals(object obj) => obj is SequencePointLocationKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Method);
                    hash = hash * 31 + GetCoverageHandler.PathComparer.GetHashCode(FilePath);
                    hash = hash * 31 + (int)Line;
                    return hash * 31 + (int)Column;
                }
            }
        }

        internal static List<CoveredSequencePoint> CollapseDuplicateSequencePoints(List<CoveredSequencePoint> points)
        {
            if (points == null || points.Count == 0) return new List<CoveredSequencePoint>();
            if (points.Count == 1) return new List<CoveredSequencePoint>(1) { points[0] };

            // Mono JIT emits extra sequence points for async YieldOffset/ResumeOffset at
            // the exact (filename, line, column) of the await statement, which stay unhit
            // when the awaited operation completes synchronously.
            var collapsed = new List<CoveredSequencePoint>(points.Count);
            var indexByKey = new Dictionary<SequencePointLocationKey, int>();
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.column == 0)
                {
                    collapsed.Add(pt);
                    continue;
                }
                var key = new SequencePointLocationKey(pt.method, pt.filename, pt.line, pt.column);
                if (indexByKey.TryGetValue(key, out int existingIndex))
                {
                    var existing = collapsed[existingIndex];
                    if (pt.hitCount > existing.hitCount) existing.hitCount = pt.hitCount;
                    if (pt.ilOffset < existing.ilOffset) existing.ilOffset = pt.ilOffset;
                    collapsed[existingIndex] = existing;
                }
                else
                {
                    indexByKey[key] = collapsed.Count;
                    collapsed.Add(pt);
                }
            }
            return collapsed;
        }

        internal static List<UncoveredMethodSpan> ComputeUncoveredSpans(List<CoveredSequencePoint> points, bool collapseDuplicates = true)
        {
            var result = new List<UncoveredMethodSpan>();
            if (points == null || points.Count == 0) return result;
            if (collapseDuplicates) points = CollapseDuplicateSequencePoints(points);

            var pointsByMethod = new Dictionary<string, List<CoveredSequencePoint>>(StringComparer.Ordinal);
            var methodNameCache = new Dictionary<MethodBase, string>();
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                string name = "";
                if (pt.method != null && !methodNameCache.TryGetValue(pt.method, out name))
                {
                    name = GetFriendlyMethodName(pt.method);
                    methodNameCache[pt.method] = name;
                }
                if (!pointsByMethod.TryGetValue(name, out var list))
                {
                    list = new List<CoveredSequencePoint>();
                    pointsByMethod[name] = list;
                }
                list.Add(pt);
            }

            foreach (var kvp in pointsByMethod)
            {
                var methodUncoveredLines = kvp.Value
                    .GroupBy(p => p.line)
                    .Where(g => g.Any(p => p.hitCount == 0))
                    .Select(g => (int)g.Key)
                    .OrderBy(l => l)
                    .ToArray();
                if (methodUncoveredLines.Length > 0)
                    AppendContiguousSpans(result, kvp.Key, methodUncoveredLines);
            }

            result.Sort((a, b) =>
            {
                int cmp = a.startLine.CompareTo(b.startLine);
                if (cmp != 0) return cmp;
                cmp = a.endLine.CompareTo(b.endLine);
                return cmp != 0 ? cmp : string.Compare(a.method, b.method, StringComparison.Ordinal);
            });
            return result;
        }

        private static void AppendContiguousSpans(List<UncoveredMethodSpan> result, string methodName, int[] sortedLines)
        {
            int spanStart = sortedLines[0];
            int prev = spanStart;
            for (int i = 1; i < sortedLines.Length; i++)
            {
                if (sortedLines[i] == prev + 1)
                {
                    prev = sortedLines[i];
                    continue;
                }
                result.Add(new UncoveredMethodSpan { method = methodName, startLine = spanStart, endLine = prev });
                spanStart = sortedLines[i];
                prev = spanStart;
            }
            result.Add(new UncoveredMethodSpan { method = methodName, startLine = spanStart, endLine = prev });
        }

        internal static string GetFriendlyMethodName(MethodBase method)
        {
            if (method == null) return "";
            string name = method.Name;
            Type declaringType = method.DeclaringType;
            // MoveNext or SetStateMachine in an async/iterator state machine type (e.g. <Foo>d__1)
            if ((name == "MoveNext" || name == "SetStateMachine") && declaringType != null && declaringType.IsNested)
            {
                string extracted = ExtractFromAngleBrackets(declaringType.Name);
                if (!string.IsNullOrEmpty(extracted)) return DemangleMethodName(extracted);
            }
            return DemangleMethodName(name);
        }

        internal static string DemangleMethodName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";
            if (TryDemangleMethodDisplayName(rawName, out string demangled)) return demangled;

            // Explicit interface implementation: Namespace.IFoo.Bar -> IFoo.Bar
            int lastDot = rawName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < rawName.Length - 1)
            {
                int secondToLastDot = rawName.LastIndexOf('.', lastDot - 1);
                if (secondToLastDot >= 0) return rawName.Substring(secondToLastDot + 1);
            }
            return rawName;
        }

        internal static bool TryDemangleMethodDisplayName(string rawName, out string displayName)
        {
            displayName = null;
            if (string.IsNullOrEmpty(rawName)) return false;

            // Strip enclosing < ... > wrapper if present: e.g. <Foo> or <<Foo>d>
            if (rawName.StartsWith("<", StringComparison.Ordinal) && rawName.EndsWith(">", StringComparison.Ordinal))
            {
                string stripped = rawName;
                while (stripped.StartsWith("<", StringComparison.Ordinal) && stripped.EndsWith(">", StringComparison.Ordinal))
                    stripped = stripped.Substring(1, stripped.Length - 2);
                displayName = DemangleMethodName(stripped);
                return true;
            }

            // State machine wrapper: <Inner>d or <Inner>d__12
            if (rawName.StartsWith("<", StringComparison.Ordinal))
            {
                int lastAngle = rawName.LastIndexOf('>');
                if (lastAngle > 0 && lastAngle < rawName.Length - 1)
                {
                    string suffix = rawName.Substring(lastAngle + 1);
                    if (suffix.StartsWith("d") || suffix.StartsWith("c"))
                    {
                        displayName = DemangleMethodName(rawName.Substring(1, lastAngle - 1));
                        return true;
                    }
                }
            }

            // Local function: <EnclosingMethod>g__LocalFunctionName|...
            int gIndex = rawName.LastIndexOf(">g__", StringComparison.Ordinal);
            if (gIndex >= 0)
            {
                int start = rawName.StartsWith("<", StringComparison.Ordinal) ? 1 : 0;
                string enclosing = DemangleMethodName(rawName.Substring(start, gIndex - start));
                string localPart = rawName.Substring(gIndex + 4);
                int pipeIndex = localPart.IndexOf('|');
                string localName = pipeIndex >= 0 ? localPart.Substring(0, pipeIndex) : localPart;
                displayName = string.IsNullOrEmpty(enclosing) ? localName : (enclosing + "." + localName);
                return true;
            }

            // Lambda: <EnclosingMethod>b__...
            int bIndex = rawName.LastIndexOf(">b__", StringComparison.Ordinal);
            if (bIndex >= 0)
            {
                int start = rawName.StartsWith("<", StringComparison.Ordinal) ? 1 : 0;
                displayName = DemangleMethodName(rawName.Substring(start, bIndex - start));
                return true;
            }

            // Generic state machine or other angle bracket wrapper: <MethodName>...
            if (rawName.StartsWith("<", StringComparison.Ordinal))
            {
                int endAngle = rawName.LastIndexOf('>');
                if (endAngle > 1)
                {
                    displayName = DemangleMethodName(rawName.Substring(1, endAngle - 1));
                    return true;
                }
            }
            return false;
        }

        private static string ExtractFromAngleBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int start = s.IndexOf('<');
            int end = s.LastIndexOf('>');
            return start >= 0 && end > start ? s.Substring(start + 1, end - start - 1) : "";
        }
    }
}
