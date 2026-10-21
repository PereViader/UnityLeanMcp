#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityLeanMcp
{
    internal class GetCoverageHandler : ICommandHandler
    {
        // Preserve distinct Unix source paths, including case-sensitive macOS
        // volumes. This is the same conservative identity policy as the protocol.
        internal static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        internal static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Native counters can be lost when Coverage.enabled becomes false. Keep
        // the completed report in native session state before restoring the flag:
        // Test Runner cleanup can unlock a pending reload after RunFinished.
        internal const string CapturedCoverageKey = "UnityLeanMcp_CapturedCoverage";

        [Serializable]
        private sealed class CapturedCoverage
        {
            public string runId;
            public int compilationEpoch;
            public long capturedAtUtcTicks;
            public List<CoverageFileReport> files;
        }

        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            var activeOp = UnityCommandGate.ReadSnapshot();
            if (activeOp != null)
            {
                writer.WriteLine($"BUSY {activeOp.Kind} {activeOp.OperationId}");
                return;
            }

            string trimmedPayload = (payload ?? "").Trim();
            if (string.IsNullOrEmpty(trimmedPayload))
            {
                writer.WriteLine("ERROR: Missing arguments");
                return;
            }

            string unescapedJson = ProtocolCodec.UnescapeLine(trimmedPayload);
            GetCoverageArgs args = null;
            try
            {
                args = JsonUtility.FromJson<GetCoverageArgs>(unescapedJson);
            }
            catch (Exception ex)
            {
                writer.WriteLine($"ERROR: Invalid JSON for GetCoverageArgs: {ex.Message}");
                return;
            }

            if (args == null || args.paths == null || args.paths.Length == 0)
            {
                writer.WriteLine("ERROR: Missing or empty 'paths' argument");
                return;
            }

            for (int i = 0; i < args.paths.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(args.paths[i]))
                {
                    writer.WriteLine("ERROR: The 'paths' parameter contains an empty or whitespace-only path.");
                    return;
                }
            }

            string projectRoot = UnityLeanMcpPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";

            // Validate that requested paths exist on disk before querying runtime stats
            var nonExistentPaths = new List<string>();
            var resolvedPaths = new List<string>();
            foreach (var rawPath in args.paths)
            {
                if (!TryResolveExistingPath(rawPath, projectRoot, out string fullPath))
                {
                    string trimmed = rawPath.Trim();
                    if (!nonExistentPaths.Contains(trimmed, PathComparer))
                    {
                        nonExistentPaths.Add(trimmed);
                    }
                }
                else
                {
                    resolvedPaths.Add(fullPath);
                }
            }

            if (nonExistentPaths.Count > 0)
            {
                if (nonExistentPaths.Count == 1)
                {
                    writer.WriteLine($"FAILURE Path does not exist: '{nonExistentPaths[0]}'.");
                }
                else
                {
                    writer.WriteLine($"FAILURE Specified paths do not exist: {string.Join(", ", nonExistentPaths.Select(p => $"'{p}'"))}.");
                }
                return;
            }

            WriteCoverage(resolvedPaths, projectRoot, writer);
        }

        internal static void WriteCoverage(List<string> resolvedPaths, string projectRoot, StreamWriter writer)
        {
            string capturedJson = SessionState.GetString(CapturedCoverageKey, "");
            var captured = string.IsNullOrEmpty(capturedJson)
                ? null
                : JsonUtility.FromJson<CapturedCoverage>(capturedJson);
            if (captured?.files == null)
            {
                writer.WriteLine("FAILURE No coverage data recorded. Run 'unity_test' with coverage: true first.");
                return;
            }

            int currentEpoch = SessionState.GetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 0);
            if (captured.compilationEpoch != currentEpoch || UnityLeanMcpCompilationTracker.IsCompiling || EditorApplication.isCompiling)
            {
                writer.WriteLine("FAILURE Coverage data is obsolete because scripts were recompiled since coverage was recorded. Run 'unity_test' with coverage: true to measure the current code.");
                return;
            }

            // Normalize requested filter paths
            var normalizedFilters = new List<string>();
            foreach (var rawPath in resolvedPaths)
            {
                string p = NormalizePathRelativeToProject(rawPath, projectRoot);

                // If path is not empty and does not end in .cs, ensure trailing slash for directory matching
                if (!string.IsNullOrEmpty(p) && !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("/"))
                {
                    p += "/";
                }

                normalizedFilters.Add(p);
            }

            if (normalizedFilters.Count == 0)
            {
                writer.WriteLine("ERROR: No valid paths provided");
                return;
            }

            var reports = captured.files.Where(r => MatchesAnyFilter(r.path, normalizedFilters)).ToList();

            if (captured.capturedAtUtcTicks > 0)
            {
                var checkedPaths = new HashSet<string>(PathComparer);
                foreach (var fullPath in resolvedPaths)
                {
                    if (File.Exists(fullPath) && !Directory.Exists(fullPath) && checkedPaths.Add(fullPath))
                    {
                        long lastWriteTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
                        if (lastWriteTicks > captured.capturedAtUtcTicks)
                        {
                            string relPath = NormalizePathRelativeToProject(fullPath, projectRoot);
                            writer.WriteLine($"FAILURE Coverage data for '{relPath}' is obsolete because the file was modified since coverage was recorded. Run 'unity_test' with coverage: true to measure the current code.");
                            return;
                        }
                    }
                }

                foreach (var report in reports)
                {
                    if (string.IsNullOrEmpty(report.path)) continue;
                    string fullPath = Path.Combine(projectRoot, report.path);
                    if (File.Exists(fullPath) && checkedPaths.Add(fullPath))
                    {
                        long lastWriteTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
                        if (lastWriteTicks > captured.capturedAtUtcTicks)
                        {
                            writer.WriteLine($"FAILURE Coverage data for '{report.path}' is obsolete because the file was modified since coverage was recorded. Run 'unity_test' with coverage: true to measure the current code.");
                            return;
                        }
                    }
                }
            }
            foreach (string filter in normalizedFilters)
            {
                if (filter.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !reports.Any(r => r.path.Equals(filter, PathComparison)))
                {
                    reports.Add(CreateEmptyFileReport(filter));
                }
            }

            string json = JsonUtility.ToJson(new CoverageResponsePayload
            {
                files = reports.OrderBy(r => r.path, PathComparer).ToList()
            });
            writer.WriteLine($"SUCCESS {ProtocolCodec.EscapeLine(json)}");
        }

        internal static void ClearCapturedCoverage() => SessionState.EraseString(CapturedCoverageKey);

        internal static void CaptureCoverage(string runId)
        {
            string previousJson = SessionState.GetString(CapturedCoverageKey, "");
            if (!string.IsNullOrEmpty(previousJson) &&
                JsonUtility.FromJson<CapturedCoverage>(previousJson)?.runId == runId)
                return;

            if (!Coverage.enabled)
                return;

            string projectRoot = UnityLeanMcpPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            var normalizedFilters = new List<string> { "" };
            var normalizedPathCache = new Dictionary<string, string>(StringComparer.Ordinal);

            // Find assemblies containing scripts that match any requested filter
            var allAssemblies = CompilationPipeline.GetAssemblies();
            var matchedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedSourceFiles = new HashSet<string>(PathComparer);

            foreach (var asm in allAssemblies)
            {
                if (asm.sourceFiles == null || asm.sourceFiles.Length == 0)
                    continue;

                bool allPackageCache = true;
                foreach (var srcFile in asm.sourceFiles)
                {
                    if (string.IsNullOrEmpty(srcFile))
                        continue;

                    if (!normalizedPathCache.TryGetValue(srcFile, out string normSrc))
                    {
                        normSrc = NormalizePathRelativeToProject(srcFile, projectRoot);
                        normalizedPathCache[srcFile] = normSrc;
                    }

                    if (!normSrc.StartsWith("PackageCache/", PathComparison) &&
                        !normSrc.StartsWith("Library/PackageCache/", PathComparison))
                    {
                        allPackageCache = false;
                        break;
                    }
                }

                if (allPackageCache)
                {
                    continue;
                }

                foreach (var srcFile in asm.sourceFiles)
                {
                    if (string.IsNullOrEmpty(srcFile))
                        continue;

                    if (!normalizedPathCache.TryGetValue(srcFile, out string normSrc))
                    {
                        normSrc = NormalizePathRelativeToProject(srcFile, projectRoot);
                        normalizedPathCache[srcFile] = normSrc;
                    }

                    if (MatchesAnyFilter(normSrc, normalizedFilters))
                    {
                        matchedAssemblyNames.Add(asm.name);
                        matchedSourceFiles.Add(normSrc);
                    }
                }
            }

            // Map loaded System.Reflection assemblies
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var assembliesToScan = new List<System.Reflection.Assembly>();
            foreach (var loaded in loadedAssemblies)
            {
                string name = loaded.GetName().Name;
                if (matchedAssemblyNames.Contains(name))
                {
                    assembliesToScan.Add(loaded);
                }
            }

            // If none matched via CompilationPipeline, scan project script assemblies as fallback
            if (assembliesToScan.Count == 0)
            {
                foreach (var loaded in loadedAssemblies)
                {
                    try
                    {
                        if (!loaded.IsDynamic && !string.IsNullOrEmpty(loaded.Location))
                        {
                            string loc = loaded.Location.Replace('\\', '/');
                            if (loc.Contains("/ScriptAssemblies/") || loc.StartsWith(projectRoot, PathComparison))
                            {
                                assembliesToScan.Add(loaded);
                            }
                        }
                    }
                    catch { }
                }
            }

            // Harvest sequence points for matching files
            var pointsByFile = new Dictionary<string, List<CoveredSequencePoint>>(PathComparer);
            var visitedTypes = new HashSet<Type>();

            foreach (var asm in assembliesToScan)
            {
                Type[] types = null;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsNested)
                        continue;

                    ProcessType(type, projectRoot, normalizedFilters, pointsByFile, visitedTypes, normalizedPathCache);
                }
            }

            var reports = new List<CoverageFileReport>();
            foreach (var kvp in pointsByFile.OrderBy(k => k.Key, PathComparer))
            {
                string filePath = kvp.Key;
                var points = kvp.Value;

                int totalPoints = points.Count;
                int coveredPoints = points.Count(p => p.hitCount > 0);

                // Group points by line. A line is uncovered if any of its sequence points was not hit.
                var linesWithPoints = points.GroupBy(p => p.line);
                var uncoveredLines = linesWithPoints
                    .Where(g => g.Any(p => p.hitCount == 0))
                    .Select(g => (int)g.Key)
                    .OrderBy(l => l)
                    .ToArray();

                var uncoveredSpans = ComputeUncoveredSpans(points);

                reports.Add(new CoverageFileReport
                {
                    path = filePath,
                    totalPoints = totalPoints,
                    coveredPoints = coveredPoints,
                    uncoveredLines = uncoveredLines,
                    uncoveredSpans = uncoveredSpans
                });
            }

            // Add any compiled source files matching the filters that had 0 sequence points (e.g. interfaces/enums)
            foreach (var srcFile in matchedSourceFiles)
            {
                if (!reports.Any(r => r.path.Equals(srcFile, PathComparison)))
                {
                    reports.Add(CreateEmptyFileReport(srcFile));
                }
            }

            reports = reports.OrderBy(r => r.path, PathComparer).ToList();

            int epoch = SessionState.GetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 0);
            long nowTicks = DateTime.UtcNow.Ticks;

            SessionState.SetString(CapturedCoverageKey, JsonUtility.ToJson(new CapturedCoverage
            {
                runId = runId,
                compilationEpoch = epoch,
                capturedAtUtcTicks = nowTicks,
                files = reports
            }));
        }

        private static CoverageFileReport CreateEmptyFileReport(string path) =>
            new CoverageFileReport
            {
                path = path,
                totalPoints = 0,
                coveredPoints = 0,
                uncoveredLines = new int[0],
                uncoveredSpans = new List<UncoveredMethodSpan>()
            };

        internal static string NormalizePathRelativeToProject(string path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (TryResolveExistingPath(path, projectRoot, out string canonicalPath))
                path = canonicalPath;
            string p = path.Trim().Replace('\\', '/');
            string normRoot = (projectRoot ?? "").Replace('\\', '/').TrimEnd('/');
            var rootComparison = normRoot.Length >= 2 && char.IsLetter(normRoot[0]) && normRoot[1] == ':'
                ? StringComparison.OrdinalIgnoreCase : PathComparison;
            if (!string.IsNullOrEmpty(normRoot))
            {
                if (p.Equals(normRoot, rootComparison))
                {
                    return "";
                }
                if (p.StartsWith(normRoot + "/", rootComparison))
                {
                    p = p.Substring(normRoot.Length + 1);
                }
            }
            if (p.StartsWith("./"))
                p = p.Substring(2);
            if (p.StartsWith("/"))
                p = p.TrimStart('/');
            if (p == "." || p == "./")
                return "";
            return p;
        }

        internal static bool IsPathRootedCrossPlatform(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Path.IsPathRooted(path)) return true;
            if (path.StartsWith('/') || path.StartsWith('\\')) return true;
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                if (path.Length == 2 || path[2] == '/' || path[2] == '\\')
                    return true;
            }
            return false;
        }

        internal static bool TryResolveExistingPath(string rawPath, string projectRoot, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            string trimmed = rawPath.Trim();
            string normRoot = (projectRoot ?? "").Replace('\\', '/').TrimEnd('/');
            try
            {
                if (IsPathRootedCrossPlatform(trimmed))
                {
                    fullPath = Path.GetFullPath(trimmed);
                    string normFull = fullPath.Replace('\\', '/');
                    if (normFull.Equals(normRoot, PathComparison) ||
                        normFull.StartsWith(normRoot + "/", PathComparison))
                    {
                        if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        {
                            return true;
                        }
                    }

                    // If a leading slash was used (e.g. "/Assets/Scripts"), check relative to projectRoot
                    string relTrimmed = trimmed.TrimStart('/', '\\');
                    string projectRelativePath = Path.GetFullPath(Path.Combine(projectRoot, relTrimmed));
                    if (File.Exists(projectRelativePath) || Directory.Exists(projectRelativePath))
                    {
                        fullPath = projectRelativePath;
                        return true;
                    }

                    return false;
                }

                fullPath = Path.GetFullPath(Path.Combine(projectRoot, trimmed));
                return File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        internal static bool MatchesAnyFilter(string filePath, List<string> filters)
        {
            foreach (var f in filters)
            {
                if (string.IsNullOrEmpty(f) || f == "/")
                {
                    return true;
                }

                if (f.EndsWith("/", StringComparison.Ordinal))
                {
                    if (filePath.StartsWith(f, PathComparison))
                        return true;
                }
                else
                {
                    if (filePath.Equals(f, PathComparison))
                        return true;
                }
            }
            return false;
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
                // We restrict GetConstructors to instanceFlags so .ctor and .cctor are disjoint and .cctor is not duplicated.
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
                {
                    result.AddRange(GetAllMethodsAndConstructorsRecursive(nested, visitedTypes));
                }
            }

            return result;
        }

        internal static void ProcessType(
            Type type,
            string projectRoot,
            List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            HashSet<Type> visitedTypes,
            Dictionary<string, string> normalizedPathCache = null)
        {
            normalizedPathCache ??= new Dictionary<string, string>(StringComparer.Ordinal);
            var methods = GetAllMethodsAndConstructorsRecursive(type, visitedTypes);
            foreach (var m in methods)
            {
                ProcessMethod(m, projectRoot, filters, pointsByFile, normalizedPathCache);
            }
        }

        internal static void ProcessMethod(
            MethodBase method,
            string projectRoot,
            List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            Dictionary<string, string> normalizedPathCache = null)
        {
            if (method == null) return;

            CoveredSequencePoint[] points = null;
            try
            {
                points = Coverage.GetSequencePointsFor(method);
            }
            catch
            {
                return;
            }

            if (points == null || points.Length == 0)
                return;

            foreach (var sp in points)
            {
                // Ignore Roslyn hidden sequence point (0xfeefee) and invalid lines
                if (sp.line == 0xfeefee || sp.line == 0 || sp.line > int.MaxValue)
                    continue;

                if (string.IsNullOrEmpty(sp.filename))
                    continue;

                string normFile;
                if (normalizedPathCache != null)
                {
                    if (!normalizedPathCache.TryGetValue(sp.filename, out normFile))
                    {
                        normFile = NormalizePathRelativeToProject(sp.filename, projectRoot);
                        normalizedPathCache[sp.filename] = normFile;
                    }
                }
                else
                {
                    normFile = NormalizePathRelativeToProject(sp.filename, projectRoot);
                }

                if (!MatchesAnyFilter(normFile, filters))
                    continue;

                if (!pointsByFile.TryGetValue(normFile, out var list))
                {
                    list = new List<CoveredSequencePoint>();
                    pointsByFile[normFile] = list;
                }

                var point = sp;
                if (point.method == null)
                {
                    point.method = method;
                }
                list.Add(point);
            }
        }

        internal static List<UncoveredMethodSpan> ComputeUncoveredSpans(List<CoveredSequencePoint> points)
        {
            var result = new List<UncoveredMethodSpan>();
            if (points == null || points.Count == 0)
                return result;

            var pointsByMethod = new Dictionary<string, List<CoveredSequencePoint>>(StringComparer.Ordinal);
            var methodNameCache = new Dictionary<MethodBase, string>();
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                string name;
                if (pt.method == null)
                {
                    name = "";
                }
                else if (!methodNameCache.TryGetValue(pt.method, out name))
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
                string methodName = kvp.Key;
                var methodPoints = kvp.Value;

                var methodUncoveredLines = methodPoints
                    .GroupBy(p => p.line)
                    .Where(g => g.Any(p => p.hitCount == 0))
                    .Select(g => (int)g.Key)
                    .OrderBy(l => l)
                    .ToArray();

                if (methodUncoveredLines.Length == 0)
                    continue;

                int spanStart = methodUncoveredLines[0];
                int prev = spanStart;
                for (int i = 1; i < methodUncoveredLines.Length; i++)
                {
                    if (methodUncoveredLines[i] == prev + 1)
                    {
                        prev = methodUncoveredLines[i];
                    }
                    else
                    {
                        result.Add(new UncoveredMethodSpan
                        {
                            method = methodName,
                            startLine = spanStart,
                            endLine = prev
                        });
                        spanStart = methodUncoveredLines[i];
                        prev = spanStart;
                    }
                }
                result.Add(new UncoveredMethodSpan
                {
                    method = methodName,
                    startLine = spanStart,
                    endLine = prev
                });
            }

            result.Sort((a, b) =>
            {
                int cmp = a.startLine.CompareTo(b.startLine);
                if (cmp != 0) return cmp;
                cmp = a.endLine.CompareTo(b.endLine);
                if (cmp != 0) return cmp;
                return string.Compare(a.method, b.method, StringComparison.Ordinal);
            });

            return result;
        }

        internal static string GetFriendlyMethodName(MethodBase method)
        {
            if (method == null) return "";

            string name = method.Name;
            Type declaringType = method.DeclaringType;

            // MoveNext or SetStateMachine in an async/iterator state machine type (e.g. <Foo>d__1)
            if ((name == "MoveNext" || name == "SetStateMachine") && declaringType != null && declaringType.IsNested)
            {
                string typeName = declaringType.Name;
                string extracted = ExtractFromAngleBrackets(typeName);
                if (!string.IsNullOrEmpty(extracted))
                {
                    return DemangleMethodName(extracted);
                }
            }

            return DemangleMethodName(name);
        }

        internal static string DemangleMethodName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";

            // Strip enclosing < ... > wrapper if present: e.g. <Foo> or <<Foo>d>
            while (rawName.StartsWith("<", StringComparison.Ordinal) && rawName.EndsWith(">", StringComparison.Ordinal))
            {
                rawName = rawName.Substring(1, rawName.Length - 2);
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
                        string inner = rawName.Substring(1, lastAngle - 1);
                        return DemangleMethodName(inner);
                    }
                }
            }

            // Local function: <EnclosingMethod>g__LocalFunctionName|...
            int gIndex = rawName.LastIndexOf(">g__", StringComparison.Ordinal);
            if (gIndex >= 0)
            {
                int start = rawName.StartsWith("<", StringComparison.Ordinal) ? 1 : 0;
                string enclosing = rawName.Substring(start, gIndex - start);
                enclosing = DemangleMethodName(enclosing);
                string localPart = rawName.Substring(gIndex + 4);
                int pipeIndex = localPart.IndexOf('|');
                string localName = pipeIndex >= 0 ? localPart.Substring(0, pipeIndex) : localPart;
                return string.IsNullOrEmpty(enclosing) ? localName : (enclosing + "." + localName);
            }

            // Lambda: <EnclosingMethod>b__...
            int bIndex = rawName.LastIndexOf(">b__", StringComparison.Ordinal);
            if (bIndex >= 0)
            {
                int start = rawName.StartsWith("<", StringComparison.Ordinal) ? 1 : 0;
                string enclosing = rawName.Substring(start, bIndex - start);
                return DemangleMethodName(enclosing);
            }

            // Generic state machine or other angle bracket wrapper: <MethodName>...
            if (rawName.StartsWith("<", StringComparison.Ordinal))
            {
                int endAngle = rawName.LastIndexOf('>');
                if (endAngle > 1)
                {
                    string inner = rawName.Substring(1, endAngle - 1);
                    return DemangleMethodName(inner);
                }
            }

            // Explicit interface implementation: Namespace.IFoo.Bar -> IFoo.Bar
            int lastDot = rawName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < rawName.Length - 1)
            {
                int secondToLastDot = rawName.LastIndexOf('.', lastDot - 1);
                if (secondToLastDot >= 0)
                {
                    return rawName.Substring(secondToLastDot + 1);
                }
                return rawName;
            }

            return rawName;
        }

        private static string ExtractFromAngleBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int start = s.IndexOf('<');
            int end = s.LastIndexOf('>');
            if (start >= 0 && end > start)
            {
                return s.Substring(start + 1, end - start - 1);
            }
            return "";
        }
    }
}
