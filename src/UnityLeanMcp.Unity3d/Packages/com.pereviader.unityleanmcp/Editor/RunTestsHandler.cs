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

        internal static string TempDirectory => UnityLeanMcpPaths.TempDir;
        internal static string RunningFilePath => UnityLeanMcpPaths.TestRunningFile;
        internal static string CancellationFilePath => UnityLeanMcpPaths.TestCancellationFile;
        internal static string ResultsFilePath => UnityLeanMcpPaths.TestResultsFile;
        internal static string GetResultsFilePath(string runId) => UnityLeanMcpPaths.GetTestResultsFile(runId);

        internal static void ClearCachedRunState()
        {
            lock (s_RunStateLock)
            {
                s_CachedRunState = null;
            }
        }

        internal static void MarkTransportInterruption(string status)
        {
            var state = ReadRunningState();
            if (state == null || string.IsNullOrEmpty(state.runId) || !UnityLeanMcpOperationStore.IsOwnedBy(state.runId, OperationKinds.Test))
            {
                return;
            }

            state.status = status;
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }
            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), state.runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to mark test run interruption: {ex.Message}");
            }
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

            RunTestsArgs testArgs;
            TestMode mode;

            if (remainder.StartsWith("{"))
            {
                string unescapedJson = ProtocolCodec.UnescapeLine(remainder);
                testArgs = JsonUtility.FromJson<RunTestsArgs>(unescapedJson) ?? new RunTestsArgs();
                mode = ParseTestMode(testArgs.mode);
            }
            else
            {
                string[] args = CommandHelper.SplitArguments(payload);
                if (args.Length < 2)
                {
                    writer.WriteLine("ERROR: Missing operation id or test mode (all/playmode/editmode)");
                    return;
                }

                mode = ParseTestMode(args[1]);

                string filter = null;
                string category = null;
                bool filterSpecified = false;
                bool categorySpecified = false;
                bool failedOnly = false;

                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--filter")
                    {
                        if (i + 1 >= args.Length)
                        {
                            writer.WriteLine("ERROR: Missing value for --filter");
                            return;
                        }

                        filterSpecified = true;
                        filter = args[++i];
                    }
                    else if (args[i] == "--category")
                    {
                        if (i + 1 >= args.Length)
                        {
                            writer.WriteLine("ERROR: Missing value for --category");
                            return;
                        }

                        categorySpecified = true;
                        category = args[++i];
                    }
                    else if (args[i] == "--failed-only")
                    {
                        failedOnly = true;
                    }
                }

                testArgs = new RunTestsArgs
                {
                    mode = args[1],
                    groupNames = filterSpecified ? new[] { filter } : null,
                    categoryNames = categorySpecified ? new[] { category } : null,
                    failedOnly = failedOnly
                };
            }

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
                        WriteAtomic(GetResultsFilePath(operationId), JsonUtility.ToJson(emptyResult, true), operationId);
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
                    // Null entries remain omitted for compatibility. Empty and
                    // whitespace-only entries must never be silently broadened
                    // into an unfiltered test run.
                    if (values[i] != null && string.IsNullOrWhiteSpace(values[i]))
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
            var failedNames = new List<string>();
            try
            {
                string path = ResultsFilePath;
                if (!File.Exists(path) && Directory.Exists(TempDirectory))
                {
                    var files = new DirectoryInfo(TempDirectory).GetFiles("unity_test_*.json");
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
                            if (!string.IsNullOrEmpty(name) && !failedNames.Contains(name))
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
            return failedNames;
        }

        private static string WriteTestRunningState(string runId, TestMode mode, RunTestsArgs args)
        {
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return null;
            }

            try
            {
                if (!Directory.Exists(TempDirectory))
                {
                    Directory.CreateDirectory(TempDirectory);
                }

                string filterSummary = args.groupNames != null && args.groupNames.Length > 0 ? string.Join(", ", args.groupNames) : "";
                string categorySummary = args.categoryNames != null && args.categoryNames.Length > 0 ? string.Join(", ", args.categoryNames) : "";

                var state = new UnityTestRunState
                {
                    runId = runId,
                    mode = mode.ToString(),
                    filter = filterSummary,
                    category = categorySummary,
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
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
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

        private static void RunTests(TestMode mode, RunTestsArgs args, string runId, string[] explicitTestNames = null)
        {
            try
            {
                if (s_Callbacks == null)
                {
                    RegisterCallbacks();
                }

                string[] effectiveTestNames = explicitTestNames != null && explicitTestNames.Length > 0
                    ? explicitTestNames
                    : (args.testNames != null && args.testNames.Length > 0 ? args.testNames : null);

                var filter = new Filter
                {
                    testMode = mode,
                    testNames = effectiveTestNames,
                    groupNames = args.groupNames != null && args.groupNames.Length > 0 ? args.groupNames : null,
                    categoryNames = args.categoryNames != null && args.categoryNames.Length > 0 ? args.categoryNames : null,
                    assemblyNames = args.assemblyNames != null && args.assemblyNames.Length > 0 ? args.assemblyNames : null
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
                if (!File.Exists(RunningFilePath))
                {
                    return null;
                }

                string text = CommandHelper.ReadFileWithRetry(RunningFilePath, maxRetries: 3, delayMs: 10);
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

            return WorkerThreadSnapshots.TryReadTestRunState(UnityLeanMcpPaths.WorkerTestRunningFile, out var snapshot)
                ? snapshot
                : null;
        }

        internal static void UpdateTestRunStatus(string runId, string status)
        {
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return;
            }

            var state = ReadRunningState();
            if (state == null || state.runId != runId)
            {
                return;
            }

            state.status = status;
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }
            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to update test run state: {ex.Message}");
            }
        }

        internal static void UpdateTestRunProgress(string runId, int totalTests, int completedTests, int passCount, int failCount, int skipCount, string currentTestName, string status = null)
        {
            if (string.IsNullOrEmpty(runId) || !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return;
            }

            var state = ReadRunningState();
            if (state == null || state.runId != runId)
            {
                return;
            }

            if (!string.IsNullOrEmpty(status))
            {
                state.status = status;
            }
            state.totalTests = totalTests;
            state.completedTests = completedTests;
            state.passCount = passCount;
            state.failCount = failCount;
            state.skipCount = skipCount;
            if (currentTestName != null)
            {
                state.currentTestName = currentTestName;
            }
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }

            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to update test run progress: {ex.Message}");
            }
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
                WriteAtomic(GetResultsFilePath(runId), JsonUtility.ToJson(result, true), runId);
                ClearCancellationRequest(runId);
                StopCancellationMonitoring(runId);
                DeleteRunningStateIfOwned(runId);
                ClearCachedRunState();
                UnityLeanMcpOperationStore.Complete(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        internal static bool RequestCancelFromWorker(string operationId)
        {
            if (!string.IsNullOrEmpty(operationId))
            {
                var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
                if (operation == null || operation.OperationId != operationId || operation.Kind != OperationKinds.Test)
                {
                    return false;
                }
            }

            var runningState = ReadThreadSafeSnapshot();
            if (runningState == null
                && string.IsNullOrEmpty(operationId))
            {
                return false;
            }

            string requestedRunId = operationId;
            if (string.IsNullOrEmpty(requestedRunId))
            {
                var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
                requestedRunId = runningState?.RunId ?? operation?.OperationId;
            }

            if (string.IsNullOrEmpty(requestedRunId))
            {
                return false;
            }

            if (runningState != null && runningState.RunId != requestedRunId)
            {
                return false;
            }

            // Persist the intent before acknowledging the request. The marker
            // survives a domain reload if the queued main-thread action is
            // discarded with the old managed domain.
            if (!WorkerThreadSnapshots.TryWriteTestCancellationRequest(UnityLeanMcpPaths.WorkerTestCancellationFile, requestedRunId))
            {
                return false;
            }

            // The action performs all Unity API and state-transition work on
            // the main thread. The worker acknowledges acceptance without
            // waiting for the dispatcher, so cancellation cannot deadlock
            // behind a synchronous operation or a domain reload.
            string runId = requestedRunId;
            UnityLeanMcpDispatcher.Enqueue(() => CancelActiveTestRunOnMainThread(runId));
            return true;
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
            var operation = UnityLeanMcpOperationStore.Read();
            var runningState = ReadRunningState();

            // 1. If neither operation store nor running state is active
            if (operation == null && runningState == null)
            {
                string resPath = GetResultsFilePath(operationId);
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
            if (operation != null && !string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
            {
                return OperationCancelResult.NotCancelable;
            }

            if (runningState != null && !string.IsNullOrEmpty(operationId) && runningState.runId != operationId)
            {
                return OperationCancelResult.NotCancelable;
            }

            string activeRunId = operationId;
            if (string.IsNullOrEmpty(activeRunId))
            {
                activeRunId = runningState?.runId ?? operation?.operationId;
            }

            if (!WorkerThreadSnapshots.TryWriteTestCancellationRequest(CancellationFilePath, activeRunId))
            {
                return OperationCancelResult.NotCancelable;
            }

            CancelActiveTestRunOnMainThread(activeRunId);
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
                WriteAtomic(GetResultsFilePath(runId), JsonUtility.ToJson(result, true), runId);
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
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist cancelled test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        private static void PrepareCancellationRequest(string runId)
        {
            string path = CancellationFilePath;
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
                WorkerThreadSnapshots.TryReadTestCancellationRequest(CancellationFilePath, runId);
        }

        private static void UpdateTestRunJobGuid(string runId, string jobGuid)
        {
            var state = ReadRunningState();
            if (state == null || state.runId != runId ||
                !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
            {
                return;
            }

            state.jobGuid = jobGuid;
            lock (s_RunStateLock)
            {
                s_CachedRunState = state;
            }

            try
            {
                WriteAtomic(RunningFilePath, JsonUtility.ToJson(state, true), runId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to persist test job identity: {ex.Message}");
            }
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
                !WorkerThreadSnapshots.TryReadTestCancellationRequest(CancellationFilePath, runId))
            {
                return;
            }

            try { File.Delete(CancellationFilePath); } catch { }
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

                    if (File.Exists(RunningFilePath))
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                if (File.Exists(RunningFilePath))
                                {
                                    File.Delete(RunningFilePath);
                                }
                                break;
                            }
                            catch (IOException) when (i < 4)
                            {
                                System.Threading.Thread.Sleep(10);
                            }
                            catch (UnauthorizedAccessException) when (i < 4)
                            {
                                System.Threading.Thread.Sleep(10);
                            }
                        }
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to delete running state: {ex.Message}");
                return false;
            }
        }

        internal static void WriteAtomic(string path, string content, string runId)
        {
            UnityLeanMcpOperationStore.WriteAtomic(path, content, runId);
        }

        internal static bool TryWriteStaticHistory(string path, string content, string runId)
        {
            return UnityLeanMcpOperationStore.TryWriteStaticHistory(path, content, runId);
        }
    }
}
