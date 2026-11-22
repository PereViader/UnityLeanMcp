using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityLeanMcp
{
    internal partial class RunTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => true;

        private const string CallbackOwnerName = "UnityLeanMcp.CallbackOwner";
        private static MyTestCallbacks s_Callbacks;
        private static TestRunnerApi s_RunnerApi;
        private static MethodInfo s_IsRunActiveMethod;
        internal static string s_CurrentTestJobGuid;

        public static void RegisterCallbacks()
        {
            if (CommandHelper.IsAssetImportWorkerProcess())
                return;

            EditorApplication.quitting -= RestoreCoverageOnEditorQuit;
            EditorApplication.quitting += RestoreCoverageOnEditorQuit;

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

            var activeOp = UnityCommandGate.ReadSnapshot();
            var runningState = ReadRunningState();
            if (runningState != null &&
                activeOp != null &&
                string.Equals(activeOp.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase) &&
                activeOp.OperationId == runningState.runId)
            {
                s_CurrentTestJobGuid = runningState.jobGuid;
                s_Callbacks.BindRun(runningState.runId);
                if (IsCancellationRequested(runningState.runId) || runningState.status == OperationStatus.Cancelling)
                {
                    // Test Runner restores its jobs in an unordered load initializer.
                    // Resume cancellation on an update, after that restoration finishes.
                    BeginCancellationMonitoring(runningState.runId);
                }
            }
            else if (activeOp == null || !string.Equals(activeOp.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
            {
                ClearCachedRunState();
                try { CommandHelper.DeleteFileWithRetry(UnityLeanMcpPaths.TestRunningFile); } catch { }
                try
                {
                    if (File.Exists(UnityLeanMcpPaths.TestCancellationFile))
                        File.Delete(UnityLeanMcpPaths.TestCancellationFile);
                }
                catch { }
            }
        }

        public static bool IsTestRunActive() =>
            TryGetTestRunnerActiveState(out bool isActive) && isActive;

        internal static bool TryGetTestRunnerActiveState(out bool isActive)
        {
            isActive = false;
            try
            {
                s_IsRunActiveMethod ??= typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (s_IsRunActiveMethod == null)
                    return false;

                object value = s_IsRunActiveMethod.Invoke(null, null);
                if (!(value is bool active))
                    return false;

                isActive = active;
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
            if (string.IsNullOrEmpty(payload))
            {
                writer.WriteLine("ERROR: Missing arguments");
                return;
            }

            string[] requestParts = (payload ?? "").Trim().Split(new[] { ' ' }, 2);
            if (requestParts.Length < 2 || string.IsNullOrWhiteSpace(requestParts[0]))
            {
                writer.WriteLine("ERROR: Missing operation id or test parameters");
                return;
            }

            string operationId = requestParts[0];
            if (File.Exists(UnityLeanMcpPaths.GetTestResultsFile(operationId)))
            {
                writer.WriteLine("RUNNING");
                return;
            }

            if (UnityCommandGate.IsOwnedBy(operationId, OperationKinds.Test))
            {
                TryAcknowledgeRunning(writer);
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

            string remainder = requestParts[1].Trim();
            if (!remainder.StartsWith("{", StringComparison.Ordinal))
            {
                writer.WriteLine("ERROR: Invalid test parameters. Expected JSON payload for RunTestsArgs.");
                return;
            }

            RunTestsArgs testArgs = JsonUtility.FromJson<RunTestsArgs>(ProtocolCodec.UnescapeLine(remainder)) ?? new RunTestsArgs();
            TestMode mode = ParseTestMode(testArgs.mode);
            if ((int)mode == -1)
            {
                writer.WriteLine("ERROR: Invalid test mode. Must be playmode or editmode");
                return;
            }

            if (!TryValidateFilterValues(testArgs, out string filterError))
            {
                writer.WriteLine($"ERROR: {filterError}");
                return;
            }

            if (testArgs.coverage)
            {
                if (mode == TestMode.PlayMode && !IsPlayModeCoverageSupported())
                {
                    writer.WriteLine("FAILURE Code coverage for 'playmode' tests is only supported on Unity 6.7 or newer. In earlier Unity versions, exiting PlayMode triggers a domain reload that wipes in-memory coverage. Please use 'editmode' tests for coverage.");
                    return;
                }

                if (CompilationPipeline.codeOptimization == CodeOptimization.Release)
                {
                    writer.WriteLine("FAILURE Cannot run tests with coverage: Unity script optimization is set to Release mode. Switch Unity to Debug mode before running tests with coverage.");
                    return;
                }
            }

            var begin = UnityCommandGate.TryBegin(OperationKinds.Test, operationId, OperationStatus.Queued, out var busyReason);
            switch (begin)
            {
                case BeginGateResult.Cancelled:
                    writer.WriteLine("INTERRUPTION Operation was cancelled before admission.");
                    return;
                case BeginGateResult.Invalid:
                    writer.WriteLine("ERROR: Missing or invalid operation id");
                    return;
                case BeginGateResult.Busy:
                    writer.WriteLine($"BUSY {busyReason}");
                    return;
                case BeginGateResult.AlreadyStarted:
                    TryAcknowledgeRunning(writer);
                    return;
            }

            if (testArgs.coverage)
            {
                // Admission starts a new single-snapshot attempt. A later setup
                // failure must not make the previous report available again.
                CoverageLifecycleManager.ClearCapturedCoverage();
            }

            try
            {
                List<string> failedTests = null;
                if (testArgs.failedOnly)
                {
                    failedTests = GetPreviouslyFailedTestNames();
                    if (testArgs.testNames != null && testArgs.testNames.Length > 0)
                        failedTests = failedTests.Intersect(testArgs.testNames, StringComparer.Ordinal).ToList();

                    if (failedTests.Count == 0)
                    {
                        PublishEmptyFailedOnlyResult(operationId, testArgs);
                        TryAcknowledgeRunning(writer);
                        return;
                    }

                    // Persist the resolved rerun set so preparation can resume
                    // after a reload without rereading mutable test history.
                    testArgs.testNames = failedTests.ToArray();
                }

                // Persist the complete run identity before acknowledging the command.
                // The client can therefore recover if this socket is closed by a reload
                // immediately after the command is dispatched.
                string runId = WriteTestRunningState(operationId, mode, testArgs);
                if (string.IsNullOrEmpty(runId))
                {
                    writer.WriteLine("ERROR: Could not persist test run state.");
                    writer.Flush();
                    UnityCommandGate.Complete(operationId);
                    return;
                }

                if (testArgs.coverage)
                {
                    try
                    {
                        CoverageLifecycleManager.BeginCoverageAttempt(operationId);
                    }
                    catch (Exception ex)
                    {
                        CoverageLifecycleManager.AbortCoveragePreparation(operationId);
                        writer.WriteLine($"FAILURE Failed to initialize code coverage: {ex.Message}");
                        writer.Flush();
                        WriteInterruptedResult("Failed to initialize code coverage: " + ex.Message, operationId);
                        return;
                    }
                }

                TryAcknowledgeRunning(writer);

                if (testArgs.coverage)
                {
                    ScheduleCoveragePreparation(runId);
                    return;
                }

                RunTests(mode, testArgs, runId, failedTests?.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Unhandled exception during RunTests: {ex}");
                WriteInterruptedResult("Failed to start test run: " + ex.Message, operationId);
            }
        }

        private static void PublishEmptyFailedOnlyResult(string operationId, RunTestsArgs testArgs)
        {
            string msg = testArgs.testNames != null && testArgs.testNames.Length > 0
                ? "No previously failed tests found matching the specified test filter."
                : "No previously failed tests found.";
            var emptyResult = new UnityTestRunResult
            {
                runId = operationId,
                success = true,
                message = msg,
                resultState = "Passed",
                coverageStatus = testArgs.coverage ? "unavailable" : null,
                coverageMessage = testArgs.coverage ? "No test cases were selected, so no coverage measurement was performed." : null,
                failedTests = new List<FailedTestInfo>()
            };
            UnityCommandGate.PublishResult(OperationKinds.Test, operationId, UnityLeanMcpPaths.GetTestResultsFile(operationId), JsonUtility.ToJson(emptyResult, true));
        }

        private static void TryAcknowledgeRunning(StreamWriter writer)
        {
            try
            {
                writer.WriteLine("RUNNING");
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException)
            {
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

                    if (parameterName == "groupNames")
                    {
                        try
                        {
                            _ = new System.Text.RegularExpressions.Regex(values[i]);
                        }
                        catch (ArgumentException exception)
                        {
                            error = $"Invalid test filter 'groupNames[{i}]': invalid .NET regular expression: {exception.Message}";
                            return false;
                        }
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
                    var result = !string.IsNullOrEmpty(json) ? JsonUtility.FromJson<UnityTestRunResult>(json) : null;
                    if (result?.failedTests != null)
                    {
                        foreach (var fail in result.failedTests)
                        {
                            string name = !string.IsNullOrEmpty(fail.fullName) ? fail.fullName : fail.name;
                            if (!string.IsNullOrEmpty(name))
                                failedNames.Add(name);
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
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
                return null;

            try
            {
                if (!Directory.Exists(UnityLeanMcpPaths.TempDir))
                    Directory.CreateDirectory(UnityLeanMcpPaths.TempDir);

                var state = new UnityTestRunState
                {
                    runId = runId,
                    mode = mode.ToString(),
                    testNames = CloneStringArray(args.testNames),
                    groupNames = CloneStringArray(args.groupNames),
                    categoryNames = CloneStringArray(args.categoryNames),
                    assemblyNames = CloneStringArray(args.assemblyNames),
                    coverage = args.coverage,
                    status = OperationStatus.Queued,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    currentTestName = ""
                };
                string prettyJson = JsonUtility.ToJson(state, true);
                PublishCachedRunState(state);
                UnityCommandGate.WriteAtomic(UnityLeanMcpPaths.TestRunningFile, prettyJson, runId);
                return runId;
            }
            catch (Exception ex)
            {
                ClearCachedRunState();
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
                    RegisterCallbacks();

                string[] effectiveTestNames = explicitTestNames != null && args.testNames != null && args.testNames.Length > 0
                    ? explicitTestNames.Intersect(args.testNames, StringComparer.Ordinal).ToArray()
                    : NonEmptyOrNull(explicitTestNames) ?? NonEmptyOrNull(args.testNames);

                var filter = new Filter
                {
                    testMode = mode,
                    testNames = effectiveTestNames,
                    groupNames = NonEmptyOrNull(args.groupNames),
                    categoryNames = NonEmptyOrNull(args.categoryNames),
                    assemblyNames = NonEmptyOrNull(args.assemblyNames)
                };

                UpdateTestRunStatus(runId, OperationStatus.Running);
                UnityCommandGate.Update(runId, OperationStatus.Executing);
                s_Callbacks.BindRun(runId);

                var settings = new ExecutionSettings(filter);
                Debug.Log($"UnityLeanMcp: Executing {mode} tests with testNames count '{(filter.testNames?.Length ?? 0)}', groupNames count '{(filter.groupNames?.Length ?? 0)}', categoryNames count '{(filter.categoryNames?.Length ?? 0)}', assemblyNames count '{(filter.assemblyNames?.Length ?? 0)}'...");
                if (args.coverage)
                    CoverageLifecycleManager.MarkCoverageExecutionStarting(runId);

                s_CurrentTestJobGuid = s_RunnerApi.Execute(settings);
                UpdateTestRunJobGuid(runId, s_CurrentTestJobGuid);

                // A cancellation request may have been durably recorded while
                // this command was waiting to reach the main thread. Apply it
                // after Execute returns, when the job identity is available.
                if (IsCancellationRequested(runId))
                    CancelActiveTestRunOnMainThread(runId);

                BeginActiveRunMonitoring(runId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to start tests: {ex}");
                if (s_Callbacks != null)
                    s_Callbacks.OnInfrastructureFailure("Failed to start test run: " + ex.Message);
                else
                    WriteInterruptedResult("Failed to start test run: " + ex.Message);
            }
        }

        internal static void CleanupTestRun(string runId)
        {
            ClearCancellationRequest(runId);
            StopCancellationMonitoring(runId);
            StopActiveRunMonitoring(runId);
            DeleteRunningStateIfOwned(runId);
            ClearCachedRunState();
            UnityCommandGate.Complete(runId);
            s_CurrentTestJobGuid = null;
            if (s_Callbacks != null)
                s_Callbacks.Reset();
        }
    }
}
