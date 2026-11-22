using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityLeanMcp
{
    internal partial class RunTestsHandler
    {
        private sealed class CachedRunSnapshot
        {
            public readonly UnityTestRunState State;
            public readonly string Json;
            public CachedRunSnapshot(UnityTestRunState state, string json) { State = state; Json = json; }
        }

        private static UnityTestRunState s_CachedRunState;
        private static CachedRunSnapshot s_CachedRunSnapshot;
        private static string s_CancellationSignalRunId;
        private static string s_CancellationMonitorRunId;
        private static bool s_CancellationMonitorRegistered;
        private static string s_ActiveRunMonitorRunId;
        private static bool s_ActiveRunObservedActive;
        private static bool s_ActiveRunMonitorRegistered;

        internal static void ClearCachedRunState()
        {
            Volatile.Write(ref s_CachedRunState, null);
            Volatile.Write(ref s_CachedRunSnapshot, null);
        }

        private static void PublishCachedRunState(UnityTestRunState state)
        {
            if (state == null)
            {
                ClearCachedRunState();
                return;
            }

            Volatile.Write(ref s_CachedRunSnapshot, new CachedRunSnapshot(state, JsonUtility.ToJson(state)));
            Volatile.Write(ref s_CachedRunState, state);
        }

        private static string[] CloneStringArray(string[] source) =>
            source == null ? null : (string[])source.Clone();

        private static UnityTestRunState CloneRunningState(UnityTestRunState source)
        {
            if (source == null)
                return null;

            return new UnityTestRunState
            {
                runId = source.runId,
                mode = source.mode,
                testNames = CloneStringArray(source.testNames),
                groupNames = CloneStringArray(source.groupNames),
                categoryNames = CloneStringArray(source.categoryNames),
                assemblyNames = CloneStringArray(source.assemblyNames),
                coverage = source.coverage,
                status = source.status,
                jobGuid = source.jobGuid,
                startedUtc = source.startedUtc,
                totalTests = source.totalTests,
                completedTests = source.completedTests,
                passCount = source.passCount,
                failCount = source.failCount,
                skipCount = source.skipCount,
                currentTestName = source.currentTestName
            };
        }

        private static bool TryGetActiveTestOperationId(out string operationId)
        {
            var activeOp = UnityCommandGate.ReadSnapshot();
            operationId = activeOp != null && string.Equals(activeOp.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase)
                ? activeOp.OperationId
                : null;
            return operationId != null;
        }

        private static bool IsOwnedActiveRun(string runId) =>
            !string.IsNullOrEmpty(runId) &&
            UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test) &&
            !UnityCommandGate.HasPendingResult(runId);

        private static bool TryUpdateRunningState(string runId, Action<UnityTestRunState> update, string actionDesc, bool persistToDisk = true)
        {
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
                return false;

            var current = ReadRunningState();
            if (current == null || current.runId != runId)
                return false;

            var next = CloneRunningState(current);
            update(next);
            PublishCachedRunState(next);

            if (!persistToDisk)
                return true;

            try
            {
                UnityCommandGate.WriteAtomic(UnityLeanMcpPaths.TestRunningFile, JsonUtility.ToJson(next, true), runId);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to {actionDesc}: {ex.Message}");
                return false;
            }
        }

        internal static void MarkTransportInterruption(string status)
        {
            var state = ReadRunningState();
            if (state == null || string.IsNullOrEmpty(state.runId) ||
                state.status == OperationStatus.Cancelling || state.status == OperationStatus.Interrupted)
                return;
            TryUpdateRunningState(state.runId, s => s.status = status, "mark test run interruption");
        }

        internal static UnityTestRunState ReadRunningState()
        {
            var cached = Volatile.Read(ref s_CachedRunState);
            if (cached != null)
                return cached;

            try
            {
                if (!TryGetActiveTestOperationId(out string activeOpId) || !File.Exists(UnityLeanMcpPaths.TestRunningFile))
                    return null;

                string text = CommandHelper.ReadFileWithRetry(UnityLeanMcpPaths.TestRunningFile, maxRetries: 3, delayMs: 10);
                if (string.IsNullOrEmpty(text))
                    return null;

                var state = JsonUtility.FromJson<UnityTestRunState>(text);
                if (state == null || state.runId != activeOpId)
                    return null;

                var published = Interlocked.CompareExchange(ref s_CachedRunState, state, null);
                if (published == null)
                    Interlocked.CompareExchange(ref s_CachedRunSnapshot, new CachedRunSnapshot(state, JsonUtility.ToJson(state)), null);
                return published ?? state;
            }
            catch (Exception ex)
            {
                WorkerDiagnosticsLogger.Error(UnityLeanMcpPaths.LogFile, $"UnityLeanMcp: Failed to read test run state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Worker-thread-safe reader for POLL_TESTS. Uses pre-serialized JSON
        /// published on the main thread, and falls back to managed JSON parsing
        /// on cache miss without calling JsonUtility or Unity logging.
        /// </summary>
        internal static bool TryReadRunningStateJson(string operationId, out string json)
        {
            json = null;
            var state = Volatile.Read(ref s_CachedRunState);
            var snapshot = Volatile.Read(ref s_CachedRunSnapshot);
            if (state != null)
            {
                if (!string.IsNullOrEmpty(operationId) && state.runId != operationId)
                    return false;
                if (snapshot != null &&
                    (ReferenceEquals(snapshot.State, state) || snapshot.State.runId == state.runId) &&
                    !string.IsNullOrEmpty(snapshot.Json))
                {
                    json = snapshot.Json;
                    return true;
                }
            }

            try
            {
                if (!TryGetActiveTestOperationId(out string activeOpId) ||
                    (!string.IsNullOrEmpty(operationId) && activeOpId != operationId))
                    return false;

                string text = WorkerThreadSnapshots.ReadFileWithRetry(UnityLeanMcpPaths.TestRunningFile, maxRetries: 3, delayMs: 10);
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                var values = UnityLeanJson.DeserializeObject(text, StringComparer.OrdinalIgnoreCase);
                if (values == null || !values.TryGetValue("runId", out object runIdObj) || !(runIdObj is string runId) || runId != activeOpId)
                    return false;

                json = text;
                return true;
            }
            catch (Exception ex)
            {
                WorkerDiagnosticsLogger.Error(UnityLeanMcpPaths.LogFile, $"UnityLeanMcp: Failed to read test run state on worker: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Worker-thread-only view of the running test state. Cache misses are
        /// decoded with the managed worker codec; they must not call
        /// JsonUtility or Unity logging while the main thread is busy.
        /// </summary>
        internal static WorkerTestRunStateSnapshot ReadThreadSafeSnapshot()
        {
            var cached = Volatile.Read(ref s_CachedRunState);
            if (cached != null)
                return new WorkerTestRunStateSnapshot(cached.runId);

            return WorkerThreadSnapshots.TryReadTestRunState(UnityLeanMcpPaths.TestRunningFile, out var snapshot)
                ? snapshot
                : null;
        }

        internal static void UpdateTestRunStatus(string runId, string status) =>
            TryUpdateRunningState(runId, s => s.status = status, "update test run state");

        private static void UpdateTestRunJobGuid(string runId, string jobGuid) =>
            TryUpdateRunningState(runId, s => s.jobGuid = jobGuid, "persist test job identity");

        internal static void UpdateTestRunProgress(string runId, int totalTests, int completedTests, int passCount, int failCount, int skipCount, string currentTestName, string status = null)
        {
            TryUpdateRunningState(runId, next =>
            {
                if (!string.IsNullOrEmpty(status))
                    next.status = status;
                next.totalTests = totalTests;
                next.completedTests = completedTests;
                next.passCount = passCount;
                next.failCount = failCount;
                next.skipCount = skipCount;
                if (currentTestName != null)
                    next.currentTestName = currentTestName;
            }, null, persistToDisk: false);
        }

        private static void PublishTerminalResult(string resultState, string message, string targetRunId, bool requireCancellation = false)
        {
            var state = ReadRunningState();
            TryGetActiveTestOperationId(out string activeOpId);
            string runId = targetRunId ?? state?.runId ?? activeOpId;
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
                return;
            if (requireCancellation && !IsCancellationRequested(runId))
                return;

            var result = new UnityTestRunResult
            {
                runId = runId,
                success = false,
                message = message,
                resultState = resultState,
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                UnityCommandGate.PublishResult(OperationKinds.Test, runId, UnityLeanMcpPaths.GetTestResultsFile(runId), JsonUtility.ToJson(result, true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist {resultState.ToLowerInvariant()} test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        internal static void WriteInterruptedResult(string message, string targetRunId = null) =>
            PublishTerminalResult(OperationStatus.Interrupted, message, targetRunId);

        internal static void WriteCancelledResult(string targetRunId = null) =>
            PublishTerminalResult(OperationStatus.Cancelled, "Test run was cancelled or interrupted.", targetRunId, requireCancellation: true);

        private static void CancelActiveTestRunOnMainThread(string operationId)
        {
            if (!UnityCommandGate.IsOwnedBy(operationId, OperationKinds.Test))
                return;

            var state = ReadRunningState();
            var snapshot = UnityCommandGate.ReadSnapshot();
            bool hasMatchingState = state != null && state.runId == operationId;
            if ((hasMatchingState && state.status != OperationStatus.Cancelling) ||
                (snapshot != null && snapshot.Status != OperationStatus.Cancelling))
            {
                if (hasMatchingState)
                    UpdateTestRunStatus(operationId, OperationStatus.Cancelling);
                UnityCommandGate.Update(operationId, OperationStatus.Cancelling);
            }

            BeginCancellationMonitoring(operationId);

            string jobGuid = s_CurrentTestJobGuid ?? (hasMatchingState ? state.jobGuid : null);
            if (!string.IsNullOrEmpty(jobGuid) && s_CancellationSignalRunId != operationId)
            {
                try
                {
                    TestRunnerApi.CancelTestRun(jobGuid);
                    s_CancellationSignalRunId = operationId;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityLeanMcp: Exception while calling TestRunnerApi.CancelTestRun: {ex.Message}");
                }
            }

            // CancelTestRun is only a request. A terminal result may be
            // published by RunFinished, or by the update monitor once the
            // Test Runner reports that no run is active.
            TryCompleteCancellationIfRunnerTerminal(operationId);
        }

        internal static OperationCancelResult CancelActiveTestRun(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                return OperationCancelResult.NotFound;

            var operation = UnityCommandGate.ReadSnapshot();
            var runningState = operation == null ? ReadThreadSafeSnapshot() : null;

            if (operation == null && runningState == null)
            {
                string resPath = UnityLeanMcpPaths.GetTestResultsFile(operationId);
                bool wasInterrupted = WorkerThreadSnapshots.TryReadOperationResult(resPath, out var existing) &&
                    existing.OperationId == operationId &&
                    (existing.Interrupted || existing.ResultState == OperationStatus.Cancelled || existing.ResultState == OperationStatus.Interrupted);
                return wasInterrupted ? OperationCancelResult.Cancelled : OperationCancelResult.NotFound;
            }

            string ownerId = operation != null ? operation.OperationId : runningState?.RunId;
            if (ownerId != operationId)
                return OperationCancelResult.NotCancelable;

            if (!UnityCommandGate.TryRequestCancellation(operationId, out _) && !UnityCommandGate.IsCancellationRequested(operationId))
                return OperationCancelResult.NotFound;

            // Perform all Unity API and state-transition work on the main thread via the dispatcher.
            UnityLeanMcpDispatcher.Enqueue(() => CancelActiveTestRunOnMainThread(operationId));
            return CancelOperationHandler.IsTerminalInterruptedResult(operationId)
                ? OperationCancelResult.Cancelled
                : OperationCancelResult.Cancelling;
        }

        internal static bool IsCancellationRequested(string runId)
        {
            if (string.IsNullOrEmpty(runId))
                return false;
            if (UnityCommandGate.IsCancellationRequested(runId))
                return true;

            var snapshot = UnityCommandGate.ReadSnapshot();
            if (snapshot != null && snapshot.OperationId == runId && snapshot.Status == OperationStatus.Cancelling)
                return true;

            var state = ReadRunningState();
            return state != null && state.runId == runId && state.status == OperationStatus.Cancelling;
        }

        private static void BeginCancellationMonitoring(string runId)
        {
            s_CancellationMonitorRunId = runId;
            if (s_CancellationMonitorRegistered)
                return;
            EditorApplication.update -= ObserveCancellationState;
            EditorApplication.update += ObserveCancellationState;
            s_CancellationMonitorRegistered = true;
        }

        private static void ObserveCancellationState()
        {
            string runId = s_CancellationMonitorRunId;
            if (!IsOwnedActiveRun(runId))
                StopCancellationMonitoring(runId);
            else
                CancelActiveTestRunOnMainThread(runId);
        }

        private static void BeginActiveRunMonitoring(string runId)
        {
            if (!IsOwnedActiveRun(runId))
                return;

            s_ActiveRunMonitorRunId = runId;
            s_ActiveRunObservedActive = TryGetTestRunnerActiveState(out bool activeNow) && activeNow;
            if (s_ActiveRunMonitorRegistered)
                return;
            EditorApplication.update -= ObserveActiveTestRun;
            EditorApplication.update += ObserveActiveTestRun;
            s_ActiveRunMonitorRegistered = true;
        }

        internal static void StopActiveRunMonitoring(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && s_ActiveRunMonitorRunId != runId)
                return;

            if (s_ActiveRunMonitorRegistered)
            {
                EditorApplication.update -= ObserveActiveTestRun;
                s_ActiveRunMonitorRegistered = false;
            }
            s_ActiveRunMonitorRunId = null;
            s_ActiveRunObservedActive = false;
        }

        private static void ObserveActiveTestRun()
        {
            string runId = s_ActiveRunMonitorRunId;
            if (!IsOwnedActiveRun(runId))
            {
                StopActiveRunMonitoring(runId);
                return;
            }

            if (!TryGetTestRunnerActiveState(out bool isActive))
                return;

            if (isActive)
            {
                s_ActiveRunObservedActive = true;
                return;
            }

            if (!s_ActiveRunObservedActive)
                return;

            StopActiveRunMonitoring(runId);
            if (IsCancellationRequested(runId))
                WriteCancelledResult(runId);
            else
                WriteInterruptedResult("Test run ended without a completion callback.", runId);
        }

        private static void TryCompleteCancellationIfRunnerTerminal(string runId)
        {
            if (IsCancellationRequested(runId) && TryGetTestRunnerActiveState(out bool isActive) && !isActive)
                WriteCancelledResult(runId);
        }

        internal static void StopCancellationMonitoring(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && s_CancellationMonitorRunId != runId)
                return;

            if (s_CancellationMonitorRegistered)
            {
                EditorApplication.update -= ObserveCancellationState;
                s_CancellationMonitorRegistered = false;
            }
            s_CancellationMonitorRunId = null;
            if (string.IsNullOrEmpty(runId) || s_CancellationSignalRunId == runId)
                s_CancellationSignalRunId = null;
        }

        internal static bool DeleteRunningStateIfOwned(string runId)
        {
            if (string.IsNullOrEmpty(runId))
                return false;

            try
            {
                var state = ReadRunningState();
                if (state == null || state.runId != runId)
                    return false;

                var cached = Volatile.Read(ref s_CachedRunState);
                if (cached != null && cached.runId == runId &&
                    Interlocked.CompareExchange(ref s_CachedRunState, null, cached) == cached)
                {
                    Volatile.Write(ref s_CachedRunSnapshot, null);
                }

                return CommandHelper.DeleteFileWithRetry(UnityLeanMcpPaths.TestRunningFile);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to delete running state: {ex.Message}");
                return false;
            }
        }
    }
}
