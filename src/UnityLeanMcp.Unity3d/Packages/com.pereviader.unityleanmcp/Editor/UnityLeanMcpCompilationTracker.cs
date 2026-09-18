using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public static class UnityLeanMcpCompilationTracker
    {
        private static readonly Dictionary<string, List<string>> s_AssemblyDiagnostics = new Dictionary<string, List<string>>();
        private static readonly object s_DiagnosticsLock = new object();

        private const int CompilationRequestIdleFrameThreshold = 3;

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;
        private static volatile bool s_RefreshPending;
        private static volatile bool s_ScriptCompilationFailed;
        private static volatile bool s_CompilationRequested;
        // Conservative latch for changes reported by Unity's project-change
        // notification. It starts set after a domain load because the client
        // cannot safely assume that an earlier process refreshed this domain.
        private static volatile bool s_RefreshRequired = true;
        private static int s_CompilationRequestIdleFrames;
        private static string s_ObservedOperationId;
        private static int s_SettledUpdateCount;

        public static bool IsCompiling => s_IsCompiling;
        public static bool IsUpdating => s_IsUpdating;
        public static bool ScriptCompilationFailed => s_ScriptCompilationFailed;
        public static bool RefreshRequired => s_RefreshRequired;

        public static bool RefreshPending
        {
            get => s_RefreshPending;
            set => s_RefreshPending = value;
        }

        public static bool CompilationRequested
        {
            get => s_CompilationRequested;
            set
            {
                s_CompilationRequested = value;
                s_CompilationRequestIdleFrames = 0;
            }
        }

        private static bool s_Initialized;
        private static readonly object s_InitLock = new object();

        private static Type s_LogEntriesType;
        private static Type s_LogEntryType;
        private static MethodInfo s_GetCountMethod;
        private static MethodInfo s_GetEntryInternalMethod;
        private static MethodInfo s_StartGettingEntriesMethod;
        private static MethodInfo s_EndGettingEntriesMethod;
        private static MethodInfo s_ClearMethod;
        private static FieldInfo s_ConditionField;
        private static FieldInfo s_ErrorNumField;
        private static FieldInfo s_FileField;
        private static FieldInfo s_LineField;
        private static FieldInfo s_ColumnField;
        private static FieldInfo s_ModeField;
        private static bool s_LogReflectionInitialized;

        private static void EnsureLogReflectionCached()
        {
            if (s_LogReflectionInitialized) return;

            s_LogEntriesType = CommandHelper.FindType("UnityEditor.LogEntries") ?? CommandHelper.FindType("UnityEditorInternal.LogEntries");
            s_LogEntryType = CommandHelper.FindType("UnityEditor.LogEntry") ?? CommandHelper.FindType("UnityEditorInternal.LogEntry");

            if (s_LogEntriesType != null)
            {
                s_GetCountMethod = s_LogEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_GetEntryInternalMethod = s_LogEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_StartGettingEntriesMethod = s_LogEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_EndGettingEntriesMethod = s_LogEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_ClearMethod = s_LogEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (s_LogEntryType != null)
            {
                s_ConditionField = s_LogEntryType.GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? s_LogEntryType.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_ErrorNumField = s_LogEntryType.GetField("errorNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_FileField = s_LogEntryType.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_LineField = s_LogEntryType.GetField("line", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_ColumnField = s_LogEntryType.GetField("column", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_ModeField = s_LogEntryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            s_LogReflectionInitialized = true;
        }

        public static void EnsureInitialized()
        {
            if (s_Initialized) return;
            lock (s_InitLock)
            {
                if (s_Initialized) return;
                s_Initialized = true;
                InitializeMainThread();
            }
        }

        private static void InitializeMainThread()
        {
            UnityLeanMcpPaths.EnsureInitialized();
            UnityLeanMcpOperationStore.EnsureInitialized();
            EnsureLogReflectionCached();
            UpdateCompilationState();
            var operation = UnityLeanMcpOperationStore.Read();
            bool resumingCompilation = operation != null &&
                (operation.kind == OperationKinds.Refresh || operation.kind == OperationKinds.Recompile) &&
                operation.editorSessionId == UnityLeanMcpOperationStore.EditorSessionId;
            if (!resumingCompilation)
            {
                WriteActiveErrorsToFile();
            }
            EditorApplication.update -= UpdateCompilationState;
            EditorApplication.update += UpdateCompilationState;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.quitting -= DeleteDiagnosticsFile;
            EditorApplication.quitting += DeleteDiagnosticsFile;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted -= OnCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompilationStarted;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished -= OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void OnCompilationStarted(object obj)
        {
            s_IsCompiling = true;
            s_CompilationRequested = false;
            s_CompilationRequestIdleFrames = 0;
        }

        private static void OnCompilationFinished(object obj)
        {
            s_IsCompiling = false;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;
            WriteActiveErrorsToFile();
        }

        private static void OnProjectChanged()
        {
            // This event is also raised for external edits that Unity has not
            // necessarily imported yet. Keep the latch set until the
            // correlated refresh operation reaches a durable terminal result.
            s_RefreshRequired = true;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            string key = assemblyPath ?? "";
            lock (s_DiagnosticsLock)
            {
                if (messages == null || messages.Length == 0)
                {
                    s_AssemblyDiagnostics.Remove(key);
                }
                else
                {
                    var list = new List<string>();
                    foreach (var msg in messages)
                    {
                        if (msg.type == UnityEditor.Compilation.CompilerMessageType.Error || msg.type == UnityEditor.Compilation.CompilerMessageType.Warning)
                        {
                            bool isError = msg.type == UnityEditor.Compilation.CompilerMessageType.Error;
                            string formatted = FormatCompilerDiagnostic(msg.message, msg.file, msg.line, msg.column, isError);
                            if (!string.IsNullOrEmpty(formatted))
                            {
                                list.Add(formatted);
                            }
                        }
                    }

                    if (list.Count > 0)
                    {
                        s_AssemblyDiagnostics[key] = list;
                    }
                    else
                    {
                        s_AssemblyDiagnostics.Remove(key);
                    }
                }
            }

            // Persist at the authoritative compiler callback. A domain reload
            // can begin before compilationFinished or the next Editor update.
            WriteCapturedDiagnosticsSnapshot();
        }

        private static List<string> GetCapturedDiagnosticsSnapshot()
        {
            var diagnostics = new List<string>();
            lock (s_DiagnosticsLock)
            {
                foreach (var list in s_AssemblyDiagnostics.Values)
                {
                    if (list != null && list.Count > 0)
                    {
                        diagnostics.AddRange(list);
                    }
                }
            }
            return diagnostics;
        }

        private static void WriteCapturedDiagnosticsSnapshot()
        {
            var diagnostics = GetCapturedDiagnosticsSnapshot();
            if (diagnostics.Count > 0)
            {
                WriteDiagnosticsFileAtomically(UnityLeanMcpPaths.DiagnosticsFile, diagnostics);
            }
        }

        private static string FormatCompilerDiagnostic(string rawMessage, string file, int line, int column, bool isError)
        {
            string msg = (rawMessage ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) return null;

            if (System.Text.RegularExpressions.Regex.IsMatch(msg, @"^.+?\([0-9]+,[0-9]+\):\s*(error|warning)\s+[a-zA-Z0-9]+:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                int newlineIdx = msg.IndexOfAny(new[] { '\r', '\n' });
                if (newlineIdx >= 0)
                {
                    msg = msg.Substring(0, newlineIdx).Trim();
                }
                return msg;
            }

            string typeStr = isError ? "error" : "warning";
            if (msg.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(6).TrimStart();
            }
            else if (msg.StartsWith("warning ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(8).TrimStart();
            }

            int nlIdx = msg.IndexOfAny(new[] { '\r', '\n' });
            if (nlIdx >= 0)
            {
                msg = msg.Substring(0, nlIdx).Trim();
            }

            if (!string.IsNullOrEmpty(file) && line > 0)
            {
                return $"{file}({line},{column}): {typeStr} {msg}";
            }

            if (!string.IsNullOrEmpty(file))
            {
                return $"{file}: {typeStr} {msg}";
            }

            return $"{typeStr} {msg}";
        }

        public static void UpdateCompilationState()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (s_CompilationRequested)
            {
                if (s_IsCompiling || EditorApplication.isCompiling)
                {
                    s_CompilationRequested = false;
                    s_CompilationRequestIdleFrames = 0;
                }
                else if (s_RefreshPending || s_IsUpdating || EditorApplication.isUpdating)
                {
                    s_CompilationRequestIdleFrames = 0;
                }
                else
                {
                    s_CompilationRequestIdleFrames++;
                    if (s_CompilationRequestIdleFrames >= CompilationRequestIdleFrameThreshold)
                    {
                        s_CompilationRequested = false;
                        s_CompilationRequestIdleFrames = 0;
                        WriteActiveErrorsToFile();
                    }
                }
            }
            else
            {
                s_CompilationRequestIdleFrames = 0;
            }

            ObserveOperationUntilSettled();
        }

        /// <summary>
        /// Completes refresh/recompile ownership only after Unity has reported a
        /// settled Editor state on two separate update ticks. This observer is
        /// reconstructed after a domain reload from the durable journal.
        /// </summary>
        internal static void ObserveOperationUntilSettled()
        {
            var operation = UnityLeanMcpOperationStore.Read();
            if (operation == null || (operation.kind != OperationKinds.Refresh && operation.kind != OperationKinds.Recompile))
            {
                s_ObservedOperationId = null;
                s_SettledUpdateCount = 0;
                return;
            }

            if (operation.editorSessionId != UnityLeanMcpOperationStore.EditorSessionId || operation.status == OperationStatus.Interrupted)
            {
                return;
            }

            if (s_ObservedOperationId != operation.operationId)
            {
                s_ObservedOperationId = operation.operationId;
                s_SettledUpdateCount = 0;
            }

            if (s_RefreshPending || s_CompilationRequested || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                s_SettledUpdateCount = 0;
                UnityLeanMcpOperationStore.Update(operation.operationId, OperationStatus.WaitingForUnity);
                return;
            }

            s_SettledUpdateCount++;
            if (s_SettledUpdateCount < 2)
            {
                return;
            }

            WriteActiveErrorsToFile();
            var result = new UnityRefreshResult
            {
                operationId = operation.operationId,
                success = !EditorUtility.scriptCompilationFailed,
                interrupted = false,
                message = EditorUtility.scriptCompilationFailed ? "Compilation failed" : ""
            };
            string json = JsonUtility.ToJson(result, true);
            UnityLeanMcpOperationStore.WriteAtomic(
                UnityLeanMcpPaths.GetRefreshResultFile(operation.operationId),
                json,
                operation.operationId);
            UnityLeanMcpOperationStore.TryWriteStaticHistory(
                UnityLeanMcpPaths.RefreshResultFile,
                json,
                operation.operationId);
            s_RefreshRequired = false;
            UnityLeanMcpOperationStore.Complete(operation.operationId);
            s_ObservedOperationId = null;
            s_SettledUpdateCount = 0;
        }

        internal static bool TryReadRefreshResultThreadSafe(string operationId, out WorkerRefreshResultSnapshot result)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                result = null;
                return false;
            }

            string path = UnityLeanMcpPaths.GetRefreshResultFile(operationId);
            if (WorkerThreadSnapshots.TryReadRefreshResult(path, out var persisted) && persisted.OperationId == operationId)
            {
                result = persisted;
                return true;
            }

            result = null;
            return false;
        }

        internal static void WriteInterruptedRefreshResult(string operationId, string message)
        {
            var result = new UnityRefreshResult
            {
                operationId = operationId,
                success = false,
                interrupted = true,
                message = message
            };
            string json = JsonUtility.ToJson(result, true);
            UnityLeanMcpOperationStore.WriteAtomic(
                UnityLeanMcpPaths.GetRefreshResultFile(operationId),
                json,
                operationId);
            UnityLeanMcpOperationStore.TryWriteStaticHistory(
                UnityLeanMcpPaths.RefreshResultFile,
                json,
                operationId);
            UnityLeanMcpOperationStore.Complete(operationId);
        }

        public static void ClearActiveEntries()
        {
            try
            {
                EnsureLogReflectionCached();
                s_ClearMethod?.Invoke(null, null);
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityLeanMcp: Failed to clear active compilation diagnostics: {e}");
            }
        }

        internal static void ClearCapturedDiagnostics()
        {
            lock (s_DiagnosticsLock)
            {
                s_AssemblyDiagnostics.Clear();
            }
        }

        public static void DeleteDiagnosticsFile()
        {
            try
            {
                string diagnosticsPath = UnityLeanMcpPaths.DiagnosticsFile;
                if(File.Exists(diagnosticsPath))
                {
                    File.Delete(diagnosticsPath);
                }
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityLeanMcp: Failed to delete compilation diagnostics file: {e}");
            }
        }

        public static void WriteActiveErrorsToFile()
        {
            try
            {
                string errorsPath = UnityLeanMcpPaths.DiagnosticsFile;
                var diagnostics = GetCapturedDiagnosticsSnapshot();

                if (diagnostics.Count == 0)
                {
                    EnsureLogReflectionCached();

                    if (s_LogEntriesType != null && s_LogEntryType != null && s_GetCountMethod != null && s_GetEntryInternalMethod != null && s_ConditionField != null && s_ModeField != null)
                    {
                        s_StartGettingEntriesMethod?.Invoke(null, null);
                        try
                        {
                            int count = (int) s_GetCountMethod.Invoke(null, null);
                            var logEntry = Activator.CreateInstance(s_LogEntryType);
                            var parameters = new object[] { 0, logEntry };

                            for (int i = 0; i < count; i++)
                            {
                                parameters[0] = i;
                                s_GetEntryInternalMethod.Invoke(null, parameters);
                                var currentEntry = parameters[1];

                                string message = (string) s_ConditionField.GetValue(currentEntry);
                                int mode = (int) s_ModeField.GetValue(currentEntry);
                                bool isCompileError = (mode & (1 << 11)) != 0 || (!string.IsNullOrEmpty(message) && message.Contains("error CS"));
                                bool isCompileWarning = (mode & (1 << 12)) != 0 || (!string.IsNullOrEmpty(message) && message.Contains("warning CS"));

                                if (isCompileError || isCompileWarning)
                                {
                                    string file = s_FileField != null ? (string) s_FileField.GetValue(currentEntry) : "";
                                    int line = s_LineField != null ? (int) s_LineField.GetValue(currentEntry) : 0;
                                    int column = s_ColumnField != null ? (int) s_ColumnField.GetValue(currentEntry) : 0;

                                    string formatted = FormatCompilerDiagnostic(message, file, line, column, isCompileError);
                                    if (!string.IsNullOrEmpty(formatted))
                                    {
                                        diagnostics.Add(formatted);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            s_EndGettingEntriesMethod?.Invoke(null, null);
                        }
                    }
                }

                if (diagnostics.Count > 0)
                {
                    WriteDiagnosticsFileAtomically(errorsPath, diagnostics);
                }
                else if (EditorUtility.scriptCompilationFailed)
                {
                    WriteFallbackDiagnosticsIfCompilationFailed("Unity editor reports scriptCompilationFailed is true, but no compiler diagnostics were captured.");
                }
                else
                {
                    DeleteDiagnosticsFile();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityLeanMcp: Failed to write active compilation errors: {e}");
                WriteFallbackDiagnosticsIfCompilationFailed($"Unity editor reports scriptCompilationFailed is true, but UnityLeanMcp failed to capture compiler diagnostics: {e.Message}");
            }
        }

        private static void WriteDiagnosticsFileAtomically(string errorsPath, IEnumerable<string> lines)
        {
            try
            {
                string content = string.Join(Environment.NewLine, lines);
                UnityLeanMcpOperationStore.WriteAtomic(errorsPath, content, Guid.NewGuid().ToString("N"));
            }
            catch (Exception e)
            {
                Debug.LogError($"UnityLeanMcp: Failed to write diagnostics file atomically to {errorsPath}: {e}");
            }
        }

        private static void WriteFallbackDiagnosticsIfCompilationFailed(string message)
        {
            try
            {
                if(!EditorUtility.scriptCompilationFailed)
                {
                    DeleteDiagnosticsFile();
                    return;
                }

                string diagnosticsPath = UnityLeanMcpPaths.DiagnosticsFile;
                string diagnostic = $"UnityLeanMcp(1,1): error UC0001: {message}";
                WriteDiagnosticsFileAtomically(diagnosticsPath, new[] { diagnostic });
            }
            catch(Exception e)
            {
                Debug.LogError($"UnityLeanMcp: Failed to write fallback compilation diagnostics: {e}");
            }
        }
    }
}
