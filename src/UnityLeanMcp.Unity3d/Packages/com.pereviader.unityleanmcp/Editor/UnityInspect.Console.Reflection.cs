#nullable disable
#pragma warning disable CS0618

using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
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
        private static volatile bool s_LogEntriesApiResolved;

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

    }
}
