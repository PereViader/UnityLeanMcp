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
        private static readonly System.Text.RegularExpressions.Regex s_LeadingLocationRegex = new System.Text.RegularExpressions.Regex(
            @"^(?<file>(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages|[A-Za-z0-9_.\-@\+\(\)]+)[\\/])[^:\r\n'""]*?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?)?:\s*(?<rest>.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex s_IsErrorRegex = new System.Text.RegularExpressions.Regex(
            @"^(?!(?:warning|info)\b)(?:(?:[a-zA-Z]:[\\/])?[^:\r\n]+(?:\(\d+(?:,\s*\d+)?\)|:\d+(?::\d+)?)?:\s*)?error(?::|\s|$)",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal const int MaxScopedDiagnosticsCount = RefreshErrorLogCapture.MaxScopedDiagnosticsCount;

        internal static void CaptureOperationError(string message, string stackTrace, LogType type)
        {
            RefreshErrorLogCapture.RecordError(message, stackTrace, type);
        }

        private static volatile bool s_IsCompiling;
        private static volatile bool s_IsUpdating;
        private static volatile bool s_RefreshPending;
        private static volatile bool s_ScriptCompilationFailed;
        private static volatile bool s_AwaitingCompilationStart;
        private static volatile bool s_CompilationInProgress;
        private static volatile bool s_CompilationFinished;
        // Conservative latch for changes reported by Unity's project-change
        // notification. It starts set after a domain load because the client
        // cannot safely assume that an earlier process refreshed this domain.
        private static volatile bool s_RefreshRequired = true;
        private static string s_ObservedOperationId;
        private static int s_SettledUpdateCount;

        public static bool IsCompiling
        {
            get => s_IsCompiling;
            internal set => s_IsCompiling = value;
        }
        public static bool IsUpdating
        {
            get => s_IsUpdating;
            internal set => s_IsUpdating = value;
        }
        public static bool ScriptCompilationFailed
        {
            get => s_ScriptCompilationFailed;
            internal set => s_ScriptCompilationFailed = value;
        }
        public static bool RefreshRequired
        {
            get => s_RefreshRequired;
            internal set => s_RefreshRequired = value;
        }

        public static bool RefreshPending
        {
            get => s_RefreshPending;
            set => s_RefreshPending = value;
        }

        public static bool AwaitingCompilationStart
        {
            get => s_AwaitingCompilationStart;
            set
            {
                s_AwaitingCompilationStart = value;
                if (value)
                {
                    s_CompilationInProgress = false;
                    s_CompilationFinished = false;
                }
            }
        }

        public static bool CompilationInProgress
        {
            get => s_CompilationInProgress;
            set
            {
                s_CompilationInProgress = value;
                if (value)
                {
                    s_AwaitingCompilationStart = false;
                    s_CompilationFinished = false;
                }
            }
        }

        public static bool CompilationFinished
        {
            get => s_CompilationFinished;
            set
            {
                s_CompilationFinished = value;
                if (value)
                {
                    s_AwaitingCompilationStart = false;
                    s_CompilationInProgress = false;
                }
            }
        }

        public static bool CompilationRequested
        {
            get => s_AwaitingCompilationStart;
            set => AwaitingCompilationStart = value;
        }

        private static bool s_Initialized;
        private static readonly object s_InitLock = new object();

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
            RefreshErrorLogCapture.InitializeMainThread();
            UpdateCompilationState();
            var operation = UnityCommandGate.ReadSnapshot();
            bool resumingCompilation = operation != null &&
                (operation.Kind == OperationKinds.Refresh || operation.Kind == OperationKinds.Recompile);
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
            CompilationInProgress = true;
        }

        private static void OnCompilationFinished(object obj)
        {
            s_IsCompiling = false;
            CompilationFinished = true;
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
                var refreshErrors = RefreshErrorLogCapture.GetCapturedErrors(active?.OperationId);
                if (refreshErrors != null && refreshErrors.Count > 0)
                {
                    diagnostics.AddRange(refreshErrors);
                }
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
                if (leadingMatch.Groups["line"].Success)
                {
                    line = int.TryParse(leadingMatch.Groups["line"].Value, out int parsedLine) ? parsedLine : 0;
                }
                if (leadingMatch.Groups["col"].Success)
                {
                    column = int.TryParse(leadingMatch.Groups["col"].Value, out int parsedCol) ? parsedCol : 0;
                }
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
            return RefreshErrorLogCapture.FormatDiagnostic(rawMessage, stackTrace, isError);
        }

        public static void UpdateCompilationState()
        {
            s_IsCompiling = EditorApplication.isCompiling;
            s_IsUpdating = EditorApplication.isUpdating;
            s_ScriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (s_IsCompiling || EditorApplication.isCompiling)
            {
                CompilationInProgress = true;
            }
            else if (s_CompilationInProgress && !s_IsCompiling && !EditorApplication.isCompiling)
            {
                CompilationFinished = true;
                WriteActiveErrorsToFile();
            }

            ObserveOperationUntilSettled();
        }

        /// <summary>
        /// Completes refresh/recompile ownership only after Unity has reported a
        /// settled Editor state across consecutive idle update ticks. This observer
        /// resumes after a domain reload from the active operation restored by
        /// <see cref="UnityCommandGate"/> from <see cref="SessionState"/>.
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

            if (s_RefreshPending || s_CompilationInProgress || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Interlocked.Exchange(ref s_SettledUpdateCount, 0);
                if (snap.Status != OperationStatus.WaitingForUnity)
                {
                    UnityCommandGate.Update(snap.OperationId, OperationStatus.WaitingForUnity);
                }
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
            RefreshErrorLogCapture.StopActiveCapture();
            s_AwaitingCompilationStart = false;
            s_CompilationInProgress = false;
            s_CompilationFinished = false;
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

        internal static void ClearCapturedDiagnostics()
        {
            lock (s_DiagnosticsLock)
            {
                var active = UnityCommandGate.ReadSnapshot();
                RefreshErrorLogCapture.Clear(active?.OperationId);
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
                RefreshErrorLogCapture.Persist();
                string errorsPath = UnityLeanMcpPaths.DiagnosticsFile;

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
                UnityCommandGate.WriteAtomic(errorsPath, content, Guid.NewGuid().ToString("N"));
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
