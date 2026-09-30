using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    [Serializable]
    public sealed class UnityLeanMcpOperationState
    {
        public string operationId;
        public string kind;
        public string status;
        public string editorSessionId;
        public string startedUtc;
        public string updatedUtc;
    }

    internal enum BeginOperationResult
    {
        Started,
        AlreadyStarted,
        Busy,
        Invalid
    }

    /// <summary>
    /// In-memory and session-backed gate for Unity operations, eliminating on-disk operation journals.
    /// Delegates active state management to UnityCommandGate.
    /// Provides atomic write utilities for result and diagnostic files.
    /// </summary>
    internal static class UnityLeanMcpOperationStore
    {
        private const string EditorSessionKey = "UnityLeanMcp.EditorSessionId";
        private static volatile string s_EditorSessionId;

        internal static string EditorSessionId => s_EditorSessionId ?? "";

        internal static void EnsureInitialized()
        {
            try
            {
                if (string.IsNullOrEmpty(s_EditorSessionId))
                {
                    string value = SessionState.GetString(EditorSessionKey, "");
                    if (string.IsNullOrEmpty(value))
                    {
                        value = Guid.NewGuid().ToString("N");
                        SessionState.SetString(EditorSessionKey, value);
                    }
                    s_EditorSessionId = value;
                }
                UnityCommandGate.InitializeMainThread();
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to initialize operation store: {ex}");
            }
        }

        internal static BeginOperationResult TryBegin(string operationId, string kind, string status, out UnityLeanMcpOperationState existing)
        {
            var res = UnityCommandGate.TryBegin(kind, operationId, status, out var busyReason);
            existing = Read();
            if (res == BeginGateResult.Busy && existing == null)
            {
                string[] parts = (busyReason ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                existing = new UnityLeanMcpOperationState
                {
                    kind = parts.Length > 0 ? parts[0] : "compile",
                    operationId = parts.Length > 1 ? parts[1] : "",
                    status = OperationStatus.Running,
                    editorSessionId = EditorSessionId,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    updatedUtc = DateTime.UtcNow.ToString("o")
                };
            }

            return res switch
            {
                BeginGateResult.Started => BeginOperationResult.Started,
                BeginGateResult.AlreadyStarted => BeginOperationResult.AlreadyStarted,
                BeginGateResult.Busy => BeginOperationResult.Busy,
                _ => BeginOperationResult.Invalid
            };
        }

        internal static UnityLeanMcpOperationState Read()
        {
            var snap = UnityCommandGate.ReadSnapshot();
            if (snap == null) return null;
            return new UnityLeanMcpOperationState
            {
                operationId = snap.OperationId,
                kind = snap.Kind,
                status = snap.Status,
                editorSessionId = EditorSessionId,
                startedUtc = new DateTime(snap.StartTime > 0 ? snap.StartTime : DateTime.UtcNow.Ticks, DateTimeKind.Utc).ToString("o"),
                updatedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        internal static WorkerOperationStateSnapshot ReadThreadSafeSnapshot()
        {
            var snap = UnityCommandGate.ReadSnapshot();
            if (snap == null) return null;
            return new WorkerOperationStateSnapshot(
                snap.OperationId,
                snap.Kind,
                snap.Status,
                EditorSessionId,
                new DateTime(snap.StartTime > 0 ? snap.StartTime : DateTime.UtcNow.Ticks, DateTimeKind.Utc).ToString("o"),
                DateTime.UtcNow.ToString("o"));
        }

        internal static bool Update(string operationId, string status)
        {
            return UnityCommandGate.Update(operationId, status);
        }

        internal static bool Complete(string operationId)
        {
            UnityCommandGate.Complete(operationId);
            return true;
        }

        internal static bool IsOwnedBy(string operationId, string kind)
        {
            return UnityCommandGate.IsOwnedBy(operationId, kind);
        }

        internal static void WriteAtomic(string path, string content, string operationId, int attempts = 5)
        {
            string tempPath = path + "." + (operationId ?? Guid.NewGuid().ToString("N")) + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                Exception lastException = null;
                for (int i = 0; i < attempts; i++)
                {
                    try
                    {
                        UnityLeanMcpStaticHistoryWriter.MoveWithOverwrite(tempPath, path);
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastException = ex;
                        if (i < attempts - 1)
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastException = ex;
                        if (i < attempts - 1)
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }
                }

                throw new IOException($"Failed to atomically write '{path}' after {attempts} attempts.", lastException);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        /// <summary>
        /// Attempts to update a shared history snapshot without allowing
        /// contention on that non-authoritative file to affect the durable
        /// operation result.
        /// </summary>
        internal static bool TryWriteStaticHistory(string path, string content, string operationId)
        {
            if (UnityLeanMcpStaticHistoryWriter.TryWrite(path, content, out var failure))
            {
                return true;
            }

            Debug.LogWarning($"UnityLeanMcp: Failed to update static history for operation '{operationId}': {failure.Message}");
            return false;
        }
    }
}
