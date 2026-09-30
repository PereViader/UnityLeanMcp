using System;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public enum BeginGateResult
    {
        Started,
        AlreadyStarted,
        Busy,
        Invalid
    }

    [Serializable]
    public sealed class ActiveOperationSnapshot
    {
        public string Kind;
        public string OperationId;
        public string Status;
        public long StartTime;

        public ActiveOperationSnapshot() { }

        public ActiveOperationSnapshot(string kind, string operationId, string status = null, long startTime = 0)
        {
            Kind = kind;
            OperationId = operationId;
            Status = status;
            StartTime = startTime;
        }
    }

    [InitializeOnLoad]
    public static class UnityCommandGate
    {
        [Serializable]
        private sealed class PendingResult
        {
            public string Kind;
            public string OperationId;
            public string Path;
            public string Json;
        }

        private const string PendingResultKey = "UnityLeanMcp_PendingResult";
        private static PendingResult s_PendingResult;
        internal static int RetryThrottleMs = 100;
        private static long s_LastRetryTicks;
        private const string SessionKey = "UnityLeanMcp_ActiveOp";
        private static readonly object s_Lock = new object();
        private static volatile ActiveOperationSnapshot s_Snapshot;

        [InitializeOnLoadMethod]
        public static void InitializeMainThread()
        {
            lock (s_Lock)
            {
                string json = SessionState.GetString(SessionKey, null);
                if (!string.IsNullOrEmpty(json))
                {
                    ActiveOperationSnapshot op = null;
                    try { op = JsonUtility.FromJson<ActiveOperationSnapshot>(json); } catch { }

                    if (op == null || string.IsNullOrEmpty(op.Kind) || string.IsNullOrEmpty(op.OperationId))
                    {
                        SessionState.EraseString(SessionKey);
                        SessionState.EraseString(PendingResultKey);
                        s_PendingResult = null;
                        s_LastRetryTicks = 0;
                        s_Snapshot = null;
                        return;
                    }

                    s_Snapshot = op;
                    string pendingJson = SessionState.GetString(PendingResultKey, null);
                    if (!string.IsNullOrEmpty(pendingJson))
                    {
                        s_PendingResult = JsonUtility.FromJson<PendingResult>(pendingJson);
                        if (s_PendingResult != null && s_PendingResult.OperationId == op.OperationId)
                        {
                            s_LastRetryTicks = 0;
                            EditorApplication.update -= RetryPendingResult;
                            EditorApplication.update += RetryPendingResult;
                            RetryPendingResult();
                            return;
                        }
                        else
                        {
                            SessionState.EraseString(PendingResultKey);
                            s_PendingResult = null;
                        }
                    }

                    if ((op.Kind == OperationKinds.Refresh || op.Kind == OperationKinds.Recompile) &&
                        op.Status == OperationStatus.Requested)
                    {
                        WriteInterruptedResult(op.Kind, op.OperationId, "Compilation request interrupted before execution by domain reload.");
                        return;
                    }

                    // 1. Sweep non-surviving in-domain operations (eval & coverage)
                    if (string.Equals(op.Kind, OperationKinds.Eval, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(op.Kind, OperationKinds.Coverage, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteInterruptedResult(op.Kind, op.OperationId, "Operation interrupted by domain reload.");
                        return;
                    }

                    // Test Runner rebuilds its nonserialized runner registry in its own
                    // InitializeOnLoadMethod. A false answer during bootstrap can precede
                    // that resume, so observe only after all load initializers have run.
                    if (string.Equals(op.Kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
                    {
                        EditorApplication.update -= RecoverTestRun;
                        EditorApplication.update += RecoverTestRun;
                        return;
                    }

                    // 3. For surviving operations (refresh, active test run), restore snapshot
                    s_Snapshot = op;
                }
                else
                {
                    SessionState.EraseString(PendingResultKey);
                    s_PendingResult = null;
                    s_LastRetryTicks = 0;
                    s_Snapshot = null;
                }
            }
        }

        private static void RecoverTestRun()
        {
            var operation = s_Snapshot;
            if (operation == null || operation.Kind != OperationKinds.Test)
            {
                EditorApplication.update -= RecoverTestRun;
                return;
            }
            if (!RunTestsHandler.TryGetTestRunnerActiveState(out bool isActive)) return;
            EditorApplication.update -= RecoverTestRun;
            if (!isActive)
                WriteInterruptedResult(OperationKinds.Test, operation.OperationId, "Test run was interrupted by an unexpected domain reload.");
        }

        // Called lock-free from background worker threads
        public static ActiveOperationSnapshot ReadSnapshot() => s_Snapshot;

        // Called on main thread to begin operation
        public static BeginGateResult TryBegin(string kind, string opId, string status, out string busyReason)
        {
            if (string.IsNullOrWhiteSpace(opId) || string.IsNullOrWhiteSpace(kind))
            {
                busyReason = "invalid";
                return BeginGateResult.Invalid;
            }

            lock (s_Lock)
            {
                if (s_Snapshot != null)
                {
                    if (s_Snapshot.OperationId == opId && string.Equals(s_Snapshot.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        busyReason = null;
                        return BeginGateResult.AlreadyStarted;
                    }

                    busyReason = $"{s_Snapshot.Kind} {s_Snapshot.OperationId}";
                    return BeginGateResult.Busy;
                }

                if (!RunTestsHandler.TryGetTestRunnerActiveState(out bool testRunActive) || testRunActive)
                {
                    busyReason = "test";
                    return BeginGateResult.Busy;
                }

                if (EditorApplication.isCompiling)
                {
                    busyReason = "compile";
                    return BeginGateResult.Busy;
                }

                var op = new ActiveOperationSnapshot
                {
                    Kind = kind,
                    OperationId = opId,
                    Status = status,
                    StartTime = DateTime.UtcNow.Ticks
                };

                // Safe publication ordering: Persist to native SessionState first, then publish volatile snapshot
                SessionState.SetString(SessionKey, JsonUtility.ToJson(op));
                s_Snapshot = op;
                busyReason = null;
                return BeginGateResult.Started;
            }
        }

        public static bool Update(string opId, string status)
        {
            lock (s_Lock)
            {
                if (s_Snapshot != null && s_Snapshot.OperationId == opId)
                {
                    var updated = new ActiveOperationSnapshot(s_Snapshot.Kind, s_Snapshot.OperationId, status, s_Snapshot.StartTime);
                    SessionState.SetString(SessionKey, JsonUtility.ToJson(updated));
                    s_Snapshot = updated;
                    return true;
                }
                return false;
            }
        }

        // Called on the main thread only after terminal publication or proven non-admission.
        public static void Complete(string opId)
        {
            lock (s_Lock)
            {
                if (s_Snapshot != null && (string.IsNullOrEmpty(opId) || s_Snapshot.OperationId == opId))
                {
                    // Safe publication ordering: Erase native SessionState first, then clear volatile snapshot
                    SessionState.EraseString(SessionKey);
                    s_Snapshot = null;
                }
            }
        }

        public static void ForceReset()
        {
            lock (s_Lock)
            {
                EditorApplication.update -= RetryPendingResult;
                EditorApplication.update -= RecoverTestRun;
                SessionState.EraseString(PendingResultKey);
                s_PendingResult = null;
                s_LastRetryTicks = 0;
                SessionState.EraseString(SessionKey);
                s_Snapshot = null;
            }
        }

        public static bool IsOwnedBy(string opId, string kind)
        {
            var snap = s_Snapshot;
            if (snap == null) return false;
            if (snap.OperationId != opId) return false;
            return kind == null || string.Equals(snap.Kind, kind, StringComparison.OrdinalIgnoreCase);
        }

        // Native SessionState retains the exact completed result across domain reloads.
        // Each Editor tick makes a bounded I/O attempt; ownership remains held on failure.
        internal static void PublishResult(string kind, string opId, string path, string json)
        {
            if (!IsOwnedBy(opId, kind)) return;
            if (s_PendingResult != null && s_PendingResult.OperationId == opId) return;
            var pending = new PendingResult { Kind = kind, OperationId = opId, Path = path, Json = json };
            SessionState.SetString(PendingResultKey, JsonUtility.ToJson(pending));
            s_PendingResult = pending;
            s_LastRetryTicks = 0;
            EditorApplication.update -= RetryPendingResult;
            EditorApplication.update += RetryPendingResult;
            RetryPendingResult();
        }

        internal static bool HasPendingResult(string opId) => s_PendingResult != null && s_PendingResult.OperationId == opId;

        private static void RetryPendingResult()
        {
            var pending = s_PendingResult;
            if (pending == null) return;

            long now = DateTime.UtcNow.Ticks;
            if (RetryThrottleMs > 0 && s_LastRetryTicks > 0)
            {
                long elapsedMs = (now - s_LastRetryTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs < RetryThrottleMs)
                    return;
            }
            s_LastRetryTicks = now;

            try
            {
                if (pending.Kind == OperationKinds.Test) RunTestsHandler.RestoreCoverage(pending.OperationId);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pending.Path));
                UnityLeanMcpOperationStore.WriteAtomic(pending.Path, pending.Json, pending.OperationId, attempts: 1);
                if (pending.Kind == OperationKinds.Test)
                {
                    UnityLeanMcpOperationStore.TryWriteStaticHistory(UnityLeanMcpPaths.TestResultsFile, pending.Json, pending.OperationId);
                    RunTestsHandler.CleanupTestRun(pending.OperationId);
                }
                else if (pending.Kind == OperationKinds.Refresh || pending.Kind == OperationKinds.Recompile)
                {
                    UnityLeanMcpOperationStore.TryWriteStaticHistory(UnityLeanMcpPaths.RefreshResultFile, pending.Json, pending.OperationId);
                }
                Complete(pending.OperationId);
                SessionState.EraseString(PendingResultKey);
                s_PendingResult = null;
                s_LastRetryTicks = 0;
                EditorApplication.update -= RetryPendingResult;
            }
            catch (Exception ex)
            {
                WorkerDiagnosticsLogger.Warning(UnityLeanMcpPaths.LogFile,
                    "Terminal result publication will retry for " + pending.Kind + " " + pending.OperationId + ": " + ex.Message);
            }
        }

        private static void WriteInterruptedResult(string kind, string opId, string message)
        {
            string path = UnityLeanMcpPaths.GetResultFilePath(kind, opId);
            string json = "{\"operationId\":\"" + opId + "\",\"runId\":\"" + opId + "\",\"success\":false,\"interrupted\":true,\"resultState\":\"Interrupted\",\"message\":\"" + message + "\"}";
            PublishResult(kind, opId, path, json);
        }
    }
}
