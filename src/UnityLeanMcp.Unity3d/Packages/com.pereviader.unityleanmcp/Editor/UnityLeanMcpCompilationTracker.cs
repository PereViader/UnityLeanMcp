using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    [Serializable]
    public sealed class ScopedDiagnostics
    {
        public string operationId;
        public List<string> messages = new List<string>();
    }

    public static class UnityLeanMcpCompilationTracker
    {
        private static readonly Dictionary<string, List<string>> s_AssemblyDiagnostics = new Dictionary<string, List<string>>();
        private static readonly object s_DiagnosticsLock = new object();
        private const string ScopedDiagnosticsKey = "UnityLeanMcp_RefreshDiagnostics";
        private static ScopedDiagnostics s_ScopedDiagnostics = new ScopedDiagnostics();
        private static int s_MainThreadId;

        private static readonly System.Text.RegularExpressions.Regex s_LeadingLocationRegex = new System.Text.RegularExpressions.Regex(
            @"^(?<file>(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages|[A-Za-z0-9_.\-@\+\(\)]+)[\\/])[^:\r\n'""]*?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?)?:\s*(?<rest>.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex s_TrailingShaderLocationRegex = new System.Text.RegularExpressions.Regex(
            @"\bat\s+(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n'""]+?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?\b)(?![/\\])",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex s_QuotedAssetRegex = new System.Text.RegularExpressions.Regex(
            @"(?:['""](?<file>(?:Assets|Packages)[/\\].+?\.[a-zA-Z0-9_\-]+)['""]|(?<file>(?:Assets|Packages)[/\\].+?\.[a-zA-Z0-9_\-]+)(?:[:\s,\r\n'""\)\.;!]|$))",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex s_StackTraceLocationRegex = new System.Text.RegularExpressions.Regex(
            @"(?:(?:\)\s+in|\(at|\bin(?=\s+(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages)[\\/])))\s+)(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex s_BareStackTraceLocationRegex = new System.Text.RegularExpressions.Regex(
            @"^\s*at\s+(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

        private static readonly System.Text.RegularExpressions.Regex s_IsErrorRegex = new System.Text.RegularExpressions.Regex(
            @"^(?!(?:warning|info)\b)(?:(?:[a-zA-Z]:[\\/])?[^:\r\n]+(?:\(\d+(?:,\s*\d+)?\)|:\d+(?::\d+)?)?:\s*)?error(?::|\s|$)",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static void CaptureOperationError(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // Publication diagnostics are infrastructure failures, not errors from asset import.
            if (message != null && message.StartsWith("UnityLeanMcp:", StringComparison.Ordinal)) return;
            var active = UnityCommandGate.ReadSnapshot();
            if (active == null || (active.Kind != OperationKinds.Refresh && active.Kind != OperationKinds.Recompile)) return;
            string formatted = FormatOperationDiagnostic(message, stackTrace, true);
            if (string.IsNullOrEmpty(formatted)) return;
            lock (s_DiagnosticsLock)
            {
                if (s_ScopedDiagnostics.operationId != active.OperationId)
                    s_ScopedDiagnostics = new ScopedDiagnostics { operationId = active.OperationId };
                if (!s_ScopedDiagnostics.messages.Contains(formatted))
                {
                    s_ScopedDiagnostics.messages.Add(formatted);
                    PersistScopedDiagnostics();
                }
            }
        }

        private static void PersistScopedDiagnostics()
        {
            if (s_MainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != s_MainThreadId) return;
            lock (s_DiagnosticsLock)
            {
                try
                {
                    SessionState.SetString(ScopedDiagnosticsKey, JsonUtility.ToJson(s_ScopedDiagnostics));
                }
                catch (Exception e)
                {
                    Debug.LogError($"UnityLeanMcp: Failed to persist scoped diagnostics: {e}");
                }
            }
        }


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
            s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
            UnityLeanMcpPaths.EnsureInitialized();
            UnityLeanMcpOperationStore.EnsureInitialized();
            EnsureLogReflectionCached();
            string scopedJson = SessionState.GetString(ScopedDiagnosticsKey, "");
            if (!string.IsNullOrEmpty(scopedJson))
            {
                try { s_ScopedDiagnostics = JsonUtility.FromJson<ScopedDiagnostics>(scopedJson) ?? new ScopedDiagnostics(); }
                catch { s_ScopedDiagnostics = new ScopedDiagnostics(); }
            }
            Application.logMessageReceivedThreaded -= CaptureOperationError;
            Application.logMessageReceivedThreaded += CaptureOperationError;
            AssemblyReloadEvents.beforeAssemblyReload -= PersistScopedDiagnostics;
            AssemblyReloadEvents.beforeAssemblyReload += PersistScopedDiagnostics;
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
                var active = UnityCommandGate.ReadSnapshot();
                if (active != null && s_ScopedDiagnostics.operationId == active.OperationId && s_ScopedDiagnostics.messages != null)
                    diagnostics.AddRange(s_ScopedDiagnostics.messages);
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

        internal static string NormalizeFilePath(string file)
        {
            if (string.IsNullOrEmpty(file)) return "";
            string normalized = file.Replace('\\', '/');
            try
            {
                string projectRoot = UnityLeanMcpPaths.ProjectRoot;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    projectRoot = projectRoot.Replace('\\', '/').TrimEnd('/');
                    if (normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = normalized.Substring(projectRoot.Length + 1);
                    }
                }
            }
            catch { }

            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            return normalized;
        }

        internal static string FormatCompilerDiagnostic(string rawMessage, string file, int line, int column, bool isError)
        {
            string msg = (rawMessage ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) return null;

            int newlineIdx = msg.IndexOfAny(new[] { '\r', '\n' });
            if (newlineIdx >= 0)
            {
                msg = msg.Substring(0, newlineIdx).Trim();
            }

            var leadingMatch = s_LeadingLocationRegex.Match(msg);
            if (leadingMatch.Success)
            {
                file = leadingMatch.Groups["file"].Value;
                if (leadingMatch.Groups["line"].Success) line = int.Parse(leadingMatch.Groups["line"].Value);
                if (leadingMatch.Groups["col"].Success) column = int.Parse(leadingMatch.Groups["col"].Value);
                msg = leadingMatch.Groups["rest"].Value;
            }

            string typeStr = isError ? "error" : "warning";
            if (msg.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(6).TrimStart();
            }
            else if (msg.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(6).TrimStart();
            }
            else if (msg.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(8).TrimStart();
            }
            else if (msg.StartsWith("warning ", StringComparison.OrdinalIgnoreCase))
            {
                msg = msg.Substring(8).TrimStart();
            }

            string normalizedFile = NormalizeFilePath(file);

            if (!string.IsNullOrEmpty(normalizedFile) && line > 0 && column > 0)
            {
                return $"{normalizedFile}({line},{column}): {typeStr} {msg}";
            }

            if (!string.IsNullOrEmpty(normalizedFile) && line > 0)
            {
                return $"{normalizedFile}({line}): {typeStr} {msg}";
            }

            if (!string.IsNullOrEmpty(normalizedFile))
            {
                return $"{normalizedFile}: {typeStr} {msg}";
            }

            return $"{typeStr}: {msg}";
        }

        internal static string FormatOperationDiagnostic(string rawMessage, string stackTrace, bool isError)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return null;
            string msg = rawMessage.Trim();
            int nlIdx = msg.IndexOfAny(new[] { '\r', '\n' });
            if (nlIdx >= 0)
            {
                msg = msg.Substring(0, nlIdx).Trim();
            }
            if (string.IsNullOrEmpty(msg)) return null;

            string file = "";
            int line = 0;
            int column = 0;

            var leadingMatch = s_LeadingLocationRegex.Match(msg);
            if (leadingMatch.Success)
            {
                file = leadingMatch.Groups["file"].Value;
                if (leadingMatch.Groups["line"].Success) line = int.Parse(leadingMatch.Groups["line"].Value);
                if (leadingMatch.Groups["col"].Success) column = int.Parse(leadingMatch.Groups["col"].Value);
                msg = leadingMatch.Groups["rest"].Value;
            }

            if (string.IsNullOrEmpty(file) || line == 0)
            {
                var shaderMatch = s_TrailingShaderLocationRegex.Match(rawMessage);
                if (shaderMatch.Success)
                {
                    if (string.IsNullOrEmpty(file))
                    {
                        file = shaderMatch.Groups["file"].Value;
                    }
                    if (shaderMatch.Groups["line"].Success) line = int.Parse(shaderMatch.Groups["line"].Value);
                    if (shaderMatch.Groups["col"].Success) column = int.Parse(shaderMatch.Groups["col"].Value);
                }
            }

            if (string.IsNullOrEmpty(file))
            {
                var quotedMatch = s_QuotedAssetRegex.Match(rawMessage);
                if (quotedMatch.Success)
                {
                    file = quotedMatch.Groups["file"].Value;
                }
            }

            if (string.IsNullOrEmpty(file) && !string.IsNullOrWhiteSpace(stackTrace))
            {
                foreach (System.Text.RegularExpressions.Match match in s_StackTraceLocationRegex.Matches(stackTrace))
                {
                    if (match.Success)
                    {
                        string candidateFile = match.Groups["file"].Value.Trim();
                        if (candidateFile.StartsWith("<", StringComparison.Ordinal)) continue;
                        if (match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out int parsedLine) && parsedLine > 0)
                        {
                            file = candidateFile;
                            line = parsedLine;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(file))
                {
                    foreach (System.Text.RegularExpressions.Match match in s_BareStackTraceLocationRegex.Matches(stackTrace))
                    {
                        if (match.Success)
                        {
                            string candidateFile = match.Groups["file"].Value.Trim();
                            if (candidateFile.StartsWith("<", StringComparison.Ordinal)) continue;
                            if (match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out int parsedLine) && parsedLine > 0)
                            {
                                file = candidateFile;
                                line = parsedLine;
                                break;
                            }
                        }
                    }
                }
            }

            return FormatCompilerDiagnostic(msg, file, line, column, isError);
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
            var snap = UnityCommandGate.ReadSnapshot();
            if (snap == null || (snap.Kind != OperationKinds.Refresh && snap.Kind != OperationKinds.Recompile))
            {
                s_ObservedOperationId = null;
                Interlocked.Exchange(ref s_SettledUpdateCount, 0);
                return;
            }

            if (snap.Status == OperationStatus.Interrupted || snap.Status == OperationStatus.Requested)
            {
                return;
            }

            if (UnityCommandGate.HasPendingResult(snap.OperationId)) return;

            if (s_ObservedOperationId != snap.OperationId)
            {
                s_ObservedOperationId = snap.OperationId;
                Interlocked.Exchange(ref s_SettledUpdateCount, 0);
            }

            if (s_RefreshPending || s_CompilationRequested || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Interlocked.Exchange(ref s_SettledUpdateCount, 0);
                UnityCommandGate.Update(snap.OperationId, OperationStatus.WaitingForUnity);
                return;
            }

            if (Interlocked.Increment(ref s_SettledUpdateCount) < 10)
            {
                return;
            }

            var diagnostics = WriteActiveErrorsToFile();
            bool hasErrors = EditorUtility.scriptCompilationFailed || diagnostics.Any(d => s_IsErrorRegex.IsMatch(d));

            var result = new UnityRefreshResult
            {
                operationId = snap.OperationId,
                success = !hasErrors,
                interrupted = false,
                message = diagnostics.Count > 0 ? string.Join("\n", diagnostics) : (hasErrors ? "Compilation failed" : "")
            };
            string json = JsonUtility.ToJson(result, true);
            UnityCommandGate.PublishResult(UnityCommandGate.ReadSnapshot()?.Kind, snap.OperationId,
                UnityLeanMcpPaths.GetRefreshResultFile(snap.OperationId), json);
            s_RefreshRequired = false;
            s_ObservedOperationId = null;
            Interlocked.Exchange(ref s_SettledUpdateCount, 0);
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
            UnityCommandGate.PublishResult(UnityCommandGate.ReadSnapshot()?.Kind, operationId,
                UnityLeanMcpPaths.GetRefreshResultFile(operationId), json);
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
                // Compiler diagnostics describe the current assembly state. A no-op
                // refresh after failed compilation has no new compiler callbacks;
                // retain that evidence until the assembly callback replaces it.
                // Only operation-scoped asset/runtime errors restart here.
                s_ScopedDiagnostics = new ScopedDiagnostics { operationId = UnityCommandGate.ReadSnapshot()?.OperationId };
                try
                {
                    if (s_MainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == s_MainThreadId)
                    {
                        SessionState.EraseString(ScopedDiagnosticsKey);
                    }
                }
                catch { }
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

        public static List<string> WriteActiveErrorsToFile()
        {
            var diagnostics = GetCapturedDiagnosticsSnapshot();
            try
            {
                PersistScopedDiagnostics();
                string errorsPath = UnityLeanMcpPaths.DiagnosticsFile;
                var seenDiagnostics = new HashSet<string>(diagnostics, StringComparer.Ordinal);

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
                            // Non-compiler errors are captured only while this operation owns
                            // the gate. Historical gameplay logs must never poison a refresh.
                            if (isCompileError || isCompileWarning)
                            {
                                string file = s_FileField != null ? (string) s_FileField.GetValue(currentEntry) : "";
                                int line = s_LineField != null ? (int) s_LineField.GetValue(currentEntry) : 0;
                                int column = s_ColumnField != null ? (int) s_ColumnField.GetValue(currentEntry) : 0;

                                bool isError = isCompileError;
                                string formatted = FormatCompilerDiagnostic(message, file, line, column, isError);
                                if (!string.IsNullOrEmpty(formatted) && seenDiagnostics.Add(formatted))
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

                if (diagnostics.Count > 0)
                {
                    WriteDiagnosticsFileAtomically(errorsPath, diagnostics);
                }
                else if (EditorUtility.scriptCompilationFailed)
                {
                    diagnostics.Add("UnityLeanMcp(1,1): error UC0001: Unity editor reports scriptCompilationFailed is true, but no compiler diagnostics were captured.");
                    WriteDiagnosticsFileAtomically(errorsPath, diagnostics);
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
            return diagnostics;
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
