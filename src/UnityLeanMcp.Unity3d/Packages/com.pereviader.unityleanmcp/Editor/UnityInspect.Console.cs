#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace UnityLeanMcp
{
    /// <summary>
    /// Severity filter flags for <see cref="Inspect.Console"/>.
    /// Matches the Unity Console's three severity buckets: Log, Warning, and Error.
    /// </summary>
    [Flags]
    public enum ConsoleSeverity
    {
        None = 0,
        Log = 1 << 0,
        Warning = 1 << 1,
        Error = 1 << 2,
        All = Log | Warning | Error
    }

    public static partial class Inspect
    {
        private const int DefaultConsoleMaxItems = 50;
        private const int MaxConsoleMessageLineCharacters = 500;
        private const int MaxConsoleStackTraceLines = 30;

        // UnityEditor.ConsoleWindow.Mode bit flags
        private const int ConsoleModeErrorMask =
            (1 << 0) |  // Error
            (1 << 1) |  // Assert
            (1 << 4) |  // Fatal
            (1 << 6) |  // AssetImportError
            (1 << 8) |  // ScriptingError
            (1 << 11) | // ScriptCompileError
            (1 << 17) | // ScriptingException
            (1 << 20);  // GraphCompileError

        private const int ConsoleModeWarningMask =
            (1 << 7) |  // AssetImportWarning
            (1 << 9) |  // ScriptingWarning
            (1 << 12);  // ScriptCompileWarning

        private static readonly Regex ConsoleTimestampRegex = new Regex(
            @"^\[(\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\s*",
            RegexOptions.CultureInvariant);

        private static readonly Regex UnityStackFrameRegex = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_.+<>`]*:[A-Za-z_][A-Za-z0-9_.+<>`]*\s*\(",
            RegexOptions.CultureInvariant);

        private static readonly Regex StackFrameLocationRegex = new Regex(
            @"\(at\s+([^():\r\n]+):(\d+)\)\s*$",
            RegexOptions.CultureInvariant);

        private sealed class LogEntriesApi
        {
            public readonly MethodInfo StartGettingEntries;
            public readonly MethodInfo EndGettingEntries;
            public readonly MethodInfo GetCount;
            public readonly MethodInfo GetEntryInternal;
            public readonly MethodInfo GetEntryCount;
            public readonly Type LogEntryType;
            public readonly MemberInfo MessageMember;
            public readonly MemberInfo FileMember;
            public readonly MemberInfo LineMember;
            public readonly MemberInfo ModeMember;

            public bool IsValid =>
                StartGettingEntries != null &&
                EndGettingEntries != null &&
                GetCount != null &&
                GetEntryInternal != null &&
                LogEntryType != null &&
                MessageMember != null &&
                ModeMember != null;

            public LogEntriesApi(
                MethodInfo startGettingEntries,
                MethodInfo endGettingEntries,
                MethodInfo getCount,
                MethodInfo getEntryInternal,
                MethodInfo getEntryCount,
                Type logEntryType,
                MemberInfo messageMember,
                MemberInfo fileMember,
                MemberInfo lineMember,
                MemberInfo modeMember)
            {
                StartGettingEntries = startGettingEntries;
                EndGettingEntries = endGettingEntries;
                GetCount = getCount;
                GetEntryInternal = getEntryInternal;
                GetEntryCount = getEntryCount;
                LogEntryType = logEntryType;
                MessageMember = messageMember;
                FileMember = fileMember;
                LineMember = lineMember;
                ModeMember = modeMember;
            }
        }

        private static readonly object s_LogEntriesApiLock = new object();
        private static LogEntriesApi s_LogEntriesApi;
        private static bool s_LogEntriesApiResolved;

        private readonly struct CollapsedConsoleKey : IEquatable<CollapsedConsoleKey>
        {
            public readonly ConsoleSeverity Severity;
            public readonly string Message;
            public readonly string StackTrace;
            public readonly string File;
            public readonly int Line;

            public CollapsedConsoleKey(
                ConsoleSeverity severity,
                string message,
                string stackTrace,
                string file,
                int line)
            {
                Severity = severity;
                Message = message ?? string.Empty;
                StackTrace = stackTrace ?? string.Empty;
                File = file ?? string.Empty;
                Line = line;
            }

            public bool Equals(CollapsedConsoleKey other)
            {
                return Severity == other.Severity &&
                       Line == other.Line &&
                       string.Equals(Message, other.Message, StringComparison.Ordinal) &&
                       string.Equals(StackTrace, other.StackTrace, StringComparison.Ordinal) &&
                       string.Equals(File, other.File, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is CollapsedConsoleKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)Severity;
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Message);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(StackTrace);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(File);
                    hash = (hash * 397) ^ Line;
                    return hash;
                }
            }
        }

        private sealed class CollapsedConsoleEntry
        {
            public readonly ConsoleSeverity Severity;
            public readonly string Message;
            public readonly string StackTrace;
            public readonly string File;
            public readonly int Line;
            public string Timestamp;
            public int Count;

            public CollapsedConsoleEntry(
                ConsoleSeverity severity,
                string message,
                string stackTrace,
                string file,
                int line,
                string timestamp,
                int count)
            {
                Severity = severity;
                Message = message;
                StackTrace = stackTrace;
                File = file;
                Line = line;
                Timestamp = timestamp;
                Count = count;
            }
        }

        /// <summary>
        /// Reads and formats entries currently in the Unity Console without mutating Console state.
        /// Identical entries are always collapsed with occurrence counts prefixed at the start of the line.
        /// </summary>
        public static string Console(
            ConsoleSeverity severity = ConsoleSeverity.All,
            string filter = null,
            int maxItems = DefaultConsoleMaxItems,
            bool includeStackTrace = false)
        {
            if (severity == ConsoleSeverity.None)
            {
                return "(No Console entries matched: severity is ConsoleSeverity.None)";
            }

            LogEntriesApi api = ResolveLogEntriesApi();
            if (api == null || !api.IsValid)
            {
                return "(Unity Console inspection unavailable: could not resolve UnityEditor.LogEntries API)";
            }

            maxItems = Math.Max(1, maxItems);
            Regex filterRegex = BuildConsoleFilterRegex(filter);

            var collapsedList = new LinkedList<CollapsedConsoleEntry>();
            var collapsedNodeByKey = new Dictionary<CollapsedConsoleKey, LinkedListNode<CollapsedConsoleEntry>>();

            int totalEntryCount = 0;
            int totalErrors = 0;
            int totalWarnings = 0;
            int totalLogs = 0;

            bool started = false;
            try
            {
                api.StartGettingEntries.Invoke(null, null);
                started = true;

                int rawCount = Convert.ToInt32(api.GetCount.Invoke(null, null), CultureInfo.InvariantCulture);
                if (rawCount > 0)
                {
                    object entryInstance = Activator.CreateInstance(api.LogEntryType);
                    object[] getEntryArgs = new object[2];
                    object[] getCountArgs = api.GetEntryCount != null ? new object[1] : null;

                    for (int i = 0; i < rawCount; i++)
                    {
                        getEntryArgs[0] = i;
                        getEntryArgs[1] = entryInstance;
                        object gotEntryObj = api.GetEntryInternal.Invoke(null, getEntryArgs);
                        if (gotEntryObj is bool gotEntry && !gotEntry)
                        {
                            continue;
                        }

                        // Refresh reference in case LogEntry is a value type or ref-like wrapper
                        object currentEntry = getEntryArgs[1] ?? entryInstance;
                        string rawMessage = ReadStringMember(api.MessageMember, currentEntry);
                        string rawFile = ReadStringMember(api.FileMember, currentEntry);
                        int line = ReadIntMember(api.LineMember, currentEntry);
                        int mode = ReadIntMember(api.ModeMember, currentEntry);

                        ExtractTimestampAndSplitMessage(
                            rawMessage,
                            out string timestamp,
                            out string primaryMessage,
                            out string stackTrace);

                        if (IsInternalHarnessLog(rawMessage, primaryMessage))
                        {
                            continue;
                        }

                        int count = 1;
                        if (api.GetEntryCount != null)
                        {
                            try
                            {
                                getCountArgs[0] = i;
                                object countObj = api.GetEntryCount.Invoke(null, getCountArgs);
                                if (countObj != null)
                                {
                                    int nativeCount = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);
                                    if (nativeCount >= 1)
                                    {
                                        count = nativeCount;
                                    }
                                }
                            }
                            catch
                            {
                                count = 1;
                            }
                        }

                        ConsoleSeverity entrySeverity = MapModeToSeverity(mode);
                        totalEntryCount += count;
                        switch (entrySeverity)
                        {
                            case ConsoleSeverity.Error:
                                totalErrors += count;
                                break;
                            case ConsoleSeverity.Warning:
                                totalWarnings += count;
                                break;
                            default:
                                totalLogs += count;
                                break;
                        }

                        if ((entrySeverity & severity) == 0)
                        {
                            continue;
                        }

                        string normalizedFile = NormalizeConsoleFilePath(rawFile);
                        if (string.IsNullOrEmpty(normalizedFile) && !string.IsNullOrEmpty(stackTrace))
                        {
                            TryExtractLocationFromStackTrace(stackTrace, out normalizedFile, out line);
                        }

                        if (!string.IsNullOrEmpty(filter) &&
                            !MatchesWildcardOrSubstring(primaryMessage, filter, filterRegex) &&
                            !MatchesWildcardOrSubstring(stackTrace, filter, filterRegex) &&
                            !MatchesWildcardOrSubstring(normalizedFile, filter, filterRegex))
                        {
                            continue;
                        }

                        var key = new CollapsedConsoleKey(entrySeverity, primaryMessage, stackTrace, normalizedFile, line);
                        if (collapsedNodeByKey.TryGetValue(key, out LinkedListNode<CollapsedConsoleEntry> existingNode))
                        {
                            CollapsedConsoleEntry existing = existingNode.Value;
                            existing.Count += count;
                            if (!string.IsNullOrEmpty(timestamp))
                            {
                                existing.Timestamp = timestamp;
                            }
                            collapsedList.Remove(existingNode);
                            collapsedList.AddLast(existingNode);
                        }
                        else
                        {
                            var node = collapsedList.AddLast(new CollapsedConsoleEntry(
                                entrySeverity,
                                primaryMessage,
                                stackTrace,
                                normalizedFile,
                                line,
                                timestamp,
                                count));
                            collapsedNodeByKey[key] = node;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException
                    : ex;
                return $"(Unity Console inspection failed: {root.GetType().Name}: {root.Message})";
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        api.EndGettingEntries.Invoke(null, null);
                    }
                    catch
                    {
                    }
                }
            }

            if (totalEntryCount == 0)
            {
                return "(No entries in Unity Console)";
            }

            if (collapsedList.Count == 0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "(No Console entries matched; {0} total in Console: {1} error, {2} warning, {3} log)",
                    totalEntryCount,
                    totalErrors,
                    totalWarnings,
                    totalLogs);
            }

            int startIndex = Math.Max(0, collapsedList.Count - maxItems);
            int shownCount = collapsedList.Count - startIndex;
            bool truncated = startIndex > 0;

            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "Console entries ({0} shown, {1} unique matched of {2} total; {3} error, {4} warning, {5} log):",
                shownCount,
                collapsedList.Count,
                totalEntryCount,
                totalErrors,
                totalWarnings,
                totalLogs);
            sb.AppendLine();

            int currentIndex = 0;
            foreach (CollapsedConsoleEntry entry in collapsedList)
            {
                if (currentIndex >= startIndex)
                {
                    AppendFormattedConsoleEntry(sb, entry, includeStackTrace);
                }
                currentIndex++;
            }

            if (truncated)
            {
                sb.AppendLine(ItemTruncationMarker);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static LogEntriesApi ResolveLogEntriesApi()
        {
            if (s_LogEntriesApiResolved)
            {
                return s_LogEntriesApi;
            }

            lock (s_LogEntriesApiLock)
            {
                if (s_LogEntriesApiResolved)
                {
                    return s_LogEntriesApi;
                }

                Assembly editorAssembly = typeof(Editor).Assembly;
                Type logEntriesType =
                    editorAssembly.GetType("UnityEditor.LogEntries") ??
                    editorAssembly.GetType("UnityEditorInternal.LogEntries");
                Type logEntryType =
                    editorAssembly.GetType("UnityEditor.LogEntry") ??
                    editorAssembly.GetType("UnityEditorInternal.LogEntry");

                if (logEntriesType != null && logEntryType != null)
                {
                    const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    MethodInfo startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", staticFlags, null, Type.EmptyTypes, null);
                    MethodInfo endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", staticFlags, null, Type.EmptyTypes, null);
                    MethodInfo getCount = logEntriesType.GetMethod("GetCount", staticFlags, null, Type.EmptyTypes, null);
                    MethodInfo getEntryInternal = logEntriesType.GetMethod("GetEntryInternal", staticFlags, null, new[] { typeof(int), logEntryType }, null);
                    MethodInfo getEntryCount = logEntriesType.GetMethod("GetEntryCount", staticFlags, null, new[] { typeof(int) }, null);

                    MemberInfo messageMember =
                        FindFieldOrProperty(logEntryType, "message", instanceFlags) ??
                        FindFieldOrProperty(logEntryType, "condition", instanceFlags);
                    MemberInfo fileMember = FindFieldOrProperty(logEntryType, "file", instanceFlags);
                    MemberInfo lineMember = FindFieldOrProperty(logEntryType, "line", instanceFlags);
                    MemberInfo modeMember = FindFieldOrProperty(logEntryType, "mode", instanceFlags);

                    var candidate = new LogEntriesApi(
                        startGettingEntries,
                        endGettingEntries,
                        getCount,
                        getEntryInternal,
                        getEntryCount,
                        logEntryType,
                        messageMember,
                        fileMember,
                        lineMember,
                        modeMember);

                    if (candidate.IsValid)
                    {
                        s_LogEntriesApi = candidate;
                    }
                }

                s_LogEntriesApiResolved = true;
                return s_LogEntriesApi;
            }
        }

        private static MemberInfo FindFieldOrProperty(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field != null)
            {
                return field;
            }

            return type.GetProperty(name, flags);
        }

        private static string ReadStringMember(MemberInfo member, object instance)
        {
            if (member == null || instance == null)
            {
                return string.Empty;
            }

            object value = member is FieldInfo field
                ? field.GetValue(instance)
                : ((PropertyInfo)member).GetValue(instance, null);

            return value as string ?? string.Empty;
        }

        private static int ReadIntMember(MemberInfo member, object instance)
        {
            if (member == null || instance == null)
            {
                return 0;
            }

            object value = member is FieldInfo field
                ? field.GetValue(instance)
                : ((PropertyInfo)member).GetValue(instance, null);

            return value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }

        private static ConsoleSeverity MapModeToSeverity(int mode)
        {
            if ((mode & ConsoleModeErrorMask) != 0)
            {
                return ConsoleSeverity.Error;
            }

            if ((mode & ConsoleModeWarningMask) != 0)
            {
                return ConsoleSeverity.Warning;
            }

            return ConsoleSeverity.Log;
        }

        private static bool IsInternalHarnessLog(string rawMessage, string primaryMessage)
        {
            if (!string.IsNullOrEmpty(rawMessage) &&
                rawMessage.StartsWith("UnityLeanMcp:", StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrEmpty(primaryMessage) &&
                   primaryMessage.StartsWith("UnityLeanMcp:", StringComparison.Ordinal);
        }

        private static void ExtractTimestampAndSplitMessage(
            string rawMessage,
            out string timestamp,
            out string primaryMessage,
            out string stackTrace)
        {
            timestamp = null;
            primaryMessage = string.Empty;
            stackTrace = string.Empty;

            if (string.IsNullOrEmpty(rawMessage))
            {
                return;
            }

            string normalized = rawMessage.Replace("\r\n", "\n").Replace('\r', '\n');
            Match tsMatch = ConsoleTimestampRegex.Match(normalized);
            if (tsMatch.Success)
            {
                timestamp = tsMatch.Groups[1].Value;
                normalized = normalized.Substring(tsMatch.Length);
            }

            string[] lines = normalized.Split('\n');
            int stackStart = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (LooksLikeStackTraceLine(lines[i]))
                {
                    stackStart = i;
                    break;
                }
            }

            if (stackStart < 0)
            {
                primaryMessage = normalized.TrimEnd();
                stackTrace = string.Empty;
                return;
            }

            var messageBuilder = new StringBuilder();
            for (int i = 0; i < stackStart; i++)
            {
                if (i > 0)
                {
                    messageBuilder.Append('\n');
                }
                messageBuilder.Append(lines[i]);
            }
            primaryMessage = messageBuilder.ToString().TrimEnd();

            var stackBuilder = new StringBuilder();
            for (int i = stackStart; i < lines.Length; i++)
            {
                if (i > stackStart)
                {
                    stackBuilder.Append('\n');
                }
                stackBuilder.Append(lines[i]);
            }
            stackTrace = stackBuilder.ToString().Trim();
        }

        private static Regex BuildConsoleFilterRegex(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || (!pattern.Contains('*') && !pattern.Contains('?')))
            {
                return null;
            }

            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool LooksLikeStackTraceLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("UnityEngine.Debug:", StringComparison.Ordinal) ||
                trimmed.StartsWith("UnityEngine.DebugLogHandler:", StringComparison.Ordinal) ||
                trimmed.StartsWith("UnityEngine.Logger:", StringComparison.Ordinal) ||
                trimmed.StartsWith("UnityEngine.Assertions.", StringComparison.Ordinal) ||
                trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.EndsWith(")", StringComparison.Ordinal) &&
                trimmed.IndexOf(" (at ", StringComparison.Ordinal) > 0)
            {
                return true;
            }

            return UnityStackFrameRegex.IsMatch(trimmed);
        }

        private static string NormalizeConsoleFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            string normalized = filePath.Trim().Replace('\\', '/');
            int assetsIndex = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0)
            {
                return normalized.Substring(assetsIndex + 1);
            }

            int packagesIndex = normalized.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase);
            if (packagesIndex >= 0)
            {
                return normalized.Substring(packagesIndex + 1);
            }

            return normalized;
        }

        private static void TryExtractLocationFromStackTrace(string stackTrace, out string file, out int line)
        {
            file = string.Empty;
            line = 0;

            if (string.IsNullOrEmpty(stackTrace))
            {
                return;
            }

            string[] frames = stackTrace.Split('\n');
            for (int i = 0; i < frames.Length; i++)
            {
                string trimmed = frames[i].Trim();
                if (IsNoisyDebugStackFrame(trimmed))
                {
                    continue;
                }

                Match match = StackFrameLocationRegex.Match(trimmed);
                if (match.Success)
                {
                    string candidateFile = NormalizeConsoleFilePath(match.Groups[1].Value);
                    if (!string.IsNullOrEmpty(candidateFile) &&
                        !candidateFile.StartsWith("<", StringComparison.Ordinal) &&
                        int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine) &&
                        parsedLine > 0)
                    {
                        file = candidateFile;
                        line = parsedLine;
                        return;
                    }
                }
            }
        }

        private static bool IsNoisyDebugStackFrame(string trimmedFrame)
        {
            return trimmedFrame.StartsWith("UnityEngine.Debug:Log", StringComparison.Ordinal) ||
                   trimmedFrame.StartsWith("UnityEngine.Debug:Assert", StringComparison.Ordinal) ||
                   trimmedFrame.StartsWith("UnityEngine.Logger:Log", StringComparison.Ordinal) ||
                   trimmedFrame.StartsWith("UnityEngine.DebugLogHandler:", StringComparison.Ordinal) ||
                   trimmedFrame.StartsWith("UnityEngine.Assertions.Assert:", StringComparison.Ordinal);
        }

        private static void AppendFormattedConsoleEntry(
            StringBuilder sb,
            CollapsedConsoleEntry entry,
            bool includeStackTrace)
        {
            if (entry.Count > 1)
            {
                sb.Append("(x");
                sb.Append(entry.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(") ");
            }

            if (!string.IsNullOrEmpty(entry.Timestamp))
            {
                sb.Append('[');
                sb.Append(entry.Timestamp);
                sb.Append("] ");
            }

            sb.Append('[');
            sb.Append(entry.Severity.ToString());
            sb.Append("] ");

            string compactMessage = FormatConsoleMessageLine(entry.Message);
            sb.Append(compactMessage);

            if (!string.IsNullOrEmpty(entry.File) &&
                compactMessage.IndexOf(entry.File, StringComparison.OrdinalIgnoreCase) < 0)
            {
                sb.Append(" (");
                sb.Append(entry.File);
                if (entry.Line > 0)
                {
                    sb.Append(':');
                    sb.Append(entry.Line.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(')');
            }

            sb.AppendLine();

            if (includeStackTrace && !string.IsNullOrEmpty(entry.StackTrace))
            {
                AppendFormattedStackTrace(sb, entry.StackTrace);
            }
        }

        private static string FormatConsoleMessageLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "(empty)";
            }

            string singleLine = message.Replace("\r", string.Empty).Replace("\n", "\\n");
            if (singleLine.Length > MaxConsoleMessageLineCharacters)
            {
                int prefixLength = MaxConsoleMessageLineCharacters;
                if (prefixLength > 0 && char.IsHighSurrogate(singleLine[prefixLength - 1]))
                {
                    prefixLength--;
                }
                singleLine = singleLine.Substring(0, prefixLength) + "...";
            }

            return singleLine;
        }

        private static void AppendFormattedStackTrace(StringBuilder sb, string stackTrace)
        {
            string[] lines = stackTrace.Split('\n');
            int start = 0;
            while (start < lines.Length && IsNoisyDebugStackFrame(lines[start].Trim()))
            {
                start++;
            }

            if (start >= lines.Length)
            {
                start = 0;
            }

            int emitted = 0;
            for (int i = start; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (emitted >= MaxConsoleStackTraceLines)
                {
                    sb.AppendLine("  ... (truncated: stack trace line limit reached)");
                    break;
                }

                sb.Append("  ");
                sb.AppendLine(trimmed);
                emitted++;
            }
        }
    }
}
