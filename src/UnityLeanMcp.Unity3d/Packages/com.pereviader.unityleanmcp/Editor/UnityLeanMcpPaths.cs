using System;
using System.IO;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class UnityLeanMcpPaths
    {
        private static readonly object s_InitLock = new object();
        private static volatile bool s_Initialized;
        private static string s_ProjectRoot;
        private static string s_TempDir;
        private static string s_PortFile;
        private static string s_DiagnosticsFile;
        private static string s_RefreshResultFile;
        private static string s_TestRunningFile;
        private static string s_TestResultsFile;
        private static string s_EvalResultFile;
        private static string s_WorkerLogFile;

        public static string ProjectRoot
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_ProjectRoot;
            }
            internal set
            {
                ResetForTesting(value);
            }
        }

        public static string TempDir
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_TempDir;
            }
        }
        public static string PortFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_PortFile;
            }
        }
        public static string DiagnosticsFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_DiagnosticsFile;
            }
        }
        public static string RefreshResultFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_RefreshResultFile;
            }
        }

        public const int MaxOperationIdLength = 128;

        public static bool IsValidOperationId(string operationId)
        {
            if (string.IsNullOrEmpty(operationId) || operationId.Length > MaxOperationIdLength)
            {
                return false;
            }

            if (operationId.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            for (int i = 0; i < operationId.Length; i++)
            {
                char c = operationId[i];
                bool isAllowed =
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '.' ||
                    c == '_' ||
                    c == '-';
                if (!isAllowed)
                {
                    return false;
                }
            }

            return true;
        }

        public static string GetRefreshResultFile(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                return RefreshResultFile;
            }

            if (!IsValidOperationId(operationId))
            {
                throw new ArgumentException("Invalid operation id.", nameof(operationId));
            }

            return Path.Combine(TempDir, $"unity_refresh_{operationId}.json");
        }
        public static string TestRunningFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_TestRunningFile;
            }
        }
        public static string TestResultsFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_TestResultsFile;
            }
        }
        public static string EvalResultFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_EvalResultFile;
            }
        }

        public static string GetEvalResultFile(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                return EvalResultFile;
            }

            if (!IsValidOperationId(operationId))
            {
                throw new ArgumentException("Invalid operation id.", nameof(operationId));
            }

            return Path.Combine(TempDir, $"unity_eval_{operationId}.json");
        }

        public static string GetTestResultsFile(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                return TestResultsFile;
            }

            if (!IsValidOperationId(operationId))
            {
                throw new ArgumentException("Invalid operation id.", nameof(operationId));
            }

            return Path.Combine(TempDir, $"unity_test_{operationId}.json");
        }

        public static string GetResultFilePath(string kind, string operationId = null)
        {
            if (string.Equals(kind, OperationKinds.Refresh, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, OperationKinds.Recompile, StringComparison.OrdinalIgnoreCase))
            {
                return GetRefreshResultFile(operationId);
            }

            if (string.Equals(kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
            {
                return GetTestResultsFile(operationId);
            }

            if (string.Equals(kind, OperationKinds.Eval, StringComparison.OrdinalIgnoreCase))
            {
                return GetEvalResultFile(operationId);
            }

            throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unknown operation kind: {kind}");
        }

        public static string LogFile
        {
            get
            {
                if (!s_Initialized) EnsureInitialized();
                return s_WorkerLogFile;
            }
        }

        internal static void ResetForTesting(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException("Project root cannot be null or empty.", nameof(projectRoot));
            }

            lock (s_InitLock)
            {
                InitializeFromProjectRoot(Path.GetFullPath(projectRoot));
            }
        }

        public static void EnsureInitialized()
        {
            if (s_Initialized) return;
            lock (s_InitLock)
            {
                if (s_Initialized) return;
                string projectRoot = null;
                try
                {
                    projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"UnityLeanMcp: Failed to initialize ProjectRoot: {ex}");
                }
                if (string.IsNullOrEmpty(projectRoot)) return;
                InitializeFromProjectRoot(projectRoot);
            }
        }

        private static void InitializeFromProjectRoot(string projectRoot)
        {
            string tempDir = Path.Combine(projectRoot, "Temp");
            s_ProjectRoot = projectRoot;
            s_PortFile = Path.Combine(tempDir, "unity_lean_mcp_port.txt");
            s_DiagnosticsFile = Path.Combine(tempDir, "unity_compilation_errors.txt");
            s_RefreshResultFile = Path.Combine(tempDir, "unity_refresh_result.json");
            s_TestRunningFile = Path.Combine(tempDir, "unity_test_running.txt");
            s_TestResultsFile = Path.Combine(tempDir, "unity_test_results.json");
            s_EvalResultFile = Path.Combine(tempDir, "unity_eval_result.json");
            s_WorkerLogFile = Path.Combine(tempDir, "unity_lean_mcp_worker.log");
            s_TempDir = tempDir;
            s_Initialized = true;
        }
    }
}
