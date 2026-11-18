#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        internal const string CapturedCoverageKey = CoverageLifecycleManager.CapturedCoverageKey;
        internal const string CoverageAttemptKey = CoverageLifecycleManager.CoverageAttemptKey;

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
            var captured = CoverageLifecycleManager.ReadCapturedCoverage();
            if (captured?.files == null)
            {
                writer.WriteLine("FAILURE No coverage data recorded. Run 'unity_test' with coverage: true first.");
                return;
            }

            string currentIdentity;
            try
            {
                currentIdentity = CompilationInputIdentityCalculator.ComputeCompilationInputIdentity();
            }
            catch (Exception ex)
            {
                writer.WriteLine($"FAILURE Could not verify whether coverage matches current compilation inputs: {ex.Message} Run 'unity_test' with coverage: true to measure the current code.");
                return;
            }

            if (captured.inputsChangedDuringRun || !string.Equals(captured.inputIdentity, currentIdentity, StringComparison.Ordinal))
            {
                writer.WriteLine("FAILURE Coverage data is obsolete because compilation inputs changed during or after the test run. Run 'unity_test' with coverage: true to measure the current code.");
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

        internal static CoverageFileReport CreateEmptyFileReport(string path) =>
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

        // Thin forwarding wrappers for existing unit and EditMode test entrypoints.
        internal static void ClearCapturedCoverage() => CoverageLifecycleManager.ClearCapturedCoverage();
        internal static void BeginCoverageAttempt(string runId) => CoverageLifecycleManager.BeginCoverageAttempt(runId);
        internal static bool EstablishCoverageBaseline(string runId) => CoverageLifecycleManager.EstablishCoverageBaseline(runId);
        internal static void MarkCoverageInstrumentationSettled(string runId) => CoverageLifecycleManager.MarkCoverageInstrumentationSettled(runId);
        internal static bool CoverageBaselineDiskInputsMatch(string runId) => CoverageLifecycleManager.CoverageBaselineDiskInputsMatch(runId);
        internal static void ObserveCompilationStarted() => CoverageLifecycleManager.ObserveCompilationStarted();
        internal static void ObserveCompilationFinished() => CoverageLifecycleManager.ObserveCompilationFinished();
        internal static bool AdvanceCoverageLifecycle(string runId, bool restoration) => CoverageLifecycleManager.AdvanceCoverageLifecycle(runId, restoration);
        internal static string GetCoverageAttemptPhase(string runId) => CoverageLifecycleManager.GetCoverageAttemptPhase(runId);
        internal static bool CoverageInputsMatchAttempt(string runId) => CoverageLifecycleManager.CoverageInputsMatchAttempt(runId);
        internal static bool CaptureCoverage(string runId) => CoverageLifecycleManager.CaptureCoverage(runId);

        internal static List<MethodBase> GetDeclaredMethodsAndConstructors(Type type) =>
            CoverageSequencePointHarvester.GetDeclaredMethodsAndConstructors(type);

        internal static List<MethodBase> GetAllMethodsAndConstructorsRecursive(Type type, HashSet<Type> visitedTypes = null) =>
            CoverageSequencePointHarvester.GetAllMethodsAndConstructorsRecursive(type, visitedTypes);

        internal static void ProcessType(
            Type type,
            string projectRoot,
            List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            HashSet<Type> visitedTypes,
            Dictionary<string, string> normalizedPathCache = null) =>
            CoverageSequencePointHarvester.ProcessType(type, projectRoot, filters, pointsByFile, visitedTypes, normalizedPathCache);

        internal static void ProcessMethod(
            MethodBase method,
            string projectRoot,
            List<string> filters,
            Dictionary<string, List<CoveredSequencePoint>> pointsByFile,
            Dictionary<string, string> normalizedPathCache = null) =>
            CoverageSequencePointHarvester.ProcessMethod(method, projectRoot, filters, pointsByFile, normalizedPathCache);

        internal static List<CoveredSequencePoint> CollapseDuplicateSequencePoints(List<CoveredSequencePoint> points) =>
            CoverageSequencePointHarvester.CollapseDuplicateSequencePoints(points);

        internal static List<UncoveredMethodSpan> ComputeUncoveredSpans(List<CoveredSequencePoint> points, bool collapseDuplicates = true) =>
            CoverageSequencePointHarvester.ComputeUncoveredSpans(points, collapseDuplicates);

        internal static string DemangleMethodName(string rawName) =>
            CoverageSequencePointHarvester.DemangleMethodName(rawName);
    }
}
