#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
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
        private const string InternalHarnessLogPrefix = "UnityLeanMcp:";

        // UnityEditor.ConsoleWindow.Mode bit flags
        private const int ConsoleModeErrorMask =
            (1 << 0) |  // Error
            (1 << 1) |  // Assert
            (1 << 4) |  // Fatal
            (1 << 6) |  // AssetImportError
            (1 << 8) |  // ScriptingError
            (1 << 11) | // ScriptCompileError
            (1 << 17) | // ScriptingException
            (1 << 20) | // GraphCompileError
            (1 << 21);  // ScriptingAssertion

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

        private delegate void GetCountsByTypeDelegate(ref int errorCount, ref int warningCount, ref int logCount);

        private sealed class LogEntriesApi
        {
            public readonly Action StartGettingEntries;
            public readonly Action EndGettingEntries;
            public readonly Func<int> GetCount;
            public readonly Func<int, object, bool> GetEntryInternal;
            public readonly Func<int, int> GetEntryCount;
            public readonly GetCountsByTypeDelegate GetCountsByType;
            public readonly Func<string> GetFilteringText;
            public readonly Type LogEntryType;
            public readonly Func<object, string> ReadMessage;
            public readonly Func<object, string> ReadFile;
            public readonly Func<object, int> ReadLine;
            public readonly Func<object, int> ReadMode;
            public readonly bool UsesCompiledDelegates;

            public bool IsValid =>
                StartGettingEntries != null &&
                EndGettingEntries != null &&
                GetCount != null &&
                GetEntryInternal != null &&
                LogEntryType != null &&
                ReadMessage != null &&
                ReadMode != null;

            public LogEntriesApi(
                Action startGettingEntries,
                Action endGettingEntries,
                Func<int> getCount,
                Func<int, object, bool> getEntryInternal,
                Func<int, int> getEntryCount,
                GetCountsByTypeDelegate getCountsByType,
                Func<string> getFilteringText,
                Type logEntryType,
                Func<object, string> readMessage,
                Func<object, string> readFile,
                Func<object, int> readLine,
                Func<object, int> readMode,
                bool usesCompiledDelegates)
            {
                StartGettingEntries = startGettingEntries;
                EndGettingEntries = endGettingEntries;
                GetCount = getCount;
                GetEntryInternal = getEntryInternal;
                GetEntryCount = getEntryCount;
                GetCountsByType = getCountsByType;
                GetFilteringText = getFilteringText;
                LogEntryType = logEntryType;
                ReadMessage = readMessage;
                ReadFile = readFile;
                ReadLine = readLine;
                ReadMode = readMode;
                UsesCompiledDelegates = usesCompiledDelegates;
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
            int skippedInternalErrors = 0;
            int skippedInternalWarnings = 0;
            int skippedInternalLogs = 0;
            bool hasCountsByType = false;
            int unfilteredErrors = 0;
            int unfilteredWarnings = 0;
            int unfilteredLogs = 0;
            string filteringText = null;

            bool started = false;
            try
            {
                api.StartGettingEntries();
                started = true;

                int rawCount = api.GetCount();
                if (rawCount > 0)
                {
                    object entryInstance = Activator.CreateInstance(api.LogEntryType);

                    for (int i = 0; i < rawCount; i++)
                    {
                        if (!api.GetEntryInternal(i, entryInstance))
                        {
                            continue;
                        }

                        int mode = api.ReadMode(entryInstance);
                        ConsoleSeverity entrySeverity = MapModeToSeverity(mode);
                        int count = ReadEntryCount(api, i);
                        string rawMessage = api.ReadMessage(entryInstance);

                        if (IsInternalHarnessLog(rawMessage))
                        {
                            switch (entrySeverity)
                            {
                                case ConsoleSeverity.Error:
                                    skippedInternalErrors += count;
                                    break;
                                case ConsoleSeverity.Warning:
                                    skippedInternalWarnings += count;
                                    break;
                                default:
                                    skippedInternalLogs += count;
                                    break;
                            }
                            continue;
                        }

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

                        string rawFile = api.ReadFile(entryInstance);
                        int line = api.ReadLine(entryInstance);

                        ExtractTimestampAndSplitMessage(
                            rawMessage,
                            out string timestamp,
                            out string primaryMessage,
                            out string stackTrace);

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

                hasCountsByType = TryReadCountsByType(api, out unfilteredErrors, out unfilteredWarnings, out unfilteredLogs);
                filteringText = TryReadFilteringText(api);
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
                        api.EndGettingEntries();
                    }
                    catch
                    {
                    }
                }
            }

            string uiFilterNote = BuildConsoleUiFilterNote(
                hasCountsByType,
                unfilteredErrors,
                unfilteredWarnings,
                unfilteredLogs,
                totalErrors + skippedInternalErrors,
                totalWarnings + skippedInternalWarnings,
                totalLogs + skippedInternalLogs,
                filteringText);

            if (totalEntryCount == 0)
            {
                return string.IsNullOrEmpty(uiFilterNote)
                    ? "(No entries in Unity Console)"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "(No visible entries in Unity Console; {0})",
                        uiFilterNote);
            }

            if (collapsedList.Count == 0)
            {
                if (string.IsNullOrEmpty(uiFilterNote))
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "(No Console entries matched; {0} total in Console: {1} error, {2} warning, {3} log)",
                        totalEntryCount,
                        totalErrors,
                        totalWarnings,
                        totalLogs);
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "(No Console entries matched; {0} total in Console: {1} error, {2} warning, {3} log; {4})",
                    totalEntryCount,
                    totalErrors,
                    totalWarnings,
                    totalLogs,
                    uiFilterNote);
            }

            int startIndex = Math.Max(0, collapsedList.Count - maxItems);
            int shownCount = collapsedList.Count - startIndex;
            bool truncated = startIndex > 0;

            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "Console entries ({0} shown, {1} unique matched of {2} total; {3} error, {4} warning, {5} log)",
                shownCount,
                collapsedList.Count,
                totalEntryCount,
                totalErrors,
                totalWarnings,
                totalLogs);
            if (!string.IsNullOrEmpty(uiFilterNote))
            {
                sb.Append(" [");
                sb.Append(uiFilterNote);
                sb.Append(']');
            }
            sb.AppendLine(":");

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
                    Type intByRef = typeof(int).MakeByRefType();
                    MethodInfo getCountsByType = logEntriesType.GetMethod("GetCountsByType", staticFlags, null, new[] { intByRef, intByRef, intByRef }, null);
                    MethodInfo getFilteringText = logEntriesType.GetMethod("GetFilteringText", staticFlags, null, Type.EmptyTypes, null);

                    MemberInfo messageMember =
                        FindFieldOrProperty(logEntryType, "message", instanceFlags) ??
                        FindFieldOrProperty(logEntryType, "condition", instanceFlags);
                    MemberInfo fileMember = FindFieldOrProperty(logEntryType, "file", instanceFlags);
                    MemberInfo lineMember = FindFieldOrProperty(logEntryType, "line", instanceFlags);
                    MemberInfo modeMember = FindFieldOrProperty(logEntryType, "mode", instanceFlags);

                    if (startGettingEntries != null &&
                        endGettingEntries != null &&
                        getCount != null &&
                        getEntryInternal != null &&
                        messageMember != null &&
                        modeMember != null)
                    {
                        bool allCompiled = true;

                        Action startAction = CompileVoidCall(startGettingEntries, ref allCompiled);
                        Action endAction = CompileVoidCall(endGettingEntries, ref allCompiled);
                        Func<int> getCountFunc = CompileIntCall(getCount, ref allCompiled);
                        Func<int, object, bool> getEntryFunc = CompileGetEntryInternal(getEntryInternal, logEntryType, ref allCompiled);
                        Func<int, int> getEntryCountFunc = CompileIntByIntCall(getEntryCount, ref allCompiled);
                        GetCountsByTypeDelegate getCountsByTypeDel = CompileGetCountsByType(getCountsByType, ref allCompiled);
                        Func<string> getFilteringTextFunc = CompileStringCall(getFilteringText, ref allCompiled);
                        Func<object, string> readMessageFunc = CompileStringMemberReader(messageMember, logEntryType, ref allCompiled);
                        Func<object, string> readFileFunc = CompileStringMemberReader(fileMember, logEntryType, ref allCompiled);
                        Func<object, int> readLineFunc = CompileIntMemberReader(lineMember, logEntryType, ref allCompiled);
                        Func<object, int> readModeFunc = CompileIntMemberReader(modeMember, logEntryType, ref allCompiled);

                        var candidate = new LogEntriesApi(
                            startAction,
                            endAction,
                            getCountFunc,
                            getEntryFunc,
                            getEntryCountFunc,
                            getCountsByTypeDel,
                            getFilteringTextFunc,
                            logEntryType,
                            readMessageFunc,
                            readFileFunc,
                            readLineFunc,
                            readModeFunc,
                            allCompiled);

                        if (candidate.IsValid)
                        {
                            s_LogEntriesApi = candidate;
                        }
                    }
                }

                s_LogEntriesApiResolved = true;
                return s_LogEntriesApi;
            }
        }

        internal static bool AreLogEntriesDelegatesCompiledForTesting()
        {
            LogEntriesApi api = ResolveLogEntriesApi();
            return api != null && api.IsValid && api.UsesCompiledDelegates;
        }

        private static int ReadEntryCount(LogEntriesApi api, int row)
        {
            if (api.GetEntryCount == null)
            {
                return 1;
            }

            try
            {
                int nativeCount = api.GetEntryCount(row);
                if (nativeCount >= 1)
                {
                    return nativeCount;
                }
            }
            catch
            {
            }

            return 1;
        }

        private static bool TryReadCountsByType(
            LogEntriesApi api,
            out int errorCount,
            out int warningCount,
            out int logCount)
        {
            errorCount = 0;
            warningCount = 0;
            logCount = 0;
            if (api.GetCountsByType == null)
            {
                return false;
            }

            try
            {
                api.GetCountsByType(ref errorCount, ref warningCount, ref logCount);
                return true;
            }
            catch
            {
                errorCount = 0;
                warningCount = 0;
                logCount = 0;
                return false;
            }
        }

        private static string TryReadFilteringText(LogEntriesApi api)
        {
            if (api.GetFilteringText == null)
            {
                return null;
            }

            try
            {
                return api.GetFilteringText();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildConsoleUiFilterNote(
            bool hasCountsByType,
            int unfilteredErrors,
            int unfilteredWarnings,
            int unfilteredLogs,
            int visibleErrors,
            int visibleWarnings,
            int visibleLogs,
            string filteringText)
        {
            string sanitizedFilter = SanitizeConsoleUiFilterText(filteringText);

            if (hasCountsByType)
            {
                int hiddenErrors = Math.Max(0, unfilteredErrors - visibleErrors);
                int hiddenWarnings = Math.Max(0, unfilteredWarnings - visibleWarnings);
                int hiddenLogs = Math.Max(0, unfilteredLogs - visibleLogs);
                int hiddenTotal = hiddenErrors + hiddenWarnings + hiddenLogs;
                if (hiddenTotal <= 0)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(sanitizedFilter))
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "Console UI filters hiding {0}: {1} error, {2} warning, {3} log; search: \"{4}\"",
                        hiddenTotal,
                        hiddenErrors,
                        hiddenWarnings,
                        hiddenLogs,
                        sanitizedFilter);
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Console UI filters hiding {0}: {1} error, {2} warning, {3} log",
                    hiddenTotal,
                    hiddenErrors,
                    hiddenWarnings,
                    hiddenLogs);
            }

            if (!string.IsNullOrEmpty(sanitizedFilter))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Console UI search filter active: \"{0}\"",
                    sanitizedFilter);
            }

            return null;
        }

        private static string SanitizeConsoleUiFilterText(string filteringText)
        {
            if (string.IsNullOrEmpty(filteringText))
            {
                return string.Empty;
            }

            string singleLine = filteringText.Replace("\r", " ").Replace("\n", " ");
            const int maxFilterLength = 40;
            if (singleLine.Length > maxFilterLength)
            {
                int prefixLength = maxFilterLength;
                if (prefixLength > 0 && char.IsHighSurrogate(singleLine[prefixLength - 1]))
                {
                    prefixLength--;
                }
                singleLine = singleLine.Substring(0, prefixLength) + "...";
            }

            return singleLine;
        }

        private static Action CompileVoidCall(MethodInfo method, ref bool allCompiled)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                if (method.GetParameters().Length == 0)
                {
                    MethodCallExpression call = Expression.Call(method);
                    Expression body = method.ReturnType == typeof(void)
                        ? (Expression)call
                        : Expression.Block(call, Expression.Empty());
                    return Expression.Lambda<Action>(body).Compile();
                }
            }
            catch
            {
            }

            allCompiled = false;
            return () => method.Invoke(null, null);
        }

        private static Func<int> CompileIntCall(MethodInfo method, ref bool allCompiled)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                if (method.GetParameters().Length == 0)
                {
                    MethodCallExpression call = Expression.Call(method);
                    if (method.ReturnType == typeof(int))
                    {
                        return Expression.Lambda<Func<int>>(call).Compile();
                    }

                    if (IsConvertibleToInt(method.ReturnType))
                    {
                        return Expression.Lambda<Func<int>>(Expression.Convert(call, typeof(int))).Compile();
                    }
                }
            }
            catch
            {
            }

            allCompiled = false;
            return () => Convert.ToInt32(method.Invoke(null, null), CultureInfo.InvariantCulture);
        }

        private static Func<int, object, bool> CompileGetEntryInternal(
            MethodInfo method,
            Type logEntryType,
            ref bool allCompiled)
        {
            if (method == null || logEntryType == null)
            {
                return null;
            }

            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (!logEntryType.IsValueType &&
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(int) &&
                    parameters[1].ParameterType == logEntryType)
                {
                    ParameterExpression rowParam = Expression.Parameter(typeof(int), "row");
                    ParameterExpression entryParam = Expression.Parameter(typeof(object), "entry");
                    MethodCallExpression call = Expression.Call(
                        method,
                        rowParam,
                        Expression.Convert(entryParam, logEntryType));

                    if (method.ReturnType == typeof(bool))
                    {
                        return Expression.Lambda<Func<int, object, bool>>(call, rowParam, entryParam).Compile();
                    }

                    if (method.ReturnType == typeof(void))
                    {
                        BlockExpression block = Expression.Block(call, Expression.Constant(true));
                        return Expression.Lambda<Func<int, object, bool>>(block, rowParam, entryParam).Compile();
                    }
                }
            }
            catch
            {
            }

            allCompiled = false;
            return (row, entry) =>
            {
                object result = method.Invoke(null, new object[] { row, entry });
                return !(result is bool gotEntry && !gotEntry);
            };
        }

        private static Func<int, int> CompileIntByIntCall(MethodInfo method, ref bool allCompiled)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    ParameterExpression rowParam = Expression.Parameter(typeof(int), "row");
                    MethodCallExpression call = Expression.Call(method, rowParam);
                    if (method.ReturnType == typeof(int))
                    {
                        return Expression.Lambda<Func<int, int>>(call, rowParam).Compile();
                    }

                    if (IsConvertibleToInt(method.ReturnType))
                    {
                        return Expression.Lambda<Func<int, int>>(Expression.Convert(call, typeof(int)), rowParam).Compile();
                    }
                }
            }
            catch
            {
            }

            allCompiled = false;
            return row =>
            {
                object countObj = method.Invoke(null, new object[] { row });
                return countObj != null ? Convert.ToInt32(countObj, CultureInfo.InvariantCulture) : 1;
            };
        }

        private static GetCountsByTypeDelegate CompileGetCountsByType(MethodInfo method, ref bool allCompiled)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                Type intByRef = typeof(int).MakeByRefType();
                if (parameters.Length == 3 &&
                    parameters[0].ParameterType == intByRef &&
                    parameters[1].ParameterType == intByRef &&
                    parameters[2].ParameterType == intByRef &&
                    method.ReturnType == typeof(void))
                {
                    ParameterExpression errParam = Expression.Parameter(intByRef, "errorCount");
                    ParameterExpression warnParam = Expression.Parameter(intByRef, "warningCount");
                    ParameterExpression logParam = Expression.Parameter(intByRef, "logCount");
                    MethodCallExpression call = Expression.Call(method, errParam, warnParam, logParam);
                    return Expression.Lambda<GetCountsByTypeDelegate>(call, errParam, warnParam, logParam).Compile();
                }
            }
            catch
            {
            }

            allCompiled = false;
            return (ref int errorCount, ref int warningCount, ref int logCount) =>
            {
                object[] args = new object[] { 0, 0, 0 };
                method.Invoke(null, args);
                errorCount = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
                warningCount = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
                logCount = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
            };
        }

        private static Func<string> CompileStringCall(MethodInfo method, ref bool allCompiled)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                if (method.GetParameters().Length == 0 && method.ReturnType == typeof(string))
                {
                    return Expression.Lambda<Func<string>>(Expression.Call(method)).Compile();
                }
            }
            catch
            {
            }

            allCompiled = false;
            return () => method.Invoke(null, null) as string;
        }

        private static Func<object, string> CompileStringMemberReader(
            MemberInfo member,
            Type instanceType,
            ref bool allCompiled)
        {
            if (member == null)
            {
                return _ => string.Empty;
            }

            try
            {
                ParameterExpression instanceParam = Expression.Parameter(typeof(object), "instance");
                Expression typedInstance = instanceType.IsValueType
                    ? (Expression)Expression.Unbox(instanceParam, instanceType)
                    : Expression.Convert(instanceParam, instanceType);
                Expression memberAccess = CreateMemberAccess(typedInstance, member);
                if (memberAccess != null && memberAccess.Type == typeof(string))
                {
                    Expression body = Expression.Condition(
                        Expression.Equal(instanceParam, Expression.Constant(null, typeof(object))),
                        Expression.Constant(string.Empty),
                        Expression.Coalesce(memberAccess, Expression.Constant(string.Empty)));
                    return Expression.Lambda<Func<object, string>>(body, instanceParam).Compile();
                }
            }
            catch
            {
            }

            allCompiled = false;
            return instance => ReadStringMember(member, instance);
        }

        private static Func<object, int> CompileIntMemberReader(
            MemberInfo member,
            Type instanceType,
            ref bool allCompiled)
        {
            if (member == null)
            {
                return _ => 0;
            }

            try
            {
                ParameterExpression instanceParam = Expression.Parameter(typeof(object), "instance");
                Expression typedInstance = instanceType.IsValueType
                    ? (Expression)Expression.Unbox(instanceParam, instanceType)
                    : Expression.Convert(instanceParam, instanceType);
                Expression memberAccess = CreateMemberAccess(typedInstance, member);
                if (memberAccess != null)
                {
                    Expression intExpr = null;
                    if (memberAccess.Type == typeof(int))
                    {
                        intExpr = memberAccess;
                    }
                    else if (IsConvertibleToInt(memberAccess.Type))
                    {
                        intExpr = Expression.Convert(memberAccess, typeof(int));
                    }

                    if (intExpr != null)
                    {
                        Expression body = Expression.Condition(
                            Expression.Equal(instanceParam, Expression.Constant(null, typeof(object))),
                            Expression.Constant(0),
                            intExpr);
                        return Expression.Lambda<Func<object, int>>(body, instanceParam).Compile();
                    }
                }
            }
            catch
            {
            }

            allCompiled = false;
            return instance => ReadIntMember(member, instance);
        }

        private static Expression CreateMemberAccess(Expression instance, MemberInfo member)
        {
            if (member is FieldInfo field)
            {
                return Expression.Field(instance, field);
            }

            if (member is PropertyInfo property && property.GetIndexParameters().Length == 0)
            {
                MethodInfo getter = property.GetGetMethod(true);
                if (getter != null)
                {
                    return Expression.Property(instance, property);
                }
            }

            return null;
        }

        private static bool IsConvertibleToInt(Type type)
        {
            return type == typeof(int) ||
                   type == typeof(uint) ||
                   type == typeof(short) ||
                   type == typeof(ushort) ||
                   type == typeof(byte) ||
                   type == typeof(sbyte) ||
                   type.IsEnum;
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

        private static bool IsInternalHarnessLog(string rawMessage)
        {
            if (string.IsNullOrEmpty(rawMessage))
            {
                return false;
            }

            if (rawMessage.StartsWith(InternalHarnessLogPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (rawMessage[0] == '[')
            {
                Match tsMatch = ConsoleTimestampRegex.Match(rawMessage);
                if (tsMatch.Success &&
                    rawMessage.Length >= tsMatch.Length + InternalHarnessLogPrefix.Length &&
                    string.CompareOrdinal(
                        rawMessage,
                        tsMatch.Length,
                        InternalHarnessLogPrefix,
                        0,
                        InternalHarnessLogPrefix.Length) == 0)
                {
                    return true;
                }
            }

            return false;
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
