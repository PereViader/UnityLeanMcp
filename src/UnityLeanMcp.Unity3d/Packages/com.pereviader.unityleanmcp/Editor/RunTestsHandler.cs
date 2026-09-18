using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class RunTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => true;

        private const string CallbackOwnerName = "UnityLeanMcp.CallbackOwner";
        private static MyTestCallbacks s_Callbacks;
        private static TestRunnerApi s_RunnerApi;
        private static MethodInfo s_IsRunActiveMethod;
        internal static string s_CurrentTestJobGuid;
        private static readonly object s_RunStateLock = new object();
        private static UnityTestRunState s_CachedRunState;
        private static string s_CancellationSignalRunId;
        private static string s_CancellationMonitorRunId;
        private static bool s_CancellationMonitorRegistered;

        internal static void ClearCachedRunState()
        {
            lock (s_RunStateLock)
            {
                s_CachedRunState = null;
            }
        }

        private static bool TryUpdateRunningState(string runId, Action<UnityTestRunState> update, string actionDesc)
        {
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
                return false;

            var state = ReadRunningState();
            if (state == null || state.runId != runId)
                return false;

            update(state);
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }

            try
            {
                UnityLeanMcpOperationStore.WriteAtomic(UnityLeanMcpPaths.TestRunningFile, JsonUtility.ToJson(state, true), runId);
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
            if (state == null || string.IsNullOrEmpty(state.runId))
            {
                return;
            }

            TryUpdateRunningState(state.runId, s => s.status = status, "mark test run interruption");
        }

        public static void RegisterCallbacks()
        {
            if (CommandHelper.IsAssetImportWorkerProcess())
            {
                return;
            }

            // Remove only callback owners created by this package. Destroying
            // every TestRunnerApi object breaks other editor tooling.
            foreach (var api in Resources.FindObjectsOfTypeAll<TestRunnerApi>())
            {
                if (api != null && api.name == CallbackOwnerName)
                {
                    try { UnityEngine.Object.DestroyImmediate(api); } catch { }
                }
            }

            s_Callbacks = new MyTestCallbacks();
            s_RunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_RunnerApi.name = CallbackOwnerName;
            s_RunnerApi.hideFlags = HideFlags.HideAndDontSave;
            s_RunnerApi.RegisterCallbacks(s_Callbacks);
            var runningState = ReadRunningState();
            if (runningState != null)
            {
                s_CurrentTestJobGuid = runningState.jobGuid;
                s_Callbacks.BindRun(runningState.runId);
                if (IsCancellationRequested(runningState.runId) || runningState.status == OperationStatus.Cancelling)
                {
                    CancelActiveTestRunOnMainThread(runningState.runId);
                }
            }
        }

        public static bool IsTestRunActive()
        {
            return TryGetTestRunnerActiveState(out bool isActive) && isActive;
        }

        private static bool TryGetTestRunnerActiveState(out bool isActive)
        {
            isActive = false;
            try
            {
                s_IsRunActiveMethod ??= typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (s_IsRunActiveMethod == null)
                {
                    return false;
                }

                object value = s_IsRunActiveMethod.Invoke(null, null);
                if (!(value is bool))
                {
                    return false;
                }

                isActive = (bool)value;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to check IsRunActive: {ex}");
                return false;
            }
        }

        public void Handle(string payload, StreamWriter writer)
        {
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

            if (string.IsNullOrEmpty(payload))
            {
                writer.WriteLine("ERROR: Missing arguments");
                return;
            }

            string trimmedPayload = (payload ?? "").Trim();
            string[] requestParts = trimmedPayload.Split(new[] { ' ' }, 2);
            if (requestParts.Length < 2 || string.IsNullOrWhiteSpace(requestParts[0]))
            {
                writer.WriteLine("ERROR: Missing operation id or test parameters");
                return;
            }

            string operationId = requestParts[0];
            string remainder = requestParts[1].Trim();

            if (!remainder.StartsWith("{", StringComparison.Ordinal))
            {
                writer.WriteLine("ERROR: Invalid test parameters. Expected JSON payload for RunTestsArgs.");
                return;
            }

            string unescapedJson = ProtocolCodec.UnescapeLine(remainder);
            RunTestsArgs testArgs = JsonUtility.FromJson<RunTestsArgs>(unescapedJson) ?? new RunTestsArgs();
            TestMode mode = ParseTestMode(testArgs.mode);

            if ((int)mode == -1)
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be all, playmode, or editmode");
                return;
            }

            if (!TryValidateFilterValues(testArgs, out string filterError))
            {
                writer.WriteLine($"ERROR: {filterError}");
                return;
            }

            var begin = UnityLeanMcpOperationStore.TryBegin(operationId, OperationKinds.Test, OperationStatus.Queued, out var existing);
            if (begin == BeginOperationResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginOperationResult.Busy)
            {
                writer.WriteLine($"BUSY {existing.kind} {existing.operationId}");
                return;
            }

            if (begin == BeginOperationResult.AlreadyStarted)
            {
                writer.WriteLine("RUNNING");
                writer.Flush();
                return;
            }

            try
            {
                PrepareCancellationRequest(operationId);
                List<string> failedTests = null;
                if (testArgs.failedOnly)
                {
                    failedTests = GetPreviouslyFailedTestNames();
                    if (failedTests.Count == 0)
                    {
                        var emptyResult = new UnityTestRunResult
                        {
                            runId = operationId,
                            success = true,
                            failCount = 0,
                            passCount = 0,
                            skipCount = 0,
                            message = "No previously failed tests found.",
                            resultState = "Passed",
                            failedTests = new List<FailedTestInfo>()
                        };
                        UnityLeanMcpOperationStore.WriteAtomic(UnityLeanMcpPaths.GetTestResultsFile(operationId), JsonUtility.ToJson(emptyResult, true), operationId);
                        UnityLeanMcpOperationStore.Complete(operationId);
                        writer.WriteLine("SUCCESS No previously failed tests found.");
                        writer.Flush();
                        return;
                    }
                }

                // Persist the complete run identity before acknowledging the command.
                // The client can therefore recover if this socket is closed by a reload
                // immediately after the command is dispatched.
                string runId = WriteTestRunningState(operationId, mode, testArgs);
                if (string.IsNullOrEmpty(runId))
                {
                    writer.WriteLine("ERROR: Could not persist test run state.");
                    writer.Flush();
                    UnityLeanMcpOperationStore.Complete(operationId);
                    return;
                }

                writer.WriteLine("RUNNING");
                writer.Flush();

                RunTests(mode, testArgs, runId, failedTests?.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Unhandled exception during RunTests: {ex}");
                WriteInterruptedResult("Failed to start test run: " + ex.Message, operationId);
            }
        }

        private static TestMode ParseTestMode(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "playmode":
                    return TestMode.PlayMode;
                case "editmode":
                    return TestMode.EditMode;
                case "all":
                    return TestMode.EditMode | TestMode.PlayMode;
                default:
                    return (TestMode)(-1);
            }
        }

        private static bool TryValidateFilterValues(RunTestsArgs args, out string error)
        {
            return TryValidateFilterValues("testNames", args.testNames, out error) &&
                TryValidateFilterValues("groupNames", args.groupNames, out error) &&
                TryValidateFilterValues("categoryNames", args.categoryNames, out error) &&
                TryValidateFilterValues("assemblyNames", args.assemblyNames, out error);
        }

        private static bool TryValidateFilterValues(string parameterName, string[] values, out string error)
        {
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    // Empty, whitespace-only, or null entries must never be
                    // silently broadened into an unfiltered test run.
                    if (string.IsNullOrWhiteSpace(values[i]))
                    {
                        error = $"Invalid test filter '{parameterName}[{i}]': value must not be empty or whitespace-only.";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static List<string> GetPreviouslyFailedTestNames()
        {
            var failedNames = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string path = UnityLeanMcpPaths.TestResultsFile;
                if (!File.Exists(path) && Directory.Exists(UnityLeanMcpPaths.TempDir))
                {
                    var files = new DirectoryInfo(UnityLeanMcpPaths.TempDir).GetFiles("unity_test_*.json");
                    if (files.Length > 0)
                    {
                        Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                        path = files[0].FullName;
                    }
                }

                if (File.Exists(path))
                {
                    string json = CommandHelper.ReadFileWithRetry(path, maxRetries: 3, delayMs: 10);
                    var result = JsonUtility.FromJson<UnityTestRunResult>(json);
                    if (result?.failedTests != null)
                    {
                        foreach (var fail in result.failedTests)
                        {
                            string name = !string.IsNullOrEmpty(fail.fullName) ? fail.fullName : fail.name;
                            if (!string.IsNullOrEmpty(name))
                            {
                                failedNames.Add(name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to read previous test results: {ex.Message}");
            }
            return new List<string>(failedNames);
        }

        private static string WriteTestRunningState(string runId, TestMode mode, RunTestsArgs args)
        {
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return null;
            }

            try
            {
                if (!Directory.Exists(UnityLeanMcpPaths.TempDir))
                {
                    Directory.CreateDirectory(UnityLeanMcpPaths.TempDir);
                }

                var state = new UnityTestRunState
                {
                    runId = runId,
                    mode = mode.ToString(),
                    testNames = args.testNames,
                    groupNames = args.groupNames,
                    categoryNames = args.categoryNames,
                    assemblyNames = args.assemblyNames,
                    status = OperationStatus.Queued,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    totalTests = 0,
                    completedTests = 0,
                    passCount = 0,
                    failCount = 0,
                    skipCount = 0,
                    currentTestName = ""
                };
                lock (s_RunStateLock)
                {
                    s_CachedRunState = state;
                }
                UnityLeanMcpOperationStore.WriteAtomic(UnityLeanMcpPaths.TestRunningFile, JsonUtility.ToJson(state, true), runId);
                return runId;
            }
            catch (Exception ex)
            {
                lock (s_RunStateLock)
                {
                    s_CachedRunState = null;
                }
                Debug.LogError($"UnityLeanMcp: Failed to write test running state: {ex}");
                return null;
            }
        }

        private static string[] NonEmptyOrNull(string[] array) => array != null && array.Length > 0 ? array : null;

        private static void RunTests(TestMode mode, RunTestsArgs args, string runId, string[] explicitTestNames = null)
        {
            try
            {
                if (s_Callbacks == null)
                {
                    RegisterCallbacks();
                }

                string[] effectiveTestNames = NonEmptyOrNull(explicitTestNames) ?? NonEmptyOrNull(args.testNames);

                var filter = new Filter
                {
                    testMode = mode,
                    testNames = effectiveTestNames,
                    groupNames = NonEmptyOrNull(args.groupNames),
                    categoryNames = NonEmptyOrNull(args.categoryNames),
                    assemblyNames = NonEmptyOrNull(args.assemblyNames)
                };

                UpdateTestRunStatus(runId, OperationStatus.Running);
                UnityLeanMcpOperationStore.Update(runId, OperationStatus.Executing);
                s_Callbacks.BindRun(runId);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityLeanMcp: Executing {mode} tests with testNames count '{(filter.testNames?.Length ?? 0)}', groupNames count '{(filter.groupNames?.Length ?? 0)}', categoryNames count '{(filter.categoryNames?.Length ?? 0)}', assemblyNames count '{(filter.assemblyNames?.Length ?? 0)}'...");
                s_CurrentTestJobGuid = s_RunnerApi.Execute(settings);
                UpdateTestRunJobGuid(runId, s_CurrentTestJobGuid);

                // A cancellation request may have been durably recorded while
                // this command was waiting to reach the main thread. Apply it
                // after Execute returns, when the job identity is available.
                if (IsCancellationRequested(runId))
                {
                    CancelActiveTestRunOnMainThread(runId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to start tests: {ex}");
                if (s_Callbacks != null)
                {
                    s_Callbacks.OnInfrastructureFailure("Failed to start test run: " + ex.Message);
                }
                else
                {
                    WriteInterruptedResult("Failed to start test run: " + ex.Message);
                }
            }
        }

        internal static UnityTestRunState ReadRunningState()
        {
            lock (s_RunStateLock)
            {
                if (s_CachedRunState != null)
                {
                    return s_CachedRunState; // Thread-safe in-memory fast path for worker threads
                }
            }
            try
            {
                if (!File.Exists(UnityLeanMcpPaths.TestRunningFile))
                {
                    return null;
                }

                string text = CommandHelper.ReadFileWithRetry(UnityLeanMcpPaths.TestRunningFile, maxRetries: 3, delayMs: 10);
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                var state = JsonUtility.FromJson<UnityTestRunState>(text);
                lock (s_RunStateLock)
                {
                    s_CachedRunState = state;
                }
                return state;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to read test run state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Worker-thread-only view of the running test state. Cache misses are
        /// decoded with the managed worker codec; they must not call
        /// JsonUtility or Unity logging while the main thread is busy.
        /// </summary>
        internal static WorkerTestRunStateSnapshot ReadThreadSafeSnapshot()
        {
            lock (s_RunStateLock)
            {
                if (s_CachedRunState != null)
                {
                    return new WorkerTestRunStateSnapshot(s_CachedRunState.runId);
                }
            }

            return WorkerThreadSnapshots.TryReadTestRunState(UnityLeanMcpPaths.TestRunningFile, out var snapshot)
                ? snapshot
                : null;
        }

        internal static void UpdateTestRunStatus(string runId, string status)
        {
            TryUpdateRunningState(runId, s => s.status = status, "update test run state");
        }

        internal static void UpdateTestRunProgress(string runId, int totalTests, int completedTests, int passCount, int failCount, int skipCount, string currentTestName, string status = null)
        {
            TryUpdateRunningState(runId, s =>
            {
                if (!string.IsNullOrEmpty(status))
                {
                    s.status = status;
                }
                s.totalTests = totalTests;
                s.completedTests = completedTests;
                s.passCount = passCount;
                s.failCount = failCount;
                s.skipCount = skipCount;
                if (currentTestName != null)
                {
                    s.currentTestName = currentTestName;
                }
            }, "update test run progress");
        }


        internal static void WriteInterruptedResult(string message, string targetRunId = null)
        {
            var state = ReadRunningState();
            string runId = targetRunId ?? state?.runId;
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return;
            }

            var result = new UnityTestRunResult
            {
                runId = runId,
                success = false,
                message = message,
                resultState = OperationStatus.Interrupted,
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                UnityLeanMcpOperationStore.WriteAtomic(UnityLeanMcpPaths.GetTestResultsFile(runId), JsonUtility.ToJson(result, true), runId);
                CleanupTestRun(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        private static void CancelActiveTestRunOnMainThread(string operationId)
        {
            var state = ReadRunningState();
            if (state == null || state.runId != operationId ||
                !UnityLeanMcpOperationStore.IsOwnedBy(operationId, OperationKinds.Test))
            {
                return;
            }

            if (state.status != OperationStatus.Cancelling)
            {
                UpdateTestRunStatus(operationId, OperationStatus.Cancelling);
                UnityLeanMcpOperationStore.Update(operationId, OperationStatus.Cancelling);
            }

            BeginCancellationMonitoring(operationId);

            string jobGuid = s_CurrentTestJobGuid ?? state.jobGuid;
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
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            var runningState = ReadThreadSafeSnapshot();

            // 1. If neither operation store nor running state is active
            if (operation == null && runningState == null)
            {
                string resPath = UnityLeanMcpPaths.GetTestResultsFile(operationId);
                if (!string.IsNullOrEmpty(operationId) && File.Exists(resPath))
                {
                    try
                    {
                        var existing = JsonUtility.FromJson<UnityTestRunResult>(CommandHelper.ReadFileWithRetry(resPath, maxRetries: 3, delayMs: 10));
                        if (existing != null && existing.runId == operationId)
                        {
                            return OperationCancelResult.Cancelled;
                        }
                    }
                    catch { }
                }

                return OperationCancelResult.NotFound;
            }

            // 2. If the operation belongs to another operation ID
            if (operation != null && !string.IsNullOrEmpty(operationId) && operation.OperationId != operationId)
            {
                return OperationCancelResult.NotCancelable;
            }

            if (runningState != null && !string.IsNullOrEmpty(operationId) && runningState.RunId != operationId)
            {
                return OperationCancelResult.NotCancelable;
            }

            string activeRunId = operationId;
            if (string.IsNullOrEmpty(activeRunId))
            {
                activeRunId = runningState?.RunId ?? operation?.OperationId;
            }

            if (!WorkerThreadSnapshots.TryWriteTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, activeRunId))
            {
                return OperationCancelResult.NotCancelable;
            }

            // The action performs all Unity API and state-transition work on
            // the main thread via the dispatcher.
            UnityLeanMcpDispatcher.Enqueue(() => CancelActiveTestRunOnMainThread(activeRunId));
            return OperationCancelResult.Cancelled;
        }

        internal static void WriteCancelledResult(string targetRunId = null)
        {
            var state = ReadRunningState();
            string runId = targetRunId ?? state?.runId;
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return;
            }

            if (!IsCancellationRequested(runId))
            {
                return;
            }

            var result = new UnityTestRunResult
            {
                runId = runId,
                success = false,
                message = "Test run was cancelled or interrupted.",
                resultState = OperationStatus.Cancelled,
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                UnityLeanMcpOperationStore.WriteAtomic(UnityLeanMcpPaths.GetTestResultsFile(runId), JsonUtility.ToJson(result, true), runId);
                CleanupTestRun(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist cancelled test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        internal static void CleanupTestRun(string runId)
        {
            ClearCancellationRequest(runId);
            StopCancellationMonitoring(runId);
            DeleteRunningStateIfOwned(runId);
            ClearCachedRunState();
            UnityLeanMcpOperationStore.Complete(runId);
            s_CurrentTestJobGuid = null;
            if (s_Callbacks != null)
            {
                s_Callbacks.Reset();
            }
        }

        private static void PrepareCancellationRequest(string runId)
        {
            string path = UnityLeanMcpPaths.TestCancellationFile;
            if (!File.Exists(path))
            {
                return;
            }

            string requestedRunId = WorkerThreadSnapshots.ReadFileWithRetry(path)?.Trim();
            if (!string.IsNullOrEmpty(requestedRunId) && requestedRunId != runId)
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static bool IsCancellationRequested(string runId)
        {
            if (string.IsNullOrEmpty(runId))
            {
                return false;
            }

            var state = ReadRunningState();
            return (state != null && state.runId == runId && state.status == OperationStatus.Cancelling) ||
                WorkerThreadSnapshots.TryReadTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, runId);
        }

        private static void UpdateTestRunJobGuid(string runId, string jobGuid)
        {
            TryUpdateRunningState(runId, s => s.jobGuid = jobGuid, "persist test job identity");
        }

        private static void BeginCancellationMonitoring(string runId)
        {
            s_CancellationMonitorRunId = runId;
            if (s_CancellationMonitorRegistered)
            {
                return;
            }

            EditorApplication.update -= ObserveCancellationState;
            EditorApplication.update += ObserveCancellationState;
            s_CancellationMonitorRegistered = true;
        }

        private static void ObserveCancellationState()
        {
            string runId = s_CancellationMonitorRunId;
            if (string.IsNullOrEmpty(runId))
            {
                StopCancellationMonitoring(null);
                return;
            }

            var state = ReadRunningState();
            if (state == null || state.runId != runId ||
                !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                StopCancellationMonitoring(runId);
                return;
            }

            TryCompleteCancellationIfRunnerTerminal(runId);
        }

        private static void TryCompleteCancellationIfRunnerTerminal(string runId)
        {
            if (!IsCancellationRequested(runId) ||
                !TryGetTestRunnerActiveState(out bool isActive) || isActive)
            {
                return;
            }

            WriteCancelledResult(runId);
        }

        internal static void ClearCancellationRequest(string runId)
        {
            if (string.IsNullOrEmpty(runId) ||
                !WorkerThreadSnapshots.TryReadTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, runId))
            {
                return;
            }

            try { File.Delete(UnityLeanMcpPaths.TestCancellationFile); } catch { }
        }

        internal static void StopCancellationMonitoring(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && s_CancellationMonitorRunId != runId)
            {
                return;
            }

            if (s_CancellationMonitorRegistered)
            {
                EditorApplication.update -= ObserveCancellationState;
                s_CancellationMonitorRegistered = false;
            }

            s_CancellationMonitorRunId = null;
            if (string.IsNullOrEmpty(runId) || s_CancellationSignalRunId == runId)
            {
                s_CancellationSignalRunId = null;
            }
        }

        internal static bool DeleteRunningStateIfOwned(string runId)
        {
            if (string.IsNullOrEmpty(runId))
            {
                return false;
            }

            try
            {
                var state = ReadRunningState();
                if (state != null && state.runId == runId)
                {
                    lock (s_RunStateLock)
                    {
                        if (s_CachedRunState != null && s_CachedRunState.runId == runId)
                        {
                            s_CachedRunState = null;
                        }
                    }

                    return CommandHelper.DeleteFileWithRetry(UnityLeanMcpPaths.TestRunningFile);
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to delete running state: {ex.Message}");
                return false;
            }
        }
    }
}
