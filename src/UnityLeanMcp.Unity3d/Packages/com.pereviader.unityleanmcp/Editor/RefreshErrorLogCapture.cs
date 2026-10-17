using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    public sealed class RefreshErrorLogCapture : IDisposable
    {
        public const int MaxScopedDiagnosticsCount = 200;
        private const string ScopedDiagnosticsKey = "UnityLeanMcp_RefreshDiagnostics";

        private static readonly Regex s_LeadingLocationRegex = new Regex(
            @"^(?<file>(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages|[A-Za-z0-9_.\-@\+\(\)]+)[\\/])[^:\r\n'""]*?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?)?:\s*(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex s_TrailingShaderLocationRegex = new Regex(
            @"\bat\s+(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n'""]+?)(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?\b)(?![/\\])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex s_QuotedAssetRegex = new Regex(
            @"(?:['""](?<file>(?:Assets|Packages)[/\\].+?\.[a-zA-Z0-9_\-]+)['""]|(?<file>(?:Assets|Packages)[/\\].+?\.[a-zA-Z0-9_\-]+)(?:[:\s,\r\n'""\)\.;!]|$))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex s_StackTraceLocationRegex = new Regex(
            @"(?:(?:\)\s+in|\(at|\bin(?=\s+(?:[a-zA-Z]:[\\/]|/|(?:Assets|Packages)[\\/])))\s+)(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex s_BareStackTraceLocationRegex = new Regex(
            @"^\s*at\s+(?<file>(?:[a-zA-Z]:[\\/]|/|[A-Za-z0-9_.\-@\+\s\(\)]+[\\/])[^:\r\n]+?):(?:line\s+)?(?<line>\d+)\)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly object s_Lock = new object();
        private static ScopedDiagnostics s_ScopedDiagnostics = new ScopedDiagnostics();
        private static int s_MainThreadId;
        private static RefreshErrorLogCapture s_ActiveCapture;
        private static bool s_ListenerRegistered;

        private readonly string m_OperationId;
        private bool m_Disposed;

        public string OperationId => m_OperationId;

        public RefreshErrorLogCapture(string operationId = null)
        {
            m_OperationId = operationId;
            EnsureInitialized();
            lock (s_Lock)
            {
                if (!string.IsNullOrEmpty(operationId) && s_ScopedDiagnostics.operationId != operationId)
                {
                    s_ScopedDiagnostics = new ScopedDiagnostics { operationId = operationId };
                    Persist();
                }
            }
            RegisterListener();
        }

        public static RefreshErrorLogCapture StartCapture(string operationId = null)
        {
            lock (s_Lock)
            {
                s_ActiveCapture?.Dispose();
                s_ActiveCapture = new RefreshErrorLogCapture(operationId);
                return s_ActiveCapture;
            }
        }

        public static void StopActiveCapture()
        {
            lock (s_Lock)
            {
                if (s_ActiveCapture != null)
                {
                    s_ActiveCapture.Dispose();
                    s_ActiveCapture = null;
                }
            }
        }

        public void StopCapture() => Dispose();

        public void Dispose()
        {
            lock (s_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                if (s_ActiveCapture == this)
                {
                    s_ActiveCapture = null;
                }
            }
        }

        internal static int MainThreadId
        {
            get => s_MainThreadId;
            set => s_MainThreadId = value;
        }

        internal static void ResetForTesting()
        {
            lock (s_Lock)
            {
                s_MainThreadId = 0;
                s_ActiveCapture = null;
                s_ScopedDiagnostics = new ScopedDiagnostics();
                s_ListenerRegistered = false;
            }
        }

        public static void InitializeMainThread()
        {
            s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureInitialized();
        }

        public static void EnsureInitialized()
        {

            lock (s_Lock)
            {
                if (s_MainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == s_MainThreadId)
                {
                    string scopedJson = SessionState.GetString(ScopedDiagnosticsKey, "");
                    if (!string.IsNullOrEmpty(scopedJson))
                    {
                        try
                        {
                            s_ScopedDiagnostics = JsonUtility.FromJson<ScopedDiagnostics>(scopedJson) ?? new ScopedDiagnostics();
                        }
                        catch
                        {
                            s_ScopedDiagnostics = new ScopedDiagnostics();
                        }
                    }
                }
            }
            RegisterListener();
        }

        private static void RegisterListener()
        {
            lock (s_Lock)
            {
                if (!s_ListenerRegistered)
                {
                    Application.logMessageReceivedThreaded -= OnLogReceived;
                    Application.logMessageReceivedThreaded += OnLogReceived;
                    AssemblyReloadEvents.beforeAssemblyReload -= Persist;
                    AssemblyReloadEvents.beforeAssemblyReload += Persist;
                    s_ListenerRegistered = true;
                }
            }
        }

        private static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            RecordError(condition, stackTrace, type);
        }

        public static void RecordError(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (message != null && message.StartsWith("UnityLeanMcp:", StringComparison.Ordinal)) return;

            var active = UnityCommandGate.ReadSnapshot();
            string expectedOpId = null;

            if (active != null && (active.Kind == OperationKinds.Refresh || active.Kind == OperationKinds.Recompile))
            {
                expectedOpId = active.OperationId;
            }
            else
            {
                lock (s_Lock)
                {
                    if (s_ActiveCapture != null && !string.IsNullOrEmpty(s_ActiveCapture.m_OperationId))
                    {
                        expectedOpId = s_ActiveCapture.m_OperationId;
                    }
                }
            }

            if (expectedOpId == null) return;

            string formatted = FormatDiagnostic(message, stackTrace, isError: true);
            if (string.IsNullOrEmpty(formatted)) return;

            lock (s_Lock)
            {
                if (s_ScopedDiagnostics.operationId != expectedOpId)
                {
                    s_ScopedDiagnostics = new ScopedDiagnostics { operationId = expectedOpId };
                }

                if (s_ScopedDiagnostics.messages.Count >= MaxScopedDiagnosticsCount)
                {
                    return;
                }

                if (!s_ScopedDiagnostics.messages.Contains(formatted))
                {
                    s_ScopedDiagnostics.messages.Add(formatted);
                    Persist();
                }
            }
        }

        public static void Persist()
        {
            if (s_MainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != s_MainThreadId) return;
            lock (s_Lock)
            {
                try
                {
                    SessionState.SetString(ScopedDiagnosticsKey, JsonUtility.ToJson(s_ScopedDiagnostics));
                }
                catch (Exception e)
                {
                    Debug.LogError($"UnityLeanMcp: Failed to persist refresh error log diagnostics: {e}");
                }
            }
        }

        public static void Clear(string operationId = null)
        {
            lock (s_Lock)
            {
                s_ActiveCapture = null;
                s_ScopedDiagnostics = new ScopedDiagnostics { operationId = operationId };
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

        public static List<string> GetCapturedErrors(string operationId = null)
        {
            lock (s_Lock)
            {
                if (operationId == null)
                {
                    var active = UnityCommandGate.ReadSnapshot();
                    operationId = active?.OperationId;
                }

                if (s_ScopedDiagnostics.messages != null &&
                    (string.IsNullOrEmpty(operationId) || string.IsNullOrEmpty(s_ScopedDiagnostics.operationId) || s_ScopedDiagnostics.operationId == operationId))
                {
                    return new List<string>(s_ScopedDiagnostics.messages);
                }

                return new List<string>();
            }
        }

        public static string FormatDiagnostic(string rawMessage, string stackTrace, bool isError = true)
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

            if (string.IsNullOrEmpty(file) || line == 0)
            {
                var shaderMatch = s_TrailingShaderLocationRegex.Match(rawMessage);
                if (shaderMatch.Success)
                {
                    if (string.IsNullOrEmpty(file))
                    {
                        file = shaderMatch.Groups["file"].Value;
                    }
                    if (shaderMatch.Groups["line"].Success)
                    {
                        line = int.TryParse(shaderMatch.Groups["line"].Value, out int parsedLine) ? parsedLine : 0;
                    }
                    if (shaderMatch.Groups["col"].Success)
                    {
                        column = int.TryParse(shaderMatch.Groups["col"].Value, out int parsedCol) ? parsedCol : 0;
                    }
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
                foreach (Match match in s_StackTraceLocationRegex.Matches(stackTrace))
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
                    foreach (Match match in s_BareStackTraceLocationRegex.Matches(stackTrace))
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

            return UnityLeanMcpCompilationTracker.FormatCompilerDiagnostic(msg, file, line, column, isError);
        }
    }
}
