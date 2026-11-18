using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
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
        private sealed class CachedRunSnapshot
        {
            public readonly UnityTestRunState State;
            public readonly string Json;

            public CachedRunSnapshot(UnityTestRunState state, string json)
            {
                State = state;
                Json = json;
            }
        }

        internal static string s_CurrentTestJobGuid;
        private static UnityTestRunState s_CachedRunState;
        private static CachedRunSnapshot s_CachedRunSnapshot;
        private static string s_CancellationSignalRunId;
        private static string s_CancellationMonitorRunId;
        private static bool s_CancellationMonitorRegistered;
        private static string s_CoveragePreparationRunId;
        private static bool s_CoveragePreparationRegistered;
        private static string s_ActiveRunMonitorRunId;
        private static bool s_ActiveRunObservedActive;
        private static bool s_ActiveRunMonitorRegistered;

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

            string json = JsonUtility.ToJson(state);
            Volatile.Write(ref s_CachedRunSnapshot, new CachedRunSnapshot(state, json));
            Volatile.Write(ref s_CachedRunState, state);
        }

        private static string[] CloneStringArray(string[] source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = new string[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static UnityTestRunState CloneRunningState(UnityTestRunState source)
        {
            if (source == null)
            {
                return null;
            }

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

        private static bool TryUpdateRunningState(string runId, Action<UnityTestRunState> update, string actionDesc)
        {
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
                return false;

            var current = ReadRunningState();
            if (current == null || current.runId != runId)
                return false;

            var next = CloneRunningState(current);
            update(next);
            PublishCachedRunState(next);

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
                    {
                        File.Delete(UnityLeanMcpPaths.TestCancellationFile);
                    }
                }
                catch { }
            }
        }

        public static bool IsTestRunActive()
        {
            return TryGetTestRunnerActiveState(out bool isActive) && isActive;
        }

        internal static bool TryGetTestRunnerActiveState(out bool isActive)
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
            if (File.Exists(UnityLeanMcpPaths.GetTestResultsFile(operationId)))
            {
                writer.WriteLine("RUNNING");
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

            string unescapedJson = ProtocolCodec.UnescapeLine(remainder);
            RunTestsArgs testArgs = JsonUtility.FromJson<RunTestsArgs>(unescapedJson) ?? new RunTestsArgs();
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
            if (begin == BeginGateResult.Cancelled)
            {
                writer.WriteLine("INTERRUPTION Operation was cancelled before admission.");
                return;
            }
            if (begin == BeginGateResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginGateResult.Busy)
            {
                writer.WriteLine($"BUSY {busyReason}");
                return;
            }

            if (begin == BeginGateResult.AlreadyStarted)
            {
                try
                {
                    writer.WriteLine("RUNNING");
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                }
                return;
            }

            if (testArgs.coverage)
            {
                // Admission starts a new single-snapshot attempt. A later setup
                // failure must not make the previous report available again.
                GetCoverageHandler.ClearCapturedCoverage();
            }

            try
            {
                List<string> failedTests = null;
                if (testArgs.failedOnly)
                {
                    failedTests = GetPreviouslyFailedTestNames();
                    if (testArgs.testNames != null && testArgs.testNames.Length > 0)
                    {
                        failedTests = failedTests.Intersect(testArgs.testNames, StringComparer.Ordinal).ToList();
                    }
                    if (failedTests.Count == 0)
                    {
                        string msg = testArgs.testNames != null && testArgs.testNames.Length > 0
                            ? "No previously failed tests found matching the specified test filter."
                            : "No previously failed tests found.";
                        var emptyResult = new UnityTestRunResult
                        {
                            runId = operationId,
                            success = true,
                            failCount = 0,
                            passCount = 0,
                            skipCount = 0,
                            message = msg,
                            resultState = "Passed",
                            coverageStatus = testArgs.coverage ? "unavailable" : null,
                            coverageMessage = testArgs.coverage ? "No test cases were selected, so no coverage measurement was performed." : null,
                            failedTests = new List<FailedTestInfo>()
                        };
                        UnityCommandGate.PublishResult(OperationKinds.Test, operationId, UnityLeanMcpPaths.GetTestResultsFile(operationId), JsonUtility.ToJson(emptyResult, true));
                        try
                        {
                            writer.WriteLine("RUNNING");
                            writer.Flush();
                        }
                        catch (Exception ex) when (ex is IOException || ex is SocketException)
                        {
                        }
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
                        GetCoverageHandler.BeginCoverageAttempt(operationId);
                    }
                    catch (Exception ex)
                    {
                        GetCoverageHandler.AbortCoveragePreparation(operationId);
                        writer.WriteLine($"FAILURE Failed to initialize code coverage: {ex.Message}");
                        writer.Flush();
                        WriteInterruptedResult("Failed to initialize code coverage: " + ex.Message, operationId);
                        return;
                    }
                }

                try
                {
                    writer.WriteLine("RUNNING");
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                }

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
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
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
                    testNames = CloneStringArray(args.testNames),
                    groupNames = CloneStringArray(args.groupNames),
                    categoryNames = CloneStringArray(args.categoryNames),
                    assemblyNames = CloneStringArray(args.assemblyNames),
                    coverage = args.coverage,
                    status = OperationStatus.Queued,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    totalTests = 0,
                    completedTests = 0,
                    passCount = 0,
                    failCount = 0,
                    skipCount = 0,
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

        internal static void ScheduleCoveragePreparation(string runId)
        {
            if (string.IsNullOrEmpty(runId) || !GetCoverageHandler.IsCoverageAttempt(runId))
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
            if (state == null || state.runId != runId || !GetCoverageHandler.IsCoverageAttempt(runId))
            {
                StopCoveragePreparation();
                GetCoverageHandler.AbortCoveragePreparation(runId);
                WriteInterruptedResult("Coverage preparation state was lost.", runId);
                return;
            }

            if (IsCancellationRequested(runId))
            {
                StopCoveragePreparation();
                WriteCancelledResult(runId);
                return;
            }

            string phase = GetCoverageHandler.GetCoverageAttemptPhase(runId);
            if (phase != "baseline-preparing" && phase != "enabling" && phase != "preparing")
            {
                StopCoveragePreparation();
                return;
            }

            try
            {
                if (!GetCoverageHandler.AdvanceCoverageLifecycle(runId, restoration: false))
                    return;

                if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed || EditorUtility.scriptCompilationFailed)
                {
                    StopCoveragePreparation();
                    WriteInterruptedResult("Coverage preparation compilation failed. Fix the compiler errors and rerun tests with coverage enabled.", runId);
                    return;
                }

                if (!TryGetTestRunnerActiveState(out bool isActive) || isActive)
                    return;

                if (phase == "baseline-preparing")
                {
                    if (!GetCoverageHandler.EstablishCoverageBaseline(runId))
                    {
                        StopCoveragePreparation();
                        WriteInterruptedResult("Compilation inputs changed while the coverage baseline was being established. Refresh the project and rerun tests with coverage enabled.", runId);
                        return;
                    }
                    return;
                }

                if (phase == "enabling")
                {
                    if (!UnityEngine.TestTools.Coverage.enabled || !GetCoverageHandler.CoverageInputsMatchAttempt(runId))
                    {
                        StopCoveragePreparation();
                        WriteInterruptedResult("Compilation inputs changed while coverage instrumentation settled. Refresh the project and rerun tests with coverage enabled.", runId);
                        return;
                    }
                    GetCoverageHandler.MarkCoverageInstrumentationSettled(runId);
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
                if (!GetCoverageHandler.CoverageInputsMatchAttempt(runId))
                {
                    StopCoveragePreparation();
                    WriteInterruptedResult("Compilation inputs changed while coverage instrumentation settled. Refresh the project and rerun tests with coverage enabled.", runId);
                    return;
                }

                GetCoverageHandler.ResetCoverageCounters();
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
                GetCoverageHandler.AbortCoveragePreparation(runId);
                Debug.LogError($"UnityLeanMcp: Failed to prepare coverage: {ex}");
                WriteInterruptedResult("Failed to prepare code coverage: " + ex.Message, runId);
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
                {
                    GetCoverageHandler.MarkCoverageExecutionStarting(runId);
                }
                s_CurrentTestJobGuid = s_RunnerApi.Execute(settings);
                UpdateTestRunJobGuid(runId, s_CurrentTestJobGuid);

                // A cancellation request may have been durably recorded while
                // this command was waiting to reach the main thread. Apply it
                // after Execute returns, when the job identity is available.
                if (IsCancellationRequested(runId))
                {
                    CancelActiveTestRunOnMainThread(runId);
                }

                BeginActiveRunMonitoring(runId);
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
            var cached = Volatile.Read(ref s_CachedRunState);
            if (cached != null)
            {
                return cached;
            }
            try
            {
                var activeOp = UnityCommandGate.ReadSnapshot();
                if (activeOp == null || !string.Equals(activeOp.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

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
                if (state == null || state.runId != activeOp.OperationId)
                {
                    return null;
                }

                var published = Interlocked.CompareExchange(ref s_CachedRunState, state, null);
                if (published == null)
                {
                    var snapshot = new CachedRunSnapshot(state, JsonUtility.ToJson(state));
                    Interlocked.CompareExchange(ref s_CachedRunSnapshot, snapshot, null);
                }
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
            var state = Volatile.Read(ref s_CachedRunState);
            var snapshot = Volatile.Read(ref s_CachedRunSnapshot);
            if (state != null)
            {
                if (!string.IsNullOrEmpty(operationId) && state.runId != operationId)
                {
                    json = null;
                    return false;
                }

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
                var activeOp = UnityCommandGate.ReadSnapshot();
                if (activeOp == null || !string.Equals(activeOp.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
                {
                    json = null;
                    return false;
                }

                if (!string.IsNullOrEmpty(operationId) && activeOp.OperationId != operationId)
                {
                    json = null;
                    return false;
                }

                string text = WorkerThreadSnapshots.ReadFileWithRetry(UnityLeanMcpPaths.TestRunningFile, maxRetries: 3, delayMs: 10);
                if (string.IsNullOrWhiteSpace(text))
                {
                    json = null;
                    return false;
                }

                var values = UnityLeanJson.DeserializeObject(text, StringComparer.OrdinalIgnoreCase);
                if (values == null ||
                    !values.TryGetValue("runId", out object runIdObj) ||
                    !(runIdObj is string runId) ||
                    runId != activeOp.OperationId)
                {
                    json = null;
                    return false;
                }

                json = text;
                return true;
            }
            catch (Exception ex)
            {
                WorkerDiagnosticsLogger.Error(UnityLeanMcpPaths.LogFile, $"UnityLeanMcp: Failed to read test run state on worker: {ex.Message}");
                json = null;
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
            {
                return new WorkerTestRunStateSnapshot(cached.runId);
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
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
                return;

            var current = Volatile.Read(ref s_CachedRunState) ?? ReadRunningState();
            if (current == null || current.runId != runId)
                return;

            var next = CloneRunningState(current);
            if (!string.IsNullOrEmpty(status))
            {
                next.status = status;
            }
            next.totalTests = totalTests;
            next.completedTests = completedTests;
            next.passCount = passCount;
            next.failCount = failCount;
            next.skipCount = skipCount;
            if (currentTestName != null)
            {
                next.currentTestName = currentTestName;
            }
            PublishCachedRunState(next);
        }


        internal static void WriteInterruptedResult(string message, string targetRunId = null)
        {
            var state = ReadRunningState();
            var operation = UnityCommandGate.ReadSnapshot();
            string runId = targetRunId ?? state?.runId ??
                (operation != null && string.Equals(operation.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase)
                    ? operation.OperationId
                    : null);
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
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
                UnityCommandGate.PublishResult(OperationKinds.Test, runId, UnityLeanMcpPaths.GetTestResultsFile(runId), JsonUtility.ToJson(result, true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist interrupted test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        private static void CancelActiveTestRunOnMainThread(string operationId)
        {
            if (!UnityCommandGate.IsOwnedBy(operationId, OperationKinds.Test))
            {
                return;
            }

            var state = ReadRunningState();
            var snapshot = UnityCommandGate.ReadSnapshot();
            bool hasMatchingState = state != null && state.runId == operationId;
            if ((hasMatchingState && state.status != OperationStatus.Cancelling) ||
                (snapshot != null && snapshot.Status != OperationStatus.Cancelling))
            {
                if (hasMatchingState)
                {
                    UpdateTestRunStatus(operationId, OperationStatus.Cancelling);
                }
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
            {
                return OperationCancelResult.NotFound;
            }

            var operation = UnityCommandGate.ReadSnapshot();
            var runningState = operation == null ? ReadThreadSafeSnapshot() : null;

            // 1. If neither operation store nor running state is active
            if (operation == null && runningState == null)
            {
                string resPath = UnityLeanMcpPaths.GetTestResultsFile(operationId);
                if (WorkerThreadSnapshots.TryReadOperationResult(resPath, out var existing) &&
                    existing.OperationId == operationId &&
                    (existing.Interrupted ||
                     existing.ResultState == OperationStatus.Cancelled ||
                     existing.ResultState == OperationStatus.Interrupted))
                {
                    return OperationCancelResult.Cancelled;
                }

                return OperationCancelResult.NotFound;
            }

            // 2. If the operation belongs to another operation ID
            if (operation != null)
            {
                if (operation.OperationId != operationId)
                {
                    return OperationCancelResult.NotCancelable;
                }
            }
            else if (runningState != null && runningState.RunId != operationId)
            {
                return OperationCancelResult.NotCancelable;
            }

            string activeRunId = operationId;

            if (!UnityCommandGate.TryRequestCancellation(activeRunId, out _) &&
                !UnityCommandGate.IsCancellationRequested(activeRunId))
                return OperationCancelResult.NotFound;

            // The action performs all Unity API and state-transition work on
            // the main thread via the dispatcher.
            UnityLeanMcpDispatcher.Enqueue(() => CancelActiveTestRunOnMainThread(activeRunId));
            return CancelOperationHandler.IsTerminalInterruptedResult(activeRunId)
                ? OperationCancelResult.Cancelled
                : OperationCancelResult.Cancelling;
        }

        internal static void WriteCancelledResult(string targetRunId = null)
        {
            var state = ReadRunningState();
            var operation = UnityCommandGate.ReadSnapshot();
            string runId = targetRunId ?? state?.runId ??
                (operation != null && string.Equals(operation.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase)
                    ? operation.OperationId
                    : null);
            if (string.IsNullOrEmpty(runId) || !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test))
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
                UnityCommandGate.PublishResult(OperationKinds.Test, runId, UnityLeanMcpPaths.GetTestResultsFile(runId), JsonUtility.ToJson(result, true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist cancelled test result. Type={ex.GetType().FullName}, StackTrace={ex.StackTrace}");
            }
        }

        internal static void MarkCoverageRunStarted(string runId) =>
            GetCoverageHandler.TryMarkCoverageRunnerStarted(runId);

        internal static void MarkCoverageRunCompleted(string runId, bool complete) =>
            GetCoverageHandler.MarkCoverageTestCompleted(runId, complete);

        // Called by terminal-result publication after the exact test payload and
        // coverage recovery record are persisted. Capture is frozen in
        // SessionState before the prior native profiler flag can discard counters.
        internal static string CompleteCoverageForTerminalResult(string runId) =>
            GetCoverageHandler.CompleteCoverageAttempt(runId);

        private static void RestoreCoverageOnEditorQuit() => GetCoverageHandler.RestoreAnyCoverageProfiler();

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
            {
                s_Callbacks.Reset();
            }
        }

        internal static bool IsCancellationRequested(string runId)
        {
            if (string.IsNullOrEmpty(runId))
            {
                return false;
            }

            if (UnityCommandGate.IsCancellationRequested(runId)) return true;

            var snapshot = UnityCommandGate.ReadSnapshot();
            if (snapshot != null && snapshot.OperationId == runId && snapshot.Status == OperationStatus.Cancelling)
            {
                return true;
            }

            var state = ReadRunningState();
            if (state != null && state.runId == runId && state.status == OperationStatus.Cancelling)
            {
                return true;
            }

            return WorkerThreadSnapshots.TryReadTestCancellationRequest(UnityLeanMcpPaths.TestCancellationFile, runId);
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

            if (!UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test) ||
                UnityCommandGate.HasPendingResult(runId))
            {
                StopCancellationMonitoring(runId);
                return;
            }

            CancelActiveTestRunOnMainThread(runId);
        }

        private static void BeginActiveRunMonitoring(string runId)
        {
            if (string.IsNullOrEmpty(runId) ||
                !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test) ||
                UnityCommandGate.HasPendingResult(runId))
            {
                return;
            }

            s_ActiveRunMonitorRunId = runId;
            s_ActiveRunObservedActive = TryGetTestRunnerActiveState(out bool activeNow) && activeNow;
            if (s_ActiveRunMonitorRegistered)
            {
                return;
            }

            EditorApplication.update -= ObserveActiveTestRun;
            EditorApplication.update += ObserveActiveTestRun;
            s_ActiveRunMonitorRegistered = true;
        }

        internal static void StopActiveRunMonitoring(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && s_ActiveRunMonitorRunId != runId)
            {
                return;
            }

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
            if (string.IsNullOrEmpty(runId) ||
                !UnityCommandGate.IsOwnedBy(runId, OperationKinds.Test) ||
                UnityCommandGate.HasPendingResult(runId))
            {
                StopActiveRunMonitoring(runId);
                return;
            }

            if (!TryGetTestRunnerActiveState(out bool isActive))
            {
                return;
            }

            if (isActive)
            {
                s_ActiveRunObservedActive = true;
                return;
            }

            if (!s_ActiveRunObservedActive)
            {
                return;
            }

            StopActiveRunMonitoring(runId);
            if (IsCancellationRequested(runId))
            {
                WriteCancelledResult(runId);
            }
            else
            {
                WriteInterruptedResult("Test run ended without a completion callback.", runId);
            }
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
                    var cached = Volatile.Read(ref s_CachedRunState);
                    if (cached != null && cached.runId == runId)
                    {
                        if (Interlocked.CompareExchange(ref s_CachedRunState, null, cached) == cached)
                        {
                            Volatile.Write(ref s_CachedRunSnapshot, null);
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
