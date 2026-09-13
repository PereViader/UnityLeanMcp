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

        internal static string TempDirectory => UnityLeanMcpPaths.TempDir;
        internal static string RunningFilePath => UnityLeanMcpPaths.TestRunningFile;
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
                s_Callbacks.BindRun(runningState.runId);
            }
        }

        public static bool IsTestRunActive()
        {
            try
            {
                s_IsRunActiveMethod ??= typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return s_IsRunActiveMethod != null && (bool)s_IsRunActiveMethod.Invoke(null, null);
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

            if (UnityLeanMcpCompilationTracker.IsCompiling || UnityLeanMcpCompilationTracker.RefreshPending)
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
                mode = (testArgs.mode ?? "all").ToLowerInvariant() switch
                {
                    "playmode" => TestMode.PlayMode,
                    "editmode" => TestMode.EditMode,
                    "all" => TestMode.EditMode | TestMode.PlayMode,
                    _ => (TestMode)(-1)
                };
            }
            else
            {
                string[] args = CommandHelper.SplitArguments(payload);
                if (args.Length < 2)
                {
                    writer.WriteLine("ERROR: Missing operation id or test mode (all/playmode/editmode)");
                    return;
                }

                mode = args[1].ToLowerInvariant() switch
                {
                    "playmode" => TestMode.PlayMode,
                    "editmode" => TestMode.EditMode,
                    "all" => TestMode.EditMode | TestMode.PlayMode,
                    _ => (TestMode)(-1)
                };

                string filter = "";
                string category = "";
                bool failedOnly = false;

                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--filter" && i + 1 < args.Length)
                    {
                        filter = args[++i];
                    }
                    else if (args[i] == "--category" && i + 1 < args.Length)
                    {
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
                    groupNames = !string.IsNullOrEmpty(filter) ? new[] { filter } : null,
                    categoryNames = !string.IsNullOrEmpty(category) ? new[] { category } : null,
                    failedOnly = failedOnly
                };
            }

            if ((int)mode == -1)
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be all, playmode, or editmode");
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
                DeleteRunningStateIfOwned(runId);
                ClearCachedRunState();
                UnityLeanMcpOperationStore.Complete(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        public static void CancelActiveTestRun(string operationId, StreamWriter writer)
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
                            writer.WriteLine("CANCELLED");
                            return;
                        }
                    }
                    catch { }
                }

                writer.WriteLine("IDLE");
                return;
            }

            // 2. If the operation belongs to another operation ID
            if (operation != null && !string.IsNullOrEmpty(operationId) && operation.operationId != operationId)
            {
                writer.WriteLine($"BUSY {operation.kind} {operation.operationId}");
                return;
            }

            if (runningState != null && !string.IsNullOrEmpty(operationId) && runningState.runId != operationId)
            {
                writer.WriteLine($"BUSY test {runningState.runId}");
                return;
            }

            string activeRunId = operationId;
            if (string.IsNullOrEmpty(activeRunId))
            {
                activeRunId = runningState?.runId ?? operation?.operationId;
            }

            string jobGuid = s_CurrentTestJobGuid;
            if (!string.IsNullOrEmpty(jobGuid))
            {
                UnityLeanMcpDispatcher.Enqueue(() =>
                {
                    try
                    {
                        TestRunnerApi.CancelTestRun(jobGuid);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"UnityLeanMcp: Exception while calling TestRunnerApi.CancelTestRun: {ex.Message}");
                    }
                });
            }

            WriteCancelledResult(activeRunId);
            writer.WriteLine("CANCELLED");
        }

        internal static void WriteCancelledResult(string targetRunId = null)
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
                message = "Test run was cancelled or interrupted.",
                resultState = OperationStatus.Cancelled,
                failedTests = new List<FailedTestInfo>()
            };

            try
            {
                WriteAtomic(GetResultsFilePath(runId), JsonUtility.ToJson(result, true), runId);
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
    }
}
