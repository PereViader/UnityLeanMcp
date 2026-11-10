#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityPackageInfo = UnityEditor.PackageManager.PackageInfo;

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

        // One completed report is retained in native session state for this Editor
        // session. A separate attempt record is recovery state for the active test.
        internal const string CapturedCoverageKey = "UnityLeanMcp_CapturedCoverage";
        internal const string CoverageAttemptKey = "UnityLeanMcp_CoverageAttempt";

        [Serializable]
        private sealed class CapturedCoverage
        {
            public string runId;
            public string inputIdentity;
            public bool inputsChangedDuringRun;
            public List<CoverageFileReport> files;
        }

        [Serializable]
        private sealed class CoverageAttempt
        {
            public string runId;
            public bool previousProfilerEnabled;
            public string inputIdentity;
            public string baselineDiskIdentity;
            public string phase;
            public bool baselineCompilationExpected;
            public bool baselineCompilationStarted;
            public bool baselineCompilationFinished;
            public bool baselineRequestIssued;
            public int baselineIdleUpdates;
            public bool enableCompilationExpected;
            public bool enableCompilationStarted;
            public bool enableCompilationFinished;
            public bool enableSettingApplied;
            public bool enableRequestIssued;
            public int enableIdleUpdates;
            public bool restoreCompilationExpected;
            public bool restoreCompilationStarted;
            public bool restoreCompilationFinished;
            public bool restoreRefreshRequested;
            public bool restoreRefreshCompleted;
            public bool restoreRefreshIssued;
            public bool restoreSettingApplied;
            public bool restoreRequestIssued;
            public string restoreFailureMessage;
            public bool runnerStarted;
            public bool terminalResultClaimed;
            public bool testCompleted;
            public bool captureAttempted;
            public string captureStatus;
            public string captureMessage;
            public int restoreIdleUpdates;
        }

        private struct CachedFileDigest
        {
            public long Length;
            public long LastWriteUtcTicks;
            public string Digest;
        }

        private static readonly Dictionary<string, CachedFileDigest> s_ImmutablePackageFileDigestCache =
            new Dictionary<string, CachedFileDigest>(PathComparer);

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

            string currentIdentity;
            try
            {
                currentIdentity = ComputeCompilationInputIdentity();
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

        internal static void ClearCapturedCoverage() => SessionState.EraseString(CapturedCoverageKey);

        internal static void BeginCoverageAttempt(string runId)
        {
            // Invalidate the single prior report as soon as a new attempt is
            // admitted. A later setup failure must not expose stale coverage.
            ClearCapturedCoverage();

            var attempt = new CoverageAttempt
            {
                runId = runId,
                previousProfilerEnabled = Coverage.enabled,
                phase = "baseline-preparing",
                baselineCompilationExpected = true
            };
            attempt.baselineDiskIdentity = ComputeCompilationInputFilesIdentity();
            SessionState.SetString(CoverageAttemptKey, JsonUtility.ToJson(attempt));

            // Reconcile disk imports and request compilation only after the
            // recovery record is durable. The baseline is finalized after this
            // compiler lifecycle, so a file edited after the last host refresh
            // cannot be mistaken for the source that produced loaded assemblies.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            CompilationPipeline.RequestScriptCompilation();
            var latest = ReadCoverageAttempt() ?? attempt;
            if (latest.runId == runId)
            {
                latest.baselineRequestIssued = true;
                WriteCoverageAttempt(latest);
            }
        }

        internal static bool EstablishCoverageBaseline(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId || attempt.phase != "baseline-preparing") return false;
            string inputIdentity = ComputeCompilationInputIdentity(out string currentDiskIdentity);
            if (!string.Equals(attempt.baselineDiskIdentity, currentDiskIdentity, StringComparison.Ordinal))
                return false;

            attempt.inputIdentity = inputIdentity;
            attempt.baselineCompilationExpected = false;
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            attempt.phase = attempt.previousProfilerEnabled ? "preparing" : "enabling";
            attempt.enableCompilationExpected = !attempt.previousProfilerEnabled;
            attempt.enableCompilationStarted = false;
            attempt.enableCompilationFinished = false;
            attempt.enableSettingApplied = attempt.previousProfilerEnabled;
            attempt.enableRequestIssued = false;
            attempt.enableIdleUpdates = 0;
            WriteCoverageAttempt(attempt);

            // Persist the expected instrumentation compile before changing the
            // native setting, which may reload this domain.
            if (!attempt.previousProfilerEnabled)
            {
                Coverage.enabled = true;
                var latest = ReadCoverageAttempt() ?? attempt;
                if (latest.runId == runId)
                {
                    latest.enableSettingApplied = true;
                    WriteCoverageAttempt(latest);
                }
            }
            return true;
        }

        internal static void MarkCoverageInstrumentationSettled(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId || attempt.phase != "enabling")
                throw new InvalidOperationException("Coverage instrumentation preparation state was lost.");
            attempt.enableCompilationExpected = false;
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            attempt.phase = "preparing";
            WriteCoverageAttempt(attempt);
        }

        internal static bool CoverageBaselineDiskInputsMatch(string runId)
        {
            var attempt = ReadCoverageAttempt();
            return attempt != null && attempt.runId == runId &&
                string.Equals(attempt.baselineDiskIdentity, ComputeCompilationInputFilesIdentity(), StringComparison.Ordinal);
        }

        internal static void AbortCoveragePreparation(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId) return;
            if (!IsEditorCompilationActive())
            {
                if (attempt.phase == "baseline-preparing" && !attempt.baselineCompilationStarted)
                    attempt.baselineCompilationExpected = false;
                if (attempt.phase == "enabling" && !attempt.enableCompilationStarted)
                    attempt.enableCompilationExpected = false;
            }
            WriteCoverageAttempt(attempt);
        }

        internal static void ObserveCompilationStarted()
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null) return;
            if (attempt.phase == "baseline-preparing" && attempt.baselineCompilationExpected)
            {
                attempt.baselineCompilationStarted = true;
                attempt.baselineCompilationFinished = false;
                attempt.baselineIdleUpdates = 0;
            }
            else if (attempt.phase == "enabling" && attempt.enableCompilationExpected)
            {
                attempt.enableCompilationStarted = true;
                attempt.enableCompilationFinished = false;
                attempt.enableIdleUpdates = 0;
            }
            else if (attempt.phase == "restoring" && attempt.restoreCompilationExpected)
            {
                attempt.restoreCompilationStarted = true;
                attempt.restoreCompilationFinished = false;
            }
            else if (attempt.phase == "restoring" && attempt.baselineCompilationExpected)
            {
                attempt.baselineCompilationStarted = true;
                attempt.baselineCompilationFinished = false;
            }
            else if (attempt.phase == "restoring" && attempt.enableCompilationExpected)
            {
                attempt.enableCompilationStarted = true;
                attempt.enableCompilationFinished = false;
            }
            else return;
            WriteCoverageAttempt(attempt);
        }

        internal static void ObserveCompilationFinished()
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null) return;
            if (attempt.phase == "baseline-preparing" && attempt.baselineCompilationExpected && attempt.baselineCompilationStarted)
                attempt.baselineCompilationFinished = true;
            else if (attempt.phase == "enabling" && attempt.enableCompilationExpected && attempt.enableCompilationStarted)
                attempt.enableCompilationFinished = true;
            else if (attempt.phase == "restoring" && attempt.restoreCompilationExpected && attempt.restoreCompilationStarted)
                attempt.restoreCompilationFinished = true;
            else if (attempt.phase == "restoring" && attempt.baselineCompilationExpected && attempt.baselineCompilationStarted)
                attempt.baselineCompilationFinished = true;
            else if (attempt.phase == "restoring" && attempt.enableCompilationExpected && attempt.enableCompilationStarted)
                attempt.enableCompilationFinished = true;
            else return;
            WriteCoverageAttempt(attempt);
        }

        internal static bool AdvanceCoverageLifecycle(string runId, bool restoration)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId) return false;
            if (!IsEditorCompilationActive())
            {
                if (!restoration && attempt.phase == "baseline-preparing" && attempt.baselineCompilationExpected &&
                    !attempt.baselineCompilationStarted && !attempt.baselineCompilationFinished && !attempt.baselineRequestIssued)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    UnityLeanMcpCompilationTracker.RefreshRequired = false;
                    attempt.baselineDiskIdentity = ComputeCompilationInputFilesIdentity();
                    WriteCoverageAttempt(attempt);
                    CompilationPipeline.RequestScriptCompilation();
                    var latest = ReadCoverageAttempt() ?? attempt;
                    if (latest.runId == runId)
                    {
                        latest.baselineRequestIssued = true;
                        WriteCoverageAttempt(latest);
                    }
                    return false;
                }
                if (!restoration && attempt.phase == "enabling" && attempt.enableCompilationExpected &&
                    !attempt.enableCompilationStarted && !attempt.enableCompilationFinished)
                {
                    if (!attempt.enableSettingApplied || !Coverage.enabled)
                    {
                        Coverage.enabled = true;
                        var latest = ReadCoverageAttempt() ?? attempt;
                        if (latest.runId == runId)
                        {
                            latest.enableSettingApplied = true;
                            WriteCoverageAttempt(latest);
                        }
                        return false;
                    }
                    if (!attempt.enableRequestIssued)
                    {
                        CompilationPipeline.RequestScriptCompilation();
                        var latest = ReadCoverageAttempt() ?? attempt;
                        if (latest.runId == runId)
                        {
                            latest.enableRequestIssued = true;
                            WriteCoverageAttempt(latest);
                        }
                        return false;
                    }
                    return false;
                }
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                UnityLeanMcpCompilationTracker.IsCompiling || UnityLeanMcpCompilationTracker.IsUpdating ||
                UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
                UnityLeanMcpCompilationTracker.CompilationInProgress)
            {
                if (restoration) attempt.restoreIdleUpdates = 0;
                else if (attempt.phase == "baseline-preparing") attempt.baselineIdleUpdates = 0;
                else if (attempt.phase == "enabling") attempt.enableIdleUpdates = 0;
                WriteCoverageAttempt(attempt);
                return false;
            }

            if (restoration)
            {
                if (attempt.baselineCompilationExpected && !attempt.baselineCompilationFinished) return false;
                if (attempt.enableCompilationExpected && !attempt.enableCompilationFinished) return false;
                if (attempt.restoreCompilationExpected && !attempt.restoreCompilationFinished) return false;
                attempt.restoreIdleUpdates++;
                WriteCoverageAttempt(attempt);
                return attempt.restoreIdleUpdates >= 5;
            }

            if (attempt.phase == "baseline-preparing")
            {
                if (attempt.baselineCompilationExpected && !attempt.baselineCompilationFinished) return false;
                attempt.baselineIdleUpdates++;
                WriteCoverageAttempt(attempt);
                return attempt.baselineIdleUpdates >= 5;
            }

            if (attempt.phase == "enabling")
            {
                if (attempt.enableCompilationExpected && !attempt.enableCompilationFinished) return false;
                attempt.enableIdleUpdates++;
                WriteCoverageAttempt(attempt);
                return attempt.enableIdleUpdates >= 5;
            }

            return true;
        }

        private static bool IsEditorCompilationActive() =>
            EditorApplication.isCompiling || EditorApplication.isUpdating ||
            UnityLeanMcpCompilationTracker.IsCompiling || UnityLeanMcpCompilationTracker.IsUpdating ||
            UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
            UnityLeanMcpCompilationTracker.CompilationInProgress;

        internal static bool IsCoverageTerminalResultClaimed(string runId)
        {
            var attempt = ReadCoverageAttempt();
            return attempt != null && attempt.runId == runId && attempt.terminalResultClaimed;
        }

        internal static bool IsCoverageAttempt(string runId)
        {
            var attempt = ReadCoverageAttempt();
            return attempt != null && attempt.runId == runId;
        }

        internal static string GetCoverageAttemptPhase(string runId)
        {
            var attempt = ReadCoverageAttempt();
            return attempt != null && attempt.runId == runId ? attempt.phase : null;
        }

        internal static string GetCoverageAttemptRunId() => ReadCoverageAttempt()?.runId;

        internal static bool CoverageInputsMatchAttempt(string runId)
        {
            var attempt = ReadCoverageAttempt();
            return attempt != null && attempt.runId == runId &&
                string.Equals(attempt.inputIdentity, ComputeCompilationInputIdentity(), StringComparison.Ordinal);
        }

        internal static bool TryMarkCoverageRunnerStarted(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId || attempt.terminalResultClaimed)
                return false;
            attempt.runnerStarted = true;
            attempt.phase = "running";
            WriteCoverageAttempt(attempt);
            return true;
        }

        internal static void MarkCoverageTestCompleted(string runId, bool complete)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId || attempt.terminalResultClaimed)
                return;
            attempt.terminalResultClaimed = true;
            attempt.testCompleted = complete && attempt.runnerStarted;
            attempt.phase = attempt.testCompleted ? "completed" : "partial";
            WriteCoverageAttempt(attempt);
        }

        internal static void MarkCoverageExecutionStarting(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId)
                throw new InvalidOperationException("Coverage preparation state was lost.");
            attempt.phase = "starting";
            WriteCoverageAttempt(attempt);
        }

        internal static void ResetCoverageCounters()
        {
            if (!Coverage.enabled)
                throw new InvalidOperationException("Coverage must be enabled before resetting counters.");
            Coverage.ResetAll();
        }

        internal static void RestoreCoverageProfiler(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId)
                return;

            // Editor shutdown has no next domain or query to finish settlement.
            attempt.phase = "restoring";
            attempt.restoreCompilationExpected = Coverage.enabled != attempt.previousProfilerEnabled;
            attempt.restoreCompilationStarted = false;
            attempt.restoreCompilationFinished = false;
            WriteCoverageAttempt(attempt);
            if (Coverage.enabled != attempt.previousProfilerEnabled)
                Coverage.enabled = attempt.previousProfilerEnabled;
            attempt.restoreSettingApplied = true;
            WriteCoverageAttempt(attempt);
            SessionState.EraseString(CoverageAttemptKey);
        }

        internal static void RestoreAnyCoverageProfiler()
        {
            string runId = GetCoverageAttemptRunId();
            if (!string.IsNullOrEmpty(runId)) RestoreCoverageProfiler(runId);
        }

        internal static string CompleteCoverageAttempt(string runId)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId)
            {
                if (CapturedCoverageRunId() == runId) return "available|Coverage report was captured.";
                var runState = RunTestsHandler.ReadRunningState();
                return runState != null && runState.runId == runId && runState.coverage
                    ? "unavailable|Coverage preparation did not produce a report."
                    : null;
            }

            if (attempt.phase != "restoring")
            {
                attempt.terminalResultClaimed = true;
                if (attempt.testCompleted && attempt.runnerStarted)
                {
                    try
                    {
                        attempt.captureStatus = CaptureCoverage(runId) ? "available" : "unavailable";
                        attempt.captureMessage = attempt.captureStatus == "available"
                            ? "Coverage report captured for the completed test run."
                            : "Coverage counters were unavailable when the test run completed.";
                    }
                    catch (Exception ex)
                    {
                        attempt.captureStatus = "unavailable";
                        attempt.captureMessage = "Coverage capture failed: " + ex.Message;
                    }
                }
                else
                {
                    attempt.captureStatus = "unavailable";
                    attempt.captureMessage = "Coverage was not captured because the test run did not complete.";
                }

                attempt.phase = "restoring";
                attempt.restoreIdleUpdates = 0;
                attempt.restoreCompilationExpected = Coverage.enabled != attempt.previousProfilerEnabled;
                attempt.restoreCompilationStarted = false;
                attempt.restoreCompilationFinished = false;
                attempt.restoreRefreshRequested = UnityLeanMcpCompilationTracker.RefreshRequired;
                attempt.restoreRefreshCompleted = false;
                attempt.restoreRefreshIssued = false;
                attempt.restoreSettingApplied = false;
                attempt.restoreRequestIssued = false;
                attempt.restoreFailureMessage = null;
                if (UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
                    UnityLeanMcpCompilationTracker.CompilationInProgress)
                    attempt.restoreCompilationExpected = true;
                if (attempt.restoreRefreshRequested) attempt.restoreCompilationExpected = true;
                WriteCoverageAttempt(attempt);
            }

            try
            {
                ApplyCoverageRestoration(attempt);
                if (!AdvanceCoverageLifecycle(runId, restoration: true))
                    return "pending|Waiting for profiler restoration to settle.";
                attempt = ReadCoverageAttempt() ?? attempt;
                attempt.restoreFailureMessage = null;
                WriteCoverageAttempt(attempt);
            }
            catch (Exception ex)
            {
                attempt = ReadCoverageAttempt() ?? attempt;
                if (!string.Equals(attempt.restoreFailureMessage, ex.Message, StringComparison.Ordinal))
                {
                    attempt.restoreFailureMessage = ex.Message;
                    WriteCoverageAttempt(attempt);
                    WorkerDiagnosticsLogger.Warning(UnityLeanMcpPaths.LogFile,
                        "Coverage restoration will retry for test " + runId + ": " + ex.Message);
                }
                return "pending|Could not restore coverage yet: " + ex.Message;
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed || EditorUtility.scriptCompilationFailed)
            {
                ClearCapturedCoverage();
                attempt.captureStatus = "unavailable";
                attempt.captureMessage = "Profiler restoration compilation failed; coverage cannot be used until the project compiles successfully.";
            }
            else if (attempt.captureStatus == "available" && !CapturedCoverageMatchesCurrentInputs(runId))
            {
                ClearCapturedCoverage();
                attempt.captureStatus = "unavailable";
                attempt.captureMessage = "Compilation inputs changed during the test or profiler restoration, so the captured coverage is obsolete.";
            }

            string outcome = (attempt.captureStatus ?? "unavailable") + "|" +
                (attempt.captureMessage ?? "Coverage was not available for this test run.");
            SessionState.EraseString(CoverageAttemptKey);
            return outcome;
        }

        private static void ApplyCoverageRestoration(CoverageAttempt attempt)
        {
            if (UnityLeanMcpCompilationTracker.RefreshRequired && !attempt.restoreRefreshRequested)
            {
                attempt.restoreCompilationExpected = true;
                attempt.restoreCompilationStarted = false;
                attempt.restoreCompilationFinished = false;
                attempt.restoreRefreshRequested = true;
                attempt.restoreRefreshCompleted = false;
                attempt.restoreRefreshIssued = false;
                attempt.restoreRequestIssued = false;
                attempt.restoreIdleUpdates = 0;
                WriteCoverageAttempt(attempt);
            }

            if (attempt.restoreRefreshRequested && !attempt.restoreRefreshCompleted &&
                !attempt.restoreCompilationStarted && !IsEditorCompilationActive())
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                UnityLeanMcpCompilationTracker.RefreshRequired = false;
                attempt.restoreRefreshCompleted = true;
                WriteCoverageAttempt(attempt);
            }

            if (!attempt.restoreSettingApplied)
            {
                if (Coverage.enabled != attempt.previousProfilerEnabled)
                    Coverage.enabled = attempt.previousProfilerEnabled;
                attempt.restoreSettingApplied = true;
                WriteCoverageAttempt(attempt);
            }

            if (attempt.restoreRefreshRequested && !attempt.restoreRefreshIssued)
            {
                if (attempt.restoreCompilationStarted)
                {
                    attempt.restoreRefreshIssued = true;
                    WriteCoverageAttempt(attempt);
                }
                else if (!IsEditorCompilationActive())
                {
                    CompilationPipeline.RequestScriptCompilation();
                    var latest = ReadCoverageAttempt() ?? attempt;
                    if (latest.runId == attempt.runId)
                    {
                        latest.restoreRefreshIssued = true;
                        latest.restoreRequestIssued = true;
                        WriteCoverageAttempt(latest);
                    }
                }
            }
            else if (attempt.restoreCompilationExpected && !attempt.restoreCompilationStarted &&
                !attempt.restoreCompilationFinished && !attempt.restoreRequestIssued && !IsEditorCompilationActive())
            {
                CompilationPipeline.RequestScriptCompilation();
                var latest = ReadCoverageAttempt() ?? attempt;
                if (latest.runId == attempt.runId)
                {
                    latest.restoreRequestIssued = true;
                    WriteCoverageAttempt(latest);
                }
            }
        }

        private static string CapturedCoverageRunId()
        {
            string json = SessionState.GetString(CapturedCoverageKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<CapturedCoverage>(json)?.runId; }
            catch { return null; }
        }

        internal static bool CapturedCoverageMatchesCurrentInputs(string runId)
        {
            string json = SessionState.GetString(CapturedCoverageKey, "");
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var captured = JsonUtility.FromJson<CapturedCoverage>(json);
                return captured != null && captured.runId == runId && !captured.inputsChangedDuringRun &&
                    string.Equals(captured.inputIdentity, ComputeCompilationInputIdentity(), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static CoverageAttempt ReadCoverageAttempt()
        {
            string json = SessionState.GetString(CoverageAttemptKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<CoverageAttempt>(json); }
            catch { return null; }
        }

        private static void WriteCoverageAttempt(CoverageAttempt attempt) =>
            SessionState.SetString(CoverageAttemptKey, JsonUtility.ToJson(attempt));

        internal static bool CaptureCoverage(string runId)
        {
            string previousJson = SessionState.GetString(CapturedCoverageKey, "");
            if (!string.IsNullOrEmpty(previousJson) &&
                JsonUtility.FromJson<CapturedCoverage>(previousJson)?.runId == runId)
                return true;

            var attempt = ReadCoverageAttempt();
            if (attempt == null || attempt.runId != runId || !attempt.terminalResultClaimed || !attempt.testCompleted || !attempt.runnerStarted || attempt.captureAttempted)
                return false;

            // Claim the capture before consulting native counters. A reload or
            // publication retry must never recapture counters after restoration.
            attempt.captureAttempted = true;
            WriteCoverageAttempt(attempt);

            if (!Coverage.enabled)
                return false;

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

            string currentIdentity = ComputeCompilationInputIdentity();

            SessionState.SetString(CapturedCoverageKey, JsonUtility.ToJson(new CapturedCoverage
            {
                runId = runId,
                inputIdentity = attempt.inputIdentity,
                inputsChangedDuringRun = !string.Equals(attempt.inputIdentity, currentIdentity, StringComparison.Ordinal),
                files = reports
            }));
            return true;
        }

        internal static string ComputeCompilationInputIdentity() =>
            ComputeCompilationInputIdentity(out _);

        internal static string ComputeCompilationInputIdentity(out string diskFilesIdentity)
        {
            string projectRoot = Path.GetFullPath(UnityLeanMcpPaths.ProjectRoot);
            var manifest = new StringBuilder();
            var diskManifest = new StringBuilder();
            var diskPaths = CollectCompilationInputFiles(projectRoot);
            var contentPaths = new SortedSet<string>(diskPaths, PathComparer);

            AppendIdentity(manifest, "unity", Application.unityVersion);
            AppendIdentity(manifest, "optimization", CompilationPipeline.codeOptimization.ToString());

            var assemblies = CompilationPipeline.GetAssemblies() ?? new UnityEditor.Compilation.Assembly[0];
            foreach (var assembly in assemblies.OrderBy(a => a?.name, StringComparer.Ordinal))
            {
                if (assembly == null) continue;
                AppendIdentity(manifest, "assembly", assembly.name);
                AppendIdentity(manifest, "flags", assembly.flags);
                AppendIdentity(manifest, "defines", string.Join("\u001f", assembly.defines.OrderBy(d => d, StringComparer.Ordinal)));

                foreach (string source in assembly.sourceFiles.OrderBy(p => p, PathComparer))
                {
                    string path = ResolveCompilerPath(source, projectRoot);
                    AppendIdentity(manifest, "source", path);
                    contentPaths.Add(path);
                }

                foreach (var reference in assembly.assemblyReferences.OrderBy(r => r.name, StringComparer.Ordinal))
                {
                    AppendIdentity(manifest, "assembly-reference", reference.name);
                }

                foreach (string reference in assembly.compiledAssemblyReferences.OrderBy(p => p, PathComparer))
                {
                    AppendIdentity(manifest, "compiled-reference", NormalizeCompilerPath(reference, projectRoot));
                }

                var compilerOptions = assembly.compilerOptions;
                AppendIdentity(manifest, "compiler-allow-unsafe", compilerOptions.AllowUnsafeCode);
                AppendIdentity(manifest, "compiler-api-compatibility", compilerOptions.ApiCompatibilityLevel);
                AppendIdentity(manifest, "compiler-optimization", compilerOptions.CodeOptimization);
                AppendIdentity(manifest, "compiler-language-version", compilerOptions.LanguageVersion);
                AppendIdentity(manifest, "compiler-arguments", string.Join("\u001f", compilerOptions.AdditionalCompilerArguments ?? new string[0]));
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-response", compilerOptions.ResponseFiles, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-config", new[] { compilerOptions.AnalyzerConfigPath }, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-additional", compilerOptions.RoslynAdditionalFilePaths, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-dll", compilerOptions.RoslynAnalyzerDllPaths, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-rules", new[] { compilerOptions.RoslynAnalyzerRulesetPath }, projectRoot);
            }

            string[] userPrecompiled = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly) ?? new string[0];
            foreach (string path in userPrecompiled.Select(p => ResolveCompilerPath(p, projectRoot)).OrderBy(p => p, PathComparer))
            {
                AppendIdentity(manifest, "user-binary", path);
                contentPaths.Add(path);
            }

            using (var sha = SHA256.Create())
            {
                foreach (string path in contentPaths)
                {
                    string digest = GetFileContentDigest(path, projectRoot, sha);
                    AppendIdentity(manifest, "input-path", path);
                    AppendIdentity(manifest, "input-content", digest);
                    if (diskPaths.Contains(path))
                    {
                        AppendIdentity(diskManifest, "input-path", path);
                        AppendIdentity(diskManifest, "input-content", digest);
                    }
                }

                diskFilesIdentity = ComputeHexDigest(sha, diskManifest.ToString());
                return ComputeHexDigest(sha, manifest.ToString());
            }
        }

        private static string ComputeCompilationInputFilesIdentity()
        {
            string projectRoot = Path.GetFullPath(UnityLeanMcpPaths.ProjectRoot);
            var manifest = new StringBuilder();
            using (var sha = SHA256.Create())
            {
                foreach (string path in CollectCompilationInputFiles(projectRoot))
                {
                    AppendIdentity(manifest, "input-path", path);
                    AppendIdentity(manifest, "input-content", GetFileContentDigest(path, projectRoot, sha));
                }

                return ComputeHexDigest(sha, manifest.ToString());
            }
        }

        private static string GetFileContentDigest(string path, string projectRoot, SHA256 sha)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return "missing";

            bool cacheable = IsImmutablePackageOrEnginePath(path, projectRoot);
            long length = info.Length;
            long writeTicks = info.LastWriteTimeUtc.Ticks;
            if (cacheable &&
                s_ImmutablePackageFileDigestCache.TryGetValue(path, out var cached) &&
                cached.Length == length &&
                cached.LastWriteUtcTicks == writeTicks)
            {
                return cached.Digest;
            }

            using (var stream = info.OpenRead())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (cacheable)
                {
                    s_ImmutablePackageFileDigestCache[path] = new CachedFileDigest
                    {
                        Length = length,
                        LastWriteUtcTicks = writeTicks,
                        Digest = digest
                    };
                }
                return digest;
            }
        }

        private static string ComputeHexDigest(SHA256 sha, string text) =>
            BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");

        private static bool IsImmutablePackageOrEnginePath(string fullPath, string projectRoot)
        {
            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (IsUnderDirectory(fullPath, packageCacheRoot))
                return true;

            string editorContents = EditorApplication.applicationContentsPath;
            if (!string.IsNullOrWhiteSpace(editorContents))
            {
                try
                {
                    if (IsUnderDirectory(fullPath, Path.GetFullPath(editorContents)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static bool IsUnderDirectory(string candidatePath, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
                return false;
            string normCandidate = candidatePath.Replace('\\', '/').TrimEnd('/');
            string normDir = directoryPath.Replace('\\', '/').TrimEnd('/');
            return normCandidate.Equals(normDir, PathComparison) ||
                normCandidate.StartsWith(normDir + "/", PathComparison);
        }

        private static SortedSet<string> CollectCompilationInputFiles(string projectRoot)
        {
            var paths = new SortedSet<string>(PathComparer);
            var inputDirectories = new SortedSet<string>(PathComparer)
            {
                Path.Combine(projectRoot, "Assets"),
                Path.Combine(projectRoot, "Packages")
            };

            foreach (UnityPackageInfo package in UnityPackageInfo.GetAllRegisteredPackages() ?? new UnityPackageInfo[0])
            {
                if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath)) continue;
                string resolved = Path.GetFullPath(package.resolvedPath);
                if (IsImmutablePackageOrEnginePath(resolved, projectRoot))
                    continue;
                inputDirectories.Add(resolved);
                AddExistingInput(paths, Path.Combine(resolved, "package.json"));
            }

            foreach (string root in inputDirectories)
            {
                if (!Directory.Exists(root)) continue;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string directory = pending.Pop();
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string extension = Path.GetExtension(file);
                        string fileName = Path.GetFileName(file);
                        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".rsp", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".additionalfile", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".asmdef.meta", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".asmref.meta", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase))
                            paths.Add(Path.GetFullPath(file));
                    }

                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        string childName = Path.GetFileName(child);
                        if (string.IsNullOrEmpty(childName) || childName.StartsWith(".", StringComparison.Ordinal) ||
                            childName.EndsWith("~", StringComparison.Ordinal))
                            continue;
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        pending.Push(child);
                    }
                }
            }

            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "EditorSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "PackageManagerSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "Packages", "manifest.json"));
            AddExistingInput(paths, Path.Combine(projectRoot, "Packages", "packages-lock.json"));

            var assemblies = CompilationPipeline.GetAssemblies() ?? new UnityEditor.Compilation.Assembly[0];
            foreach (var assembly in assemblies)
            {
                if (assembly == null) continue;
                foreach (string source in assembly.sourceFiles ?? new string[0])
                    paths.Add(ResolveCompilerPath(source, projectRoot));
                var options = assembly.compilerOptions;
                AddPaths(paths, options.ResponseFiles, projectRoot);
                AddPaths(paths, new[] { options.AnalyzerConfigPath }, projectRoot);
                AddPaths(paths, options.RoslynAdditionalFilePaths, projectRoot);
                AddPaths(paths, options.RoslynAnalyzerDllPaths, projectRoot);
                AddPaths(paths, new[] { options.RoslynAnalyzerRulesetPath }, projectRoot);
            }

            string[] userPrecompiled = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly) ?? new string[0];
            foreach (string path in userPrecompiled)
            {
                string resolved = ResolveCompilerPath(path, projectRoot);
                paths.Add(resolved);
                if (File.Exists(resolved + ".meta")) paths.Add(resolved + ".meta");
            }
            return paths;
        }

        private static void AddPaths(SortedSet<string> paths, string[] values, string projectRoot)
        {
            foreach (string value in values ?? new string[0])
                if (!string.IsNullOrWhiteSpace(value)) paths.Add(ResolveCompilerPath(value, projectRoot));
        }

        private static void AddCompilerOptionFiles(StringBuilder manifest, SortedSet<string> contentPaths, string name, string[] paths, string projectRoot)
        {
            foreach (string path in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string resolved = ResolveCompilerPath(path, projectRoot);
                AppendIdentity(manifest, name, resolved);
                contentPaths.Add(resolved);
            }
        }

        private static string ResolveCompilerPath(string path, string projectRoot)
        {
            string normalized = NormalizeCompilerPath(path, projectRoot);
            // A Windows compiler path may be returned by a host running on
            // another OS in unit tests. Keep its identity without pretending it
            // is a local path.
            if (Path.DirectorySeparatorChar != '\\' && normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
                return normalized;
            if (Path.IsPathRooted(normalized)) return Path.GetFullPath(normalized);
            return Path.GetFullPath(Path.Combine(projectRoot, normalized));
        }

        private static string NormalizeCompilerPath(string path, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Replace('\\', '/');
        }

        private static void AddExistingInput(SortedSet<string> paths, string path)
        {
            if (File.Exists(path)) paths.Add(Path.GetFullPath(path));
        }

        private static void AppendIdentity(StringBuilder manifest, string name, object value)
        {
            manifest.Append(name ?? "").Append('\0').Append(value?.ToString() ?? "").Append('\n');
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
