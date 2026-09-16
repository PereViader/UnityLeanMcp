using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityLeanMcp
{
    public class MyTestCallbacks : ICallbacks, IErrorCallbacks
    {
        private readonly List<FailedTestInfo> m_FailedTests = new List<FailedTestInfo>();
        private bool m_IsRunning = false;
        private string m_RunId;
        private int m_TotalTests = 0;
        private int m_CompletedTests = 0;
        private int m_PassCount = 0;
        private int m_FailCount = 0;
        private int m_SkipCount = 0;
        private string m_CurrentTestName = "";

        public bool IsRunning => m_IsRunning || File.Exists(RunTestsHandler.RunningFilePath);

        internal void Reset()
        {
            m_IsRunning = false;
            m_RunId = null;
            m_FailedTests.Clear();
            m_TotalTests = 0;
            m_CompletedTests = 0;
            m_PassCount = 0;
            m_FailCount = 0;
            m_SkipCount = 0;
            m_CurrentTestName = "";
            RunTestsHandler.ClearCachedRunState();
        }

        internal void BindRun(string runId)
        {
            m_RunId = runId;
            var state = RunTestsHandler.ReadRunningState();
            if (state != null && state.runId == runId)
            {
                m_TotalTests = state.totalTests;
                m_CompletedTests = state.completedTests;
                m_PassCount = state.passCount;
                m_FailCount = state.failCount;
                m_SkipCount = state.skipCount;
                m_CurrentTestName = state.currentTestName ?? "";
            }
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (string.IsNullOrEmpty(m_RunId) || !UnityLeanMcpOperationStore.IsOwnedBy(m_RunId, OperationKinds.Test))
            {
                m_IsRunning = false;
                return;
            }

            m_FailedTests.Clear();
            m_IsRunning = true;
            m_TotalTests = testsToRun != null ? testsToRun.TestCaseCount : 0;
            m_CompletedTests = 0;
            m_PassCount = 0;
            m_FailCount = 0;
            m_SkipCount = 0;
            m_CurrentTestName = "";

            var state = RunTestsHandler.ReadRunningState();
            if (state != null)
            {
                m_RunId = state.runId;
                RunTestsHandler.UpdateTestRunProgress(state.runId, m_TotalTests, m_CompletedTests, m_PassCount, m_FailCount, m_SkipCount, m_CurrentTestName, "Running");
            }
        }


        public void OnError(string message)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, string.IsNullOrEmpty(message) ? "Test run failed with error." : message, "Failed");
        }

        public void OnRunCancelled(string reason = "Test run was cancelled or interrupted.")
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "Cancelled");
        }

        public void OnRunInterrupted(string reason)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "Interrupted");
        }

        public void OnInfrastructureFailure(string reason)
        {
            FinalizeTestRun(false, m_FailedTests.Count, 0, 0, reason, "InfrastructureFailure");
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                string resultState = result.ResultState ?? "";
                bool isCancelled = resultState == "Cancelled" ||
                                   resultState.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0;
                var runState = RunTestsHandler.ReadRunningState();
                bool transportInterrupted = runState != null &&
                    (runState.status == "Reloading" || runState.status == "ShuttingDown");
                bool isFailed = result.FailCount > 0 || result.TestStatus == TestStatus.Failed || isCancelled;

                bool success = !isFailed;
                string message = isCancelled ? (!string.IsNullOrEmpty(result.Message) ? result.Message : "Test run was cancelled or interrupted.")
                               : "";

                if (transportInterrupted && isCancelled)
                {
                    message = "Test run was interrupted by Unity domain reload or shutdown.";
                    resultState = "Interrupted";
                }

                FinalizeTestRun(success, result.FailCount, result.PassCount, result.SkipCount, message, resultState);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Exception in RunFinished callback: {ex}");
            }
        }

        private void FinalizeTestRun(bool success, int failCount, int passCount, int skipCount, string message, string resultState)
        {
            string runId = null;
            try
            {
                string runningPath = RunTestsHandler.RunningFilePath;
                var state = RunTestsHandler.ReadRunningState();
                runId = m_RunId ?? state?.runId;
                string resultsPath = RunTestsHandler.GetResultsFilePath(runId);

                if (state == null || string.IsNullOrEmpty(runId) || state.runId != runId ||
                    !UnityLeanMcpOperationStore.IsOwnedBy(runId, OperationKinds.Test))
                {
                    m_IsRunning = false;
                    return;
                }

                if (!m_IsRunning && !File.Exists(runningPath))
                {
                    return;
                }

                // A callback can arrive more than once (for example OnError
                // followed by RunFinished), and callbacks can straddle a domain
                // reload. Once a result exists for this run it is authoritative.
                if (!string.IsNullOrEmpty(runId) && File.Exists(resultsPath))
                {
                    var existing = JsonUtility.FromJson<UnityTestRunResult>(CommandHelper.ReadFileWithRetry(resultsPath, maxRetries: 3, delayMs: 10));
                    if (existing != null && existing.runId == runId)
                    {
                        m_IsRunning = false;
                        return;
                    }
                }

                m_IsRunning = false;

                Debug.Log($"UnityLeanMcp: Finalizing test run. Success: {success}, ResultState: {resultState}, Message: {message}");

                var runResult = new UnityTestRunResult
                {
                    runId = runId ?? Guid.NewGuid().ToString("N"),
                    success = success,
                    failCount = failCount,
                    passCount = passCount,
                    skipCount = skipCount,
                    message = message,
                    resultState = resultState,
                    failedTests = new List<FailedTestInfo>(m_FailedTests)
                };

                string json = JsonUtility.ToJson(runResult, true);
                RunTestsHandler.WriteAtomic(resultsPath, json, runResult.runId);
                RunTestsHandler.TryWriteStaticHistory(
                    RunTestsHandler.ResultsFilePath,
                    json,
                    runResult.runId);
                RunTestsHandler.ClearCancellationRequest(runResult.runId);
                RunTestsHandler.StopCancellationMonitoring(runResult.runId);
                RunTestsHandler.DeleteRunningStateIfOwned(runResult.runId);
                RunTestsHandler.ClearCachedRunState();
                UnityLeanMcpOperationStore.Complete(runResult.runId);
                m_RunId = null;
                RunTestsHandler.s_CurrentTestJobGuid = null;
                Debug.Log($"UnityLeanMcp: Playmode/Editmode tests completed. Success: {runResult.success}, Failed: {runResult.failCount}, Passed: {runResult.passCount}, Skipped: {runResult.skipCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Exception in FinalizeTestRun: {ex}");
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            if (test != null && !test.HasChildren && !string.IsNullOrEmpty(m_RunId))
            {
                m_CurrentTestName = test.FullName ?? test.Name ?? "";
                RunTestsHandler.UpdateTestRunProgress(m_RunId, m_TotalTests, m_CompletedTests, m_PassCount, m_FailCount, m_SkipCount, m_CurrentTestName);
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null || result.HasChildren)
            {
                return;
            }

            m_CompletedTests++;
            if (result.TestStatus == TestStatus.Passed)
            {
                m_PassCount++;
            }
            else if (result.TestStatus == TestStatus.Failed)
            {
                m_FailCount++;
                m_FailedTests.Add(new FailedTestInfo
                {
                    name = result.Name,
                    fullName = result.FullName,
                    message = result.Message,
                    stackTrace = result.StackTrace,
                    duration = result.Duration
                });
            }
            else if (result.TestStatus == TestStatus.Skipped)
            {
                m_SkipCount++;
            }

            if (!string.IsNullOrEmpty(m_RunId))
            {
                RunTestsHandler.UpdateTestRunProgress(m_RunId, m_TotalTests, m_CompletedTests, m_PassCount, m_FailCount, m_SkipCount, m_CurrentTestName);
            }
        }
    }
}
