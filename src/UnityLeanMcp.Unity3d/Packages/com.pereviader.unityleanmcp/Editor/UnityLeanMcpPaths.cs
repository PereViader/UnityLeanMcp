using System;
using System.IO;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class UnityLeanMcpPaths
    {
        private static string s_ProjectRoot;
        private static string s_TempDir;
        private static string s_PortFile;
        private static string s_OperationFile;
        private static string s_DiagnosticsFile;
        private static string s_RefreshResultFile;
        private static string s_TestRunningFile;
        private static string s_TestCancellationFile;
        private static string s_TestResultsFile;
        private static string s_EvalResultFile;
        private static string s_WorkerLogFile;

        public static string ProjectRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_ProjectRoot)) EnsureInitialized();
                return s_ProjectRoot;
            }
        }

        public static string TempDir
        {
            get
            {
                if (string.IsNullOrEmpty(s_TempDir)) EnsureInitialized();
                return s_TempDir;
            }
        }
        public static string PortFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_PortFile)) EnsureInitialized();
                return s_PortFile;
            }
        }
        public static string OperationFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_OperationFile)) EnsureInitialized();
                return s_OperationFile;
            }
        }
        public static string DiagnosticsFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_DiagnosticsFile)) EnsureInitialized();
                return s_DiagnosticsFile;
            }
        }
        public static string RefreshResultFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_RefreshResultFile)) EnsureInitialized();
                return s_RefreshResultFile;
            }
        }

        public static string GetRefreshResultFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? RefreshResultFile : Path.Combine(TempDir, $"unity_refresh_{operationId}.json");
        public static string TestRunningFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_TestRunningFile)) EnsureInitialized();
                return s_TestRunningFile;
            }
        }
        public static string TestCancellationFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_TestCancellationFile)) EnsureInitialized();
                return s_TestCancellationFile;
            }
        }
        public static string TestResultsFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_TestResultsFile)) EnsureInitialized();
                return s_TestResultsFile;
            }
        }
        public static string EvalResultFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_EvalResultFile)) EnsureInitialized();
                return s_EvalResultFile;
            }
        }

        public static string GetEvalResultFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? EvalResultFile : Path.Combine(TempDir, $"unity_eval_{operationId}.json");

        public static string GetTestResultsFile(string operationId) =>
            string.IsNullOrEmpty(operationId) ? TestResultsFile : Path.Combine(TempDir, $"unity_test_{operationId}.json");

        public static string LogFile
        {
            get
            {
                if (string.IsNullOrEmpty(s_WorkerLogFile)) EnsureInitialized();
                return s_WorkerLogFile;
            }
        }

        public static void EnsureInitialized()
        {
            if (!string.IsNullOrEmpty(s_TempDir)) return;
            try
            {
                s_ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to initialize ProjectRoot: {ex}");
            }
            if (string.IsNullOrEmpty(s_ProjectRoot)) return;
            s_TempDir = Path.Combine(s_ProjectRoot, "Temp");
            s_PortFile = Path.Combine(s_TempDir, "unity_lean_mcp_port.txt");
            s_OperationFile = Path.Combine(s_TempDir, "unity_lean_mcp_operation.json");
            s_DiagnosticsFile = Path.Combine(s_TempDir, "unity_compilation_errors.txt");
            s_RefreshResultFile = Path.Combine(s_TempDir, "unity_refresh_result.json");
            s_TestRunningFile = Path.Combine(s_TempDir, "unity_test_running.txt");
            s_TestCancellationFile = Path.Combine(s_TempDir, "unity_test_cancellation.txt");
            s_TestResultsFile = Path.Combine(s_TempDir, "unity_test_results.json");
            s_EvalResultFile = Path.Combine(s_TempDir, "unity_eval_result.json");
            s_WorkerLogFile = Path.Combine(s_TempDir, "unity_lean_mcp_worker.log");
        }
    }
}
