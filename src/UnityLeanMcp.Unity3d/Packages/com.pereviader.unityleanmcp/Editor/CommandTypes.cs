using System;
using System.Collections.Generic;

namespace UnityLeanMcp
{
    internal static class OperationKinds
    {
        public const string Refresh = "refresh";
        public const string Recompile = "recompile";
        public const string Test = "test";
        public const string Execute = "execute";
        public const string Eval = "eval";
    }

    internal static class OperationStatus
    {
        public const string Queued = "Queued";
        public const string Running = "Running";
        public const string Executing = "Executing";
        public const string Compiling = "Compiling";
        public const string Refreshing = "Refreshing";
        public const string Recompiling = "Recompiling";
        public const string Cancelling = "Cancelling";
        public const string Requested = "Requested";
        public const string WaitingForUnity = "WaitingForUnity";
        public const string Reloading = "Reloading";
        public const string ShuttingDown = "ShuttingDown";
        public const string Interrupted = "Interrupted";
        public const string Cancelled = "Cancelled";
        public const string Completed = "Completed";
    }

    [Serializable]
    public class FailedTestInfo
    {
        public string name;
        public string fullName;
        public string message;
        public string stackTrace;
        public double duration;
    }

    [Serializable]
    public class RunTestsArgs
    {
        public string mode;
        public string[] testNames;
        public string[] groupNames;
        public string[] categoryNames;
        public string[] assemblyNames;
        public bool failedOnly;
    }

    [Serializable]
    public class UnityTestRunState
    {
        public string runId;
        public string mode;
        public string filter;
        public string category;
        public string[] testNames;
        public string[] groupNames;
        public string[] categoryNames;
        public string[] assemblyNames;
        public string status;
        public string jobGuid;
        public string startedUtc;
        public int totalTests;
        public int completedTests;
        public int passCount;
        public int failCount;
        public int skipCount;
        public string currentTestName;
    }

    public interface IOperationResult
    {
        string OperationId { get; set; }
        bool Success { get; set; }
        bool Interrupted { get; set; }
        string Message { get; set; }
    }

    [Serializable]
    public class UnityTestRunResult : IOperationResult
    {
        public string runId;
        public bool success;
        public int failCount;
        public int passCount;
        public int skipCount;
        public string message;
        public string resultState;
        public List<FailedTestInfo> failedTests;

        public string OperationId { get => runId; set => runId = value; }
        public bool Success { get => success; set => success = value; }
        public bool Interrupted
        {
            get => resultState == "Interrupted";
            set
            {
                if (value)
                {
                    resultState = "Interrupted";
                }
                else if (resultState == "Interrupted")
                {
                    resultState = success ? "Passed" : (failCount > 0 ? "Failed" : "");
                }
            }
        }
        public string Message { get => message; set => message = value; }
    }

    [Serializable]
    public class ConsoleLogEntry
    {
        public string message;
        public string logType;
    }

    [Serializable]
    public class UnityRefreshResult : IOperationResult
    {
        public string operationId;
        public bool success;
        public bool interrupted;
        public string message;

        public string OperationId { get => operationId; set => operationId = value; }
        public bool Success { get => success; set => success = value; }
        public bool Interrupted { get => interrupted; set => interrupted = value; }
        public string Message { get => message; set => message = value; }
    }

    [Serializable]
    public class UnityOperationResult : IOperationResult
    {
        public string operationId;
        public bool success;
        public bool interrupted;
        public string message;
        public double duration;
        public string payload;
        public List<ConsoleLogEntry> logs;

        public string OperationId { get => operationId; set => operationId = value; }
        public bool Success { get => success; set => success = value; }
        public bool Interrupted { get => interrupted; set => interrupted = value; }
        public string Message { get => message; set => message = value; }
    }

    [Serializable]
    public class UnityExecuteResult : UnityOperationResult
    {
    }

    [Serializable]
    public class UnityEvalResult : UnityOperationResult
    {
    }
}
