#nullable disable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityLeanMcp
{
    /// <summary>Durable lifecycle phases for a single coverage-enabled test attempt.</summary>
    internal static class CoverageAttemptPhase
    {
        public const string BaselinePreparing = "baseline-preparing";
        public const string Enabling = "enabling";
        public const string Preparing = "preparing";
        public const string Starting = "starting";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Partial = "partial";
        public const string Restoring = "restoring";

        public static bool IsPreparationPhase(string phase) =>
            phase == BaselinePreparing || phase == Enabling || phase == Preparing;
    }

    internal enum CoverageCompletionState { None, Pending, Available, Unavailable }

    internal readonly struct CoverageCompletionOutcome
    {
        public const string AvailableStatus = "available";
        public const string UnavailableStatus = "unavailable";

        public CoverageCompletionState State { get; }
        public string Message { get; }
        public bool HasOutcome => State != CoverageCompletionState.None;
        public bool IsPending => State == CoverageCompletionState.Pending;

        public CoverageCompletionOutcome(CoverageCompletionState state, string message)
        {
            State = state;
            Message = message;
        }

        public string ToProtocolStatus() => State == CoverageCompletionState.Available ? AvailableStatus
            : State == CoverageCompletionState.Unavailable ? UnavailableStatus
            : State == CoverageCompletionState.Pending ? "pending" : null;

        public static CoverageCompletionOutcome None => new CoverageCompletionOutcome(CoverageCompletionState.None, null);
        public static CoverageCompletionOutcome Pending(string message) => new CoverageCompletionOutcome(CoverageCompletionState.Pending, message);
        public static CoverageCompletionOutcome Available(string message) => new CoverageCompletionOutcome(CoverageCompletionState.Available, message);
        public static CoverageCompletionOutcome Unavailable(string message) => new CoverageCompletionOutcome(CoverageCompletionState.Unavailable, message);
    }

    /// <summary>
    /// Manages the SessionState-backed transactional coverage attempt lifecycle
    /// across domain reloads, profiler enablement, native counter capture, and restoration.
    /// </summary>
    internal static class CoverageLifecycleManager
    {
        // One completed report is retained in native session state for this Editor
        // session. A separate attempt record is recovery state for the active test.
        internal const string CapturedCoverageKey = "UnityLeanMcp_CapturedCoverage";
        internal const string CoverageAttemptKey = "UnityLeanMcp_CoverageAttempt";

        [Serializable]
        internal sealed class CapturedCoverage
        {
            public string runId, inputIdentity;
            public bool inputsChangedDuringRun;
            public List<CoverageFileReport> files;
        }

        [Serializable]
        private sealed class CoverageAttempt
        {
            public string runId, inputIdentity, baselineDiskIdentity, phase;
            public bool previousProfilerEnabled;
            public bool baselineCompilationExpected, baselineCompilationStarted, baselineCompilationFinished, baselineRequestIssued;
            public int baselineIdleUpdates;
            public bool enableCompilationExpected, enableCompilationStarted, enableCompilationFinished, enableSettingApplied, enableRequestIssued;
            public int enableIdleUpdates;
            public bool restoreCompilationExpected, restoreCompilationStarted, restoreCompilationFinished;
            public bool restoreRefreshRequested, restoreRefreshCompleted, restoreRefreshIssued, restoreSettingApplied, restoreRequestIssued;
            public string restoreFailureMessage;
            public int restoreIdleUpdates;
            public bool runnerStarted, terminalResultClaimed, testCompleted, captureAttempted;
            public string captureStatus, captureMessage;
        }

        internal static CapturedCoverage ReadCapturedCoverage()
        {
            string capturedJson = SessionState.GetString(CapturedCoverageKey, "");
            if (string.IsNullOrEmpty(capturedJson)) return null;
            try { return JsonUtility.FromJson<CapturedCoverage>(capturedJson); }
            catch { return null; }
        }

        internal static void ClearCapturedCoverage() => SessionState.EraseString(CapturedCoverageKey);
        internal static void ClearCoverageAttempt() => SessionState.EraseString(CoverageAttemptKey);

        internal static void BeginCoverageAttempt(string runId)
        {
            // Invalidate the single prior report as soon as a new attempt is
            // admitted. A later setup failure must not expose stale coverage.
            ClearCapturedCoverage();
            var attempt = new CoverageAttempt
            {
                runId = runId,
                previousProfilerEnabled = Coverage.enabled,
                phase = CoverageAttemptPhase.BaselinePreparing,
                baselineCompilationExpected = true
            };
            // Reconcile disk imports and request compilation only after the
            // recovery record is durable. The baseline is finalized after this
            // compiler lifecycle, so a file edited after the last host refresh
            // cannot be mistaken for the source that produced loaded assemblies.
            IssueBaselineCompilationRequest(runId, attempt);
        }

        private static void IssueBaselineCompilationRequest(string runId, CoverageAttempt attempt)
        {
            attempt.baselineDiskIdentity = CompilationInputIdentityCalculator.ComputeCompilationInputFilesIdentity();
            WriteCoverageAttempt(attempt);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            CompilationPipeline.RequestScriptCompilation();
            UpdateAttemptIfMatching(runId, attempt, latest => latest.baselineRequestIssued = true);
        }

        internal static bool EstablishCoverageBaseline(string runId)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null || attempt.phase != CoverageAttemptPhase.BaselinePreparing) return false;
            string inputIdentity = CompilationInputIdentityCalculator.ComputeCompilationInputIdentity(out string currentDiskIdentity);
            if (!string.Equals(attempt.baselineDiskIdentity, currentDiskIdentity, StringComparison.Ordinal)) return false;

            attempt.inputIdentity = inputIdentity;
            attempt.baselineCompilationExpected = false;
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            attempt.phase = attempt.previousProfilerEnabled ? CoverageAttemptPhase.Preparing : CoverageAttemptPhase.Enabling;
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
                UpdateAttemptIfMatching(runId, attempt, latest => latest.enableSettingApplied = true);
            }
            return true;
        }

        internal static void MarkCoverageInstrumentationSettled(string runId)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null || attempt.phase != CoverageAttemptPhase.Enabling)
                throw new InvalidOperationException("Coverage instrumentation preparation state was lost.");
            attempt.enableCompilationExpected = false;
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            attempt.phase = CoverageAttemptPhase.Preparing;
            WriteCoverageAttempt(attempt);
        }

        internal static bool CoverageBaselineDiskInputsMatch(string runId) =>
            ReadMatchingAttempt(runId) is CoverageAttempt attempt &&
            string.Equals(attempt.baselineDiskIdentity, CompilationInputIdentityCalculator.ComputeCompilationInputFilesIdentity(), StringComparison.Ordinal);

        internal static void AbortCoveragePreparation(string runId)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null) return;
            if (!IsEditorCompilationActive())
            {
                if (attempt.phase == CoverageAttemptPhase.BaselinePreparing && !attempt.baselineCompilationStarted)
                    attempt.baselineCompilationExpected = false;
                if (attempt.phase == CoverageAttemptPhase.Enabling && !attempt.enableCompilationStarted)
                    attempt.enableCompilationExpected = false;
            }
            WriteCoverageAttempt(attempt);
        }

        internal static void ObserveCompilationStarted() => ObserveCompilationEvent(finished: false);
        internal static void ObserveCompilationFinished() => ObserveCompilationEvent(finished: true);

        private static void ObserveCompilationEvent(bool finished)
        {
            var attempt = ReadCoverageAttempt();
            if (attempt == null) return;
            bool baselinePhase = attempt.phase == CoverageAttemptPhase.BaselinePreparing || attempt.phase == CoverageAttemptPhase.Restoring;
            bool enablePhase = attempt.phase == CoverageAttemptPhase.Enabling || attempt.phase == CoverageAttemptPhase.Restoring;
            bool updated =
                UpdateCompilationFlags(finished, baselinePhase, attempt.baselineCompilationExpected, false,
                    ref attempt.baselineCompilationStarted, ref attempt.baselineCompilationFinished, ref attempt.baselineIdleUpdates) |
                UpdateCompilationFlags(finished, enablePhase, attempt.enableCompilationExpected, false,
                    ref attempt.enableCompilationStarted, ref attempt.enableCompilationFinished, ref attempt.enableIdleUpdates) |
                UpdateCompilationFlags(finished, attempt.phase == CoverageAttemptPhase.Restoring, attempt.restoreCompilationExpected, true,
                    ref attempt.restoreCompilationStarted, ref attempt.restoreCompilationFinished, ref attempt.restoreIdleUpdates);
            if (updated) WriteCoverageAttempt(attempt);
        }

        private static bool UpdateCompilationFlags(
            bool finished, bool inPhase, bool expected, bool forceStartOnFinish,
            ref bool started, ref bool completed, ref int idleUpdates)
        {
            if (!inPhase || !expected) return false;
            if (!finished) { started = true; completed = false; idleUpdates = 0; return true; }
            if (!started && !forceStartOnFinish) return false;
            if (forceStartOnFinish) started = true;
            completed = true;
            return true;
        }

        internal static bool AdvanceCoverageLifecycle(string runId, bool restoration)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null) return false;
            if (!IsEditorCompilationActive() && !restoration && TryStartPreparationStep(runId, attempt)) return false;
            if (IsEditorCompilationActive())
            {
                if (restoration) attempt.restoreIdleUpdates = 0;
                else if (attempt.phase == CoverageAttemptPhase.BaselinePreparing) attempt.baselineIdleUpdates = 0;
                else if (attempt.phase == CoverageAttemptPhase.Enabling) attempt.enableIdleUpdates = 0;
                WriteCoverageAttempt(attempt);
                return false;
            }

            if (restoration) return AdvanceRestorationSettlement(attempt);
            if (attempt.phase == CoverageAttemptPhase.BaselinePreparing)
                return AdvanceSettledPhase(attempt, ref attempt.baselineIdleUpdates,
                    attempt.baselineCompilationExpected && !attempt.baselineCompilationFinished,
                    attempt.baselineRequestIssued && !attempt.baselineCompilationStarted,
                    () => attempt.baselineCompilationFinished = true);
            if (attempt.phase == CoverageAttemptPhase.Enabling)
                return AdvanceSettledPhase(attempt, ref attempt.enableIdleUpdates,
                    attempt.enableCompilationExpected && !attempt.enableCompilationFinished,
                    attempt.enableSettingApplied && attempt.enableRequestIssued && !attempt.enableCompilationStarted,
                    () => attempt.enableCompilationFinished = true);
            return true;
        }

        private static bool TryStartPreparationStep(string runId, CoverageAttempt attempt)
        {
            if (attempt.phase == CoverageAttemptPhase.BaselinePreparing &&
                attempt.baselineCompilationExpected && !attempt.baselineCompilationStarted &&
                !attempt.baselineCompilationFinished && !attempt.baselineRequestIssued)
            {
                IssueBaselineCompilationRequest(runId, attempt);
                return true;
            }
            if (attempt.phase != CoverageAttemptPhase.Enabling || !attempt.enableCompilationExpected ||
                attempt.enableCompilationStarted || attempt.enableCompilationFinished)
                return false;
            if (!attempt.enableSettingApplied || !Coverage.enabled)
            {
                Coverage.enabled = true;
                UpdateAttemptIfMatching(runId, attempt, latest => latest.enableSettingApplied = true);
                return true;
            }
            else if (!attempt.enableRequestIssued)
            {
                CompilationPipeline.RequestScriptCompilation();
                UpdateAttemptIfMatching(runId, attempt, latest => latest.enableRequestIssued = true);
                return true;
            }
            return false;
        }

        private static bool AdvanceRestorationSettlement(CoverageAttempt attempt)
        {
            bool waitBaseline = attempt.baselineCompilationExpected && !attempt.baselineCompilationFinished;
            bool waitEnable = attempt.enableCompilationExpected && !attempt.enableCompilationFinished;
            bool waitRestore = attempt.restoreCompilationExpected && !attempt.restoreCompilationFinished;
            bool canFallback = attempt.restoreSettingApplied &&
                (!attempt.restoreRefreshRequested || attempt.restoreRefreshIssued) &&
                (attempt.restoreRequestIssued || Coverage.enabled == attempt.previousProfilerEnabled) &&
                (!waitBaseline || !attempt.baselineCompilationStarted) &&
                (!waitEnable || !attempt.enableCompilationStarted) &&
                (!waitRestore || !attempt.restoreCompilationStarted);

            return AdvanceSettledPhase(attempt, ref attempt.restoreIdleUpdates,
                waitBaseline || waitEnable || waitRestore, canFallback, () =>
                {
                    if (waitBaseline) attempt.baselineCompilationFinished = true;
                    if (waitEnable) attempt.enableCompilationFinished = true;
                    if (waitRestore) attempt.restoreCompilationFinished = true;
                });
        }

        private static bool AdvanceSettledPhase(
            CoverageAttempt attempt, ref int idleUpdates, bool waitingForCompilation, bool canFallback, Action onFallback)
        {
            if (!waitingForCompilation)
            {
                idleUpdates++;
                WriteCoverageAttempt(attempt);
                return idleUpdates >= 5;
            }
            if (canFallback)
            {
                if (++idleUpdates >= 20) { onFallback(); idleUpdates = 0; }
                WriteCoverageAttempt(attempt);
            }
            return false;
        }

        private static bool IsEditorCompilationActive() =>
            EditorApplication.isCompiling || EditorApplication.isUpdating ||
            UnityLeanMcpCompilationTracker.IsCompiling || UnityLeanMcpCompilationTracker.IsUpdating ||
            UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
            UnityLeanMcpCompilationTracker.CompilationInProgress;

        internal static bool IsCoverageTerminalResultClaimed(string runId) => ReadMatchingAttempt(runId)?.terminalResultClaimed == true;
        internal static bool IsCoverageAttempt(string runId) => ReadMatchingAttempt(runId) != null;
        internal static string GetCoverageAttemptPhase(string runId) => ReadMatchingAttempt(runId)?.phase;
        internal static string GetCoverageAttemptRunId() => ReadCoverageAttempt()?.runId;

        internal static bool CoverageInputsMatchAttempt(string runId) =>
            ReadMatchingAttempt(runId) is CoverageAttempt attempt &&
            string.Equals(attempt.inputIdentity, CompilationInputIdentityCalculator.ComputeCompilationInputIdentity(), StringComparison.Ordinal);

        internal static bool TryMarkCoverageRunnerStarted(string runId)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null || attempt.terminalResultClaimed) return false;
            attempt.runnerStarted = true;
            attempt.phase = CoverageAttemptPhase.Running;
            WriteCoverageAttempt(attempt);
            return true;
        }

        internal static void MarkCoverageTestCompleted(string runId, bool complete)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null || attempt.terminalResultClaimed) return;
            attempt.terminalResultClaimed = true;
            attempt.testCompleted = complete && attempt.runnerStarted;
            attempt.phase = attempt.testCompleted ? CoverageAttemptPhase.Completed : CoverageAttemptPhase.Partial;
            WriteCoverageAttempt(attempt);
        }

        internal static void MarkCoverageExecutionStarting(string runId)
        {
            var attempt = ReadMatchingAttempt(runId)
                ?? throw new InvalidOperationException("Coverage preparation state was lost.");
            attempt.phase = CoverageAttemptPhase.Starting;
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
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null) return;
            // Editor shutdown has no next domain or query to finish settlement.
            attempt.phase = CoverageAttemptPhase.Restoring;
            attempt.restoreCompilationExpected = Coverage.enabled != attempt.previousProfilerEnabled;
            attempt.restoreCompilationStarted = false;
            attempt.restoreCompilationFinished = false;
            WriteCoverageAttempt(attempt);
            if (Coverage.enabled != attempt.previousProfilerEnabled) Coverage.enabled = attempt.previousProfilerEnabled;
            attempt.restoreSettingApplied = true;
            WriteCoverageAttempt(attempt);
            ClearCoverageAttempt();
        }

        internal static void RestoreAnyCoverageProfiler()
        {
            string runId = GetCoverageAttemptRunId();
            if (!string.IsNullOrEmpty(runId)) RestoreCoverageProfiler(runId);
        }

        internal static CoverageCompletionOutcome CompleteCoverageAttempt(string runId)
        {
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null)
            {
                if (CapturedCoverageRunId() == runId)
                    return CoverageCompletionOutcome.Available("Coverage report was captured.");
                var runState = RunTestsHandler.ReadRunningState();
                return runState != null && runState.runId == runId && runState.coverage
                    ? CoverageCompletionOutcome.Unavailable("Coverage preparation did not produce a report.")
                    : CoverageCompletionOutcome.None;
            }
            if (attempt.phase != CoverageAttemptPhase.Restoring) BeginAttemptRestoration(runId, attempt);

            try
            {
                ApplyCoverageRestoration(attempt);
                attempt = ReadCoverageAttempt() ?? attempt;
                if (!AdvanceCoverageLifecycle(runId, restoration: true))
                    return CoverageCompletionOutcome.Pending("Waiting for profiler restoration to settle.");
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
                return CoverageCompletionOutcome.Pending("Could not restore coverage yet: " + ex.Message);
            }
            return FinalizeCoverageOutcome(runId, attempt);
        }

        private static CoverageCompletionOutcome FinalizeCoverageOutcome(string runId, CoverageAttempt attempt)
        {
            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed || EditorUtility.scriptCompilationFailed)
            {
                ClearCapturedCoverage();
                attempt.captureStatus = CoverageCompletionOutcome.UnavailableStatus;
                attempt.captureMessage = "Profiler restoration compilation failed; coverage cannot be used until the project compiles successfully.";
            }
            else if (attempt.captureStatus == CoverageCompletionOutcome.AvailableStatus && !CapturedCoverageMatchesCurrentInputs(runId))
            {
                ClearCapturedCoverage();
                attempt.captureStatus = CoverageCompletionOutcome.UnavailableStatus;
                attempt.captureMessage = "Compilation inputs changed during the test or profiler restoration, so the captured coverage is obsolete.";
            }
            string message = attempt.captureMessage ?? "Coverage was not available for this test run.";
            var outcome = string.Equals(attempt.captureStatus, CoverageCompletionOutcome.AvailableStatus, StringComparison.Ordinal)
                ? CoverageCompletionOutcome.Available(message)
                : CoverageCompletionOutcome.Unavailable(message);
            UnityLeanMcpCompilationTracker.RefreshRequired = false;
            ClearCoverageAttempt();
            return outcome;
        }

        private static void BeginAttemptRestoration(string runId, CoverageAttempt attempt)
        {
            attempt.terminalResultClaimed = true;
            if (attempt.testCompleted && attempt.runnerStarted)
            {
                try
                {
                    bool captured = CaptureCoverage(runId);
                    attempt.captureStatus = captured ? CoverageCompletionOutcome.AvailableStatus : CoverageCompletionOutcome.UnavailableStatus;
                    attempt.captureMessage = captured
                        ? "Coverage report captured for the completed test run."
                        : "Coverage counters were unavailable when the test run completed.";
                }
                catch (Exception ex)
                {
                    attempt.captureStatus = CoverageCompletionOutcome.UnavailableStatus;
                    attempt.captureMessage = "Coverage capture failed: " + ex.Message;
                }
            }
            else
            {
                attempt.captureStatus = CoverageCompletionOutcome.UnavailableStatus;
                attempt.captureMessage = "Coverage was not captured because the test run did not complete.";
            }
            if (!IsEditorCompilationActive())
            {
                if (!attempt.baselineCompilationStarted) attempt.baselineCompilationExpected = false;
                if (!attempt.enableCompilationStarted) attempt.enableCompilationExpected = false;
            }
            bool compilationActive = UnityLeanMcpCompilationTracker.CompilationInProgress ||
                EditorApplication.isCompiling || UnityLeanMcpCompilationTracker.IsCompiling;
            bool compilationExpected = Coverage.enabled != attempt.previousProfilerEnabled ||
                UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
                UnityLeanMcpCompilationTracker.CompilationInProgress || UnityLeanMcpCompilationTracker.RefreshRequired;
            attempt.phase = CoverageAttemptPhase.Restoring;
            attempt.restoreFailureMessage = null;
            ResetRestorationFlags(attempt, compilationExpected, compilationActive, UnityLeanMcpCompilationTracker.RefreshRequired);
            WriteCoverageAttempt(attempt);
        }

        private static void ResetRestorationFlags(CoverageAttempt attempt, bool expected, bool started, bool refreshRequested)
        {
            attempt.restoreIdleUpdates = 0;
            attempt.restoreCompilationExpected = expected;
            attempt.restoreCompilationStarted = started;
            attempt.restoreCompilationFinished = false;
            attempt.restoreRefreshRequested = refreshRequested;
            attempt.restoreRefreshCompleted = false;
            attempt.restoreRefreshIssued = false;
            attempt.restoreSettingApplied = false;
            attempt.restoreRequestIssued = false;
        }

        private static void ApplyCoverageRestoration(CoverageAttempt attempt)
        {
            string expectedRunId = attempt.runId;
            attempt = ReadCoverageAttempt() ?? attempt;
            if (UnityLeanMcpCompilationTracker.RefreshRequired && !attempt.restoreRefreshRequested && !attempt.restoreSettingApplied)
            {
                ResetRestorationFlags(attempt, expected: true, started: false, refreshRequested: true);
                WriteCoverageAttempt(attempt);
            }
            if (attempt.restoreRefreshRequested && !attempt.restoreRefreshCompleted &&
                !attempt.restoreCompilationStarted && !IsEditorCompilationActive())
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                UnityLeanMcpCompilationTracker.RefreshRequired = false;
                UpdateAttemptIfMatching(expectedRunId, attempt, latest => latest.restoreRefreshCompleted = true);
            }
            if (!attempt.restoreSettingApplied)
            {
                if (Coverage.enabled != attempt.previousProfilerEnabled) Coverage.enabled = attempt.previousProfilerEnabled;
                UpdateAttemptIfMatching(expectedRunId, attempt, latest => latest.restoreSettingApplied = true);
            }
            attempt = ReadCoverageAttempt() ?? attempt;
            bool needsRefreshCompile = attempt.restoreRefreshRequested && !attempt.restoreRefreshIssued;
            if (needsRefreshCompile && attempt.restoreCompilationStarted)
            {
                attempt.restoreRefreshIssued = true;
                WriteCoverageAttempt(attempt);
            }
            else if ((needsRefreshCompile || (attempt.restoreCompilationExpected && !attempt.restoreCompilationStarted &&
                !attempt.restoreCompilationFinished && !attempt.restoreRequestIssued)) && !IsEditorCompilationActive())
            {
                CompilationPipeline.RequestScriptCompilation();
                UpdateAttemptIfMatching(expectedRunId, attempt, latest =>
                {
                    if (needsRefreshCompile) latest.restoreRefreshIssued = true;
                    latest.restoreRequestIssued = true;
                });
            }
        }

        private static string CapturedCoverageRunId() => ReadCapturedCoverage()?.runId;
        private static CoverageAttempt ReadMatchingAttempt(string runId) => ReadCoverageAttempt() is CoverageAttempt a && a.runId == runId ? a : null;
        private static void WriteCoverageAttempt(CoverageAttempt attempt) => SessionState.SetString(CoverageAttemptKey, JsonUtility.ToJson(attempt));

        internal static bool CapturedCoverageMatchesCurrentInputs(string runId)
        {
            var captured = ReadCapturedCoverage();
            return captured != null && captured.runId == runId && !captured.inputsChangedDuringRun &&
                string.Equals(captured.inputIdentity, CompilationInputIdentityCalculator.ComputeCompilationInputIdentity(), StringComparison.Ordinal);
        }

        private static CoverageAttempt ReadCoverageAttempt()
        {
            string json = SessionState.GetString(CoverageAttemptKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<CoverageAttempt>(json); }
            catch { return null; }
        }

        private static void UpdateAttemptIfMatching(string runId, CoverageAttempt fallback, Action<CoverageAttempt> mutate)
        {
            var latest = ReadCoverageAttempt() ?? fallback;
            if (latest.runId != runId) return;
            mutate(latest);
            WriteCoverageAttempt(latest);
        }

        internal static bool CaptureCoverage(string runId)
        {
            if (CapturedCoverageRunId() == runId) return true;
            var attempt = ReadMatchingAttempt(runId);
            if (attempt == null || !attempt.terminalResultClaimed ||
                !attempt.testCompleted || !attempt.runnerStarted || attempt.captureAttempted)
                return false;

            // Claim the capture before consulting native counters. A reload or
            // publication retry must never recapture counters after restoration.
            attempt.captureAttempted = true;
            WriteCoverageAttempt(attempt);
            if (!Coverage.enabled) return false;

            string projectRoot = UnityLeanMcpPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            var reports = CoverageSequencePointHarvester.HarvestCoverageReports(projectRoot);
            string currentIdentity = CompilationInputIdentityCalculator.ComputeCompilationInputIdentity();
            var captured = new CapturedCoverage
            {
                runId = runId,
                inputIdentity = attempt.inputIdentity,
                inputsChangedDuringRun = !string.Equals(attempt.inputIdentity, currentIdentity, StringComparison.Ordinal),
                files = reports
            };
            SessionState.SetString(CapturedCoverageKey, JsonUtility.ToJson(captured));
            return true;
        }
    }
}
