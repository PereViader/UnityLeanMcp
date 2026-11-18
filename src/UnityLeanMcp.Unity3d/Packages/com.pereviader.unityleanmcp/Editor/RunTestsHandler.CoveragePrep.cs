using System;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal partial class RunTestsHandler
    {
        private static string s_CoveragePreparationRunId;
        private static bool s_CoveragePreparationRegistered;

        internal static bool IsPlayModeCoverageSupported(string version = null)
        {
            version ??= Application.unityVersion;
            if (string.IsNullOrEmpty(version))
            {
                return false;
            }

            string[] parts = version.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            if (int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
            {
                if (major == 6000 || major == 6)
                {
                    return minor >= 7;
                }
                if (major > 6000 || (major > 6 && major < 1000))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ScheduleCoveragePreparation(string runId)
        {
            if (string.IsNullOrEmpty(runId) || !CoverageLifecycleManager.IsCoverageAttempt(runId))
                return;
            s_CoveragePreparationRunId = runId;
            if (s_CoveragePreparationRegistered)
                EditorApplication.update -= ContinueCoveragePreparation;
            EditorApplication.update += ContinueCoveragePreparation;
            s_CoveragePreparationRegistered = true;
        }

        private static void StopCoveragePreparation()
        {
            if (s_CoveragePreparationRegistered)
                EditorApplication.update -= ContinueCoveragePreparation;
            s_CoveragePreparationRegistered = false;
            s_CoveragePreparationRunId = null;
        }

        private static void ContinueCoveragePreparation()
        {
            string runId = s_CoveragePreparationRunId;
            var operation = UnityCommandGate.ReadSnapshot();
            if (string.IsNullOrEmpty(runId) || operation == null || operation.OperationId != runId)
            {
                StopCoveragePreparation();
                return;
            }

            var state = ReadRunningState();
            if (state == null || state.runId != runId || !CoverageLifecycleManager.IsCoverageAttempt(runId))
            {
                StopCoveragePreparation();
                CoverageLifecycleManager.AbortCoveragePreparation(runId);
                WriteInterruptedResult("Coverage preparation state was lost.", runId);
                return;
            }

            if (IsCancellationRequested(runId))
            {
                StopCoveragePreparation();
                WriteCancelledResult(runId);
                return;
            }

            string phase = CoverageLifecycleManager.GetCoverageAttemptPhase(runId);
            if (!CoverageAttemptPhase.IsPreparationPhase(phase))
            {
                StopCoveragePreparation();
                return;
            }

            try
            {
                if (!CoverageLifecycleManager.AdvanceCoverageLifecycle(runId, restoration: false))
                    return;

                if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed || EditorUtility.scriptCompilationFailed)
                {
                    StopCoveragePreparation();
                    WriteInterruptedResult("Coverage preparation compilation failed. Fix the compiler errors and rerun tests with coverage enabled.", runId);
                    return;
                }

                if (!TryGetTestRunnerActiveState(out bool isActive) || isActive)
                    return;

                if (phase == CoverageAttemptPhase.BaselinePreparing)
                {
                    if (!CoverageLifecycleManager.EstablishCoverageBaseline(runId))
                    {
                        StopCoveragePreparation();
                        WriteInterruptedResult("Compilation inputs changed while the coverage baseline was being established. Refresh the project and rerun tests with coverage enabled.", runId);
                        return;
                    }
                    return;
                }

                if (phase == CoverageAttemptPhase.Enabling)
                {
                    if (!UnityEngine.TestTools.Coverage.enabled || !CoverageLifecycleManager.CoverageInputsMatchAttempt(runId))
                    {
                        StopCoveragePreparation();
                        WriteInterruptedResult("Compilation inputs changed while coverage instrumentation settled. Refresh the project and rerun tests with coverage enabled.", runId);
                        return;
                    }
                    CoverageLifecycleManager.MarkCoverageInstrumentationSettled(runId);
                    return;
                }

                if (!UnityEngine.TestTools.Coverage.enabled)
                {
                    StopCoveragePreparation();
                    WriteInterruptedResult("Coverage profiler was disabled before tests could start.", runId);
                    return;
                }

                // Enabling native coverage can trigger a Unity compilation. The
                // pre-enable fingerprint identifies the refreshed code; if any
                // inputs changed while instrumentation settled, do not measure a
                // different assembly graph under the old identity.
                if (!CoverageLifecycleManager.CoverageInputsMatchAttempt(runId))
                {
                    StopCoveragePreparation();
                    WriteInterruptedResult("Compilation inputs changed while coverage instrumentation settled. Refresh the project and rerun tests with coverage enabled.", runId);
                    return;
                }

                CoverageLifecycleManager.ResetCoverageCounters();
                StopCoveragePreparation();

                var args = new RunTestsArgs
                {
                    mode = state.mode,
                    testNames = state.testNames,
                    groupNames = state.groupNames,
                    categoryNames = state.categoryNames,
                    assemblyNames = state.assemblyNames,
                    coverage = true
                };
                RunTests(ParseTestMode(state.mode), args, runId);
            }
            catch (Exception ex)
            {
                StopCoveragePreparation();
                CoverageLifecycleManager.AbortCoveragePreparation(runId);
                Debug.LogError($"UnityLeanMcp: Failed to prepare coverage: {ex}");
                WriteInterruptedResult("Failed to prepare code coverage: " + ex.Message, runId);
            }
        }

        internal static void MarkCoverageRunStarted(string runId) =>
            CoverageLifecycleManager.TryMarkCoverageRunnerStarted(runId);

        internal static void MarkCoverageRunCompleted(string runId, bool complete) =>
            CoverageLifecycleManager.MarkCoverageTestCompleted(runId, complete);

        // Called by terminal-result publication after the exact test payload and
        // coverage recovery record are persisted. Capture is frozen in
        // SessionState before the prior native profiler flag can discard counters.
        internal static CoverageCompletionOutcome CompleteCoverageForTerminalResult(string runId) =>
            CoverageLifecycleManager.CompleteCoverageAttempt(runId);

        private static void RestoreCoverageOnEditorQuit() => CoverageLifecycleManager.RestoreAnyCoverageProfiler();
    }
}
