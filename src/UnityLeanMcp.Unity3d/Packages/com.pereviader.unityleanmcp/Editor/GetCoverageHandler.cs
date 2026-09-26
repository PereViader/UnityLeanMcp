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
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => true;

        public void Handle(string payload, StreamWriter writer)
        {
            var activeOp = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            if (activeOp != null)
            {
                writer.WriteLine($"BUSY {activeOp.Kind} {activeOp.OperationId}");
                return;
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                writer.WriteLine("FAILURE Compilation failed");
                return;
            }

            if (UnityLeanMcpCompilationTracker.IsCompiling ||
                UnityLeanMcpCompilationTracker.RefreshPending ||
                UnityLeanMcpCompilationTracker.RefreshRequired)
            {
                writer.WriteLine("BUSY compile");
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

            var coveredStats = Coverage.GetStatsForAllCoveredMethods();
            if (coveredStats == null || coveredStats.Length == 0)
            {
                writer.WriteLine("FAILURE No coverage data recorded. Run 'unity_test' with coverage: true first.");
                return;
            }

            string projectRoot = UnityLeanMcpPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";

            // Normalize requested filter paths
            var normalizedFilters = new List<string>();
            foreach (var rawPath in args.paths)
            {
                string p = NormalizePathRelativeToProject(rawPath, projectRoot);

                // If path does not end in .cs, ensure trailing slash for directory matching
                if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("/"))
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

            // Validate that requested paths exist on disk
            var nonExistentPaths = new List<string>();
            foreach (var rawPath in args.paths)
            {
                string trimmed = rawPath.Trim();
                string fullPath;
                try
                {
                    fullPath = IsPathRootedCrossPlatform(trimmed)
                        ? Path.GetFullPath(trimmed)
                        : Path.GetFullPath(Path.Combine(projectRoot, trimmed));
                }
                catch
                {
                    nonExistentPaths.Add(trimmed);
                    continue;
                }

                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    nonExistentPaths.Add(trimmed);
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

            // Find assemblies containing scripts that match any requested filter
            var allAssemblies = CompilationPipeline.GetAssemblies();
            var matchedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in allAssemblies)
            {
                if (asm.sourceFiles == null)
                    continue;

                foreach (var srcFile in asm.sourceFiles)
                {
                    string normSrc = NormalizePathRelativeToProject(srcFile, projectRoot);

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
                            if (loc.Contains("/ScriptAssemblies/") || loc.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                assembliesToScan.Add(loaded);
                            }
                        }
                    }
                    catch { }
                }
            }

            // Harvest sequence points for matching files
            var pointsByFile = new Dictionary<string, List<CoveredSequencePoint>>(StringComparer.OrdinalIgnoreCase);
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

                    ProcessType(type, projectRoot, normalizedFilters, pointsByFile, visitedTypes);
                }
            }

            var reports = new List<CoverageFileReport>();
            foreach (var kvp in pointsByFile.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string filePath = kvp.Key;
                var points = kvp.Value;

                int totalPoints = points.Count;
                int coveredPoints = points.Count(p => p.hitCount > 0);

                // Group points by line. A line is uncovered if none of its sequence points was hit.
                var linesWithPoints = points.GroupBy(p => p.line);
                var uncoveredLines = linesWithPoints
                    .Where(g => g.All(p => p.hitCount == 0))
                    .Select(g => (int)g.Key)
                    .OrderBy(l => l)
                    .ToArray();

                reports.Add(new CoverageFileReport
                {
                    path = filePath,
                    totalPoints = totalPoints,
                    coveredPoints = coveredPoints,
                    uncoveredLines = uncoveredLines
                });
            }

            // Add any compiled source files matching the filters that had 0 sequence points (e.g. interfaces/enums)
            foreach (var srcFile in matchedSourceFiles)
            {
                if (!reports.Any(r => r.path.Equals(srcFile, StringComparison.OrdinalIgnoreCase)))
                {
                    reports.Add(CreateEmptyFileReport(srcFile));
                }
            }

            // Also check if an exact .cs file filter exists on disk even if not in compiled assembly source files
            foreach (var filter in normalizedFilters)
            {
                if (filter.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reports.Any(r => r.path.Equals(filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        string fullPath = Path.Combine(projectRoot, filter);
                        if (File.Exists(fullPath))
                        {
                            reports.Add(CreateEmptyFileReport(filter));
                        }
                    }
                }
            }

            reports = reports.OrderBy(r => r.path, StringComparer.OrdinalIgnoreCase).ToList();

            var response = new CoverageResponsePayload
            {
                files = reports
            };

            string json = JsonUtility.ToJson(response);
            writer.WriteLine($"SUCCESS {ProtocolCodec.EscapeLine(json)}");
        }

        private static CoverageFileReport CreateEmptyFileReport(string path) =>
            new CoverageFileReport
            {
                path = path,
                totalPoints = 0,
                coveredPoints = 0,
                uncoveredLines = new int[0]
            };

        private static string NormalizePathRelativeToProject(string path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Trim().Replace('\\', '/');
            if (p.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(projectRoot.Length);
            }
            if (p.StartsWith("./"))
                p = p.Substring(2);
            if (p.StartsWith("/"))
                p = p.TrimStart('/');
            return p;
        }

        private static bool IsPathRootedCrossPlatform(string path)
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

        private static bool MatchesAnyFilter(string filePath, List<string> filters)
        {
            foreach (var f in filters)
            {
                if (f.EndsWith("/", StringComparison.Ordinal))
                {
                    if (filePath.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    if (filePath.Equals(f, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static void ProcessType(Type type, string projectRoot, List<string> filters, Dictionary<string, List<CoveredSequencePoint>> pointsByFile, HashSet<Type> visitedTypes)
        {
            if (type == null || !visitedTypes.Add(type)) return;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            MethodInfo[] methods = null;
            try { methods = type.GetMethods(flags); } catch { }
            if (methods != null)
            {
                foreach (var m in methods)
                {
                    ProcessMethod(m, projectRoot, filters, pointsByFile);
                }
            }

            ConstructorInfo[] constructors = null;
            try { constructors = type.GetConstructors(flags); } catch { }
            if (constructors != null)
            {
                foreach (var c in constructors)
                {
                    ProcessMethod(c, projectRoot, filters, pointsByFile);
                }
            }

            MethodBase initializer = null;
            try { initializer = type.TypeInitializer; } catch { }
            if (initializer != null)
            {
                ProcessMethod(initializer, projectRoot, filters, pointsByFile);
            }

            Type[] nestedTypes = null;
            try { nestedTypes = type.GetNestedTypes(flags); } catch { }
            if (nestedTypes != null)
            {
                foreach (var nested in nestedTypes)
                {
                    ProcessType(nested, projectRoot, filters, pointsByFile, visitedTypes);
                }
            }
        }

        private static void ProcessMethod(MethodBase method, string projectRoot, List<string> filters, Dictionary<string, List<CoveredSequencePoint>> pointsByFile)
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
                if (sp.line == 0xfeefee || sp.line <= 0)
                    continue;

                if (string.IsNullOrEmpty(sp.filename))
                    continue;

                string normFile = NormalizePathRelativeToProject(sp.filename, projectRoot);

                if (!MatchesAnyFilter(normFile, filters))
                    continue;

                if (!pointsByFile.TryGetValue(normFile, out var list))
                {
                    list = new List<CoveredSequencePoint>();
                    pointsByFile[normFile] = list;
                }

                list.Add(sp);
            }
        }
    }
}
