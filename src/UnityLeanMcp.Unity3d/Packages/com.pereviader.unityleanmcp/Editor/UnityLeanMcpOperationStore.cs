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
    /// Durable, project-scoped ownership for commands which may outlive their
    /// socket or managed AppDomain. All mutations happen on Unity's main thread.
    /// </summary>
    internal static class UnityLeanMcpOperationStore
    {
        private const string EditorSessionKey = "UnityLeanMcp.EditorSessionId";
        private static readonly OperationStateCache s_CachedState = new OperationStateCache();
        private static string s_EditorSessionId;

        internal static string OperationFilePath => UnityLeanMcpPaths.OperationFile;

        internal static string EditorSessionId
        {
            get
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

                return s_EditorSessionId;
            }
        }

        internal static void EnsureInitialized()
        {
            try
            {
                _ = EditorSessionId;
                Read();
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to initialize operation store: {ex}");
            }
        }

        internal static BeginOperationResult TryBegin(string operationId, string kind, string status, out UnityLeanMcpOperationState existing)
        {
            existing = Read();
            if (!IsValidToken(operationId) || !IsValidToken(kind))
            {
                return BeginOperationResult.Invalid;
            }

            if (existing != null)
            {
                if (existing.operationId == operationId && existing.kind == kind)
                {
                    return BeginOperationResult.AlreadyStarted;
                }

                return BeginOperationResult.Busy;
            }

            string now = DateTime.UtcNow.ToString("o");
            var state = new UnityLeanMcpOperationState
            {
                operationId = operationId,
                kind = kind,
                status = status,
                editorSessionId = EditorSessionId,
                startedUtc = now,
                updatedUtc = now
            };
            Write(state);
            existing = state;
            return BeginOperationResult.Started;
        }

        internal static UnityLeanMcpOperationState Read()
        {
            try
            {
                string json;
                WorkerThreadSnapshots.FileReadStatus fileStatus =
                    WorkerThreadSnapshots.TryReadFileWithStatus(OperationFilePath, out json);
                if (fileStatus == WorkerThreadSnapshots.FileReadStatus.Missing)
                {
                    s_CachedState.Clear();
                    return null;
                }

                if (fileStatus == WorkerThreadSnapshots.FileReadStatus.Unavailable)
                {
                    return FromWorkerSnapshot(s_CachedState.GetCached());
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    s_CachedState.Clear();
                    return null;
                }

                var state = JsonUtility.FromJson<UnityLeanMcpOperationState>(json);
                if (state == null || !IsValidToken(state.operationId) || !IsValidToken(state.kind))
                {
                    QuarantineMalformedRecord();
                    s_CachedState.Clear();
                    return null;
                }

                SetCachedState(state);
                return state;
            }
            catch (Exception ex)
            {
                s_CachedState.Clear();
                Debug.LogError($"UnityLeanMcp: Failed to read operation journal: {ex}");
                return null;
            }
        }

        internal static WorkerOperationStateSnapshot ReadThreadSafeSnapshot()
        {
            s_CachedState.Read(OperationFilePath, out var snapshot);
            return snapshot;
        }

        internal static bool Update(string operationId, string status)
        {
            var state = Read();
            if (state == null || state.operationId != operationId)
            {
                return false;
            }

            state.status = status;
            state.updatedUtc = DateTime.UtcNow.ToString("o");
            Write(state);
            return true;
        }

        internal static bool Complete(string operationId)
        {
            var state = Read();
            if (state == null || state.operationId != operationId)
            {
                return false;
            }

            try
            {
                File.Delete(OperationFilePath);
                s_CachedState.Clear();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to complete operation '{operationId}': {ex}");
                return false;
            }
        }

        internal static bool IsOwnedBy(string operationId, string kind)
        {
            var state = Read();
            return state != null && state.operationId == operationId && state.kind == kind;
        }

        internal static void Write(UnityLeanMcpOperationState state)
        {
            Directory.CreateDirectory(UnityLeanMcpPaths.TempDir);
            WriteAtomic(OperationFilePath, JsonUtility.ToJson(state, true), state.operationId);
            SetCachedState(state);
        }

        internal static void WriteAtomic(string path, string content, string operationId)
        {
            string tempPath = path + "." + (operationId ?? Guid.NewGuid().ToString("N")) + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                Exception lastException = null;
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(tempPath, path, null);
                        }
                        else
                        {
                            File.Move(tempPath, path);
                        }
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastException = ex;
                        if (i < 4)
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastException = ex;
                        if (i < 4)
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }
                }

                throw new IOException($"Failed to atomically write '{path}' after 5 attempts.", lastException);
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

        private static bool IsValidToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                {
                    return false;
                }
            }

            return true;
        }

        private static void SetCachedState(UnityLeanMcpOperationState state)
        {
            s_CachedState.Set(ToWorkerSnapshot(state));
        }

        private static UnityLeanMcpOperationState FromWorkerSnapshot(WorkerOperationStateSnapshot state)
        {
            if (state == null) return null;
            return new UnityLeanMcpOperationState
            {
                operationId = state.OperationId,
                kind = state.Kind,
                status = state.Status,
                editorSessionId = state.EditorSessionId,
                startedUtc = state.StartedUtc,
                updatedUtc = state.UpdatedUtc
            };
        }

        private static WorkerOperationStateSnapshot ToWorkerSnapshot(UnityLeanMcpOperationState state)
        {
            if (state == null) return null;
            return new WorkerOperationStateSnapshot(
                state.operationId,
                state.kind,
                state.status,
                state.editorSessionId,
                state.startedUtc,
                state.updatedUtc);
        }

        private static void QuarantineMalformedRecord()
        {
            try
            {
                string quarantinePath = OperationFilePath + ".invalid." + Guid.NewGuid().ToString("N");
                File.Move(OperationFilePath, quarantinePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to quarantine malformed operation journal: {ex}");
            }
        }
    }
}
