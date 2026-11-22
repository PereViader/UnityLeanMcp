using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class EvalHandler : ICommandHandler
    {
        private readonly struct CompiledSnippetEntry
        {
            public readonly MethodInfo ExecuteMethod;
            public readonly bool IsVoidStatement;

            public CompiledSnippetEntry(MethodInfo executeMethod, bool isVoidStatement)
            {
                ExecuteMethod = executeMethod;
                IsVoidStatement = isVoidStatement;
            }
        }

        private static readonly ConcurrentDictionary<string, CompiledSnippetEntry> s_SnippetCache =
            new ConcurrentDictionary<string, CompiledSnippetEntry>(StringComparer.Ordinal);

        internal static int CachedSnippetCount => s_SnippetCache.Count;

        internal static void ClearCache()
        {
            s_SnippetCache.Clear();
        }

        public static bool CancelActiveEval(string operationId)
        {
            return OperationExecutionEngine.TryCancel(operationId);
        }

        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => true;

        public void Handle(string payload, StreamWriter writer)
        {
            string[] requestParts = (payload ?? "").Split(new[] { ' ' }, 2);
            if (requestParts.Length < 2 || string.IsNullOrWhiteSpace(requestParts[1]))
            {
                writer.WriteLine("ERROR: Missing operation id or code snippet");
                return;
            }

            string operationId = requestParts[0];
            if (!UnityLeanMcpPaths.IsValidOperationId(operationId))
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }

            if (File.Exists(UnityLeanMcpPaths.GetEvalResultFile(operationId)))
            {
                writer.WriteLine("RUNNING");
                return;
            }

            if (UnityCommandGate.IsOwnedBy(operationId, OperationKinds.Eval))
            {
                try
                {
                    writer.WriteLine("RUNNING");
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                }
                return;
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                writer.WriteLine("FAILURE Compilation failed");
                return;
            }

            if (UnityLeanMcpCompilationTracker.IsCompiling ||
                UnityLeanMcpCompilationTracker.RefreshPending ||
                (UnityLeanMcpCompilationTracker.RefreshRequired && !UnityLeanMcpServer.IsPlayModeActive))
            {
                writer.WriteLine("BUSY compile");
                return;
            }

            string rawCode = ProtocolCodec.UnescapeLine(requestParts[1].Trim());
            var begin = UnityCommandGate.TryBegin(OperationKinds.Eval, operationId, OperationStatus.Compiling, out var busyReason);
            if (begin == BeginGateResult.Cancelled)
            {
                writer.WriteLine("INTERRUPTION Operation was cancelled before admission.");
                return;
            }
            if (begin == BeginGateResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }
            if (begin == BeginGateResult.Busy)
            {
                writer.WriteLine($"BUSY {busyReason}");
                return;
            }
            if (begin == BeginGateResult.AlreadyStarted)
            {
                try
                {
                    writer.WriteLine("RUNNING");
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                }
                return;
            }

            string resultFilePath = UnityLeanMcpPaths.GetEvalResultFile(operationId);
            var cts = OperationExecutionEngine.RegisterActiveOperation(operationId, isCancelable: true);

            bool runningSent = false;
            try
            {
                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, resultFilePath, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine($"INTERRUPTION {ProtocolCodec.EscapeLine("Evaluation was canceled.")}");
                    return;
                }

                var status = RoslynCompilerHelper.GetSupportStatus();
                if (!status.IsSupported)
                {
                    string msg = "The 'eval' command is not supported on this Unity version (" + status.UnsupportedReason + ")";
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, resultFilePath, false, msg, 0, null, null);
                    writer.WriteLine($"FAILURE {ProtocolCodec.EscapeLine(msg)}");
                    return;
                }

                // Resolve cached or newly compiled snippet runner method
                MethodInfo execMethod;
                bool isVoidStatement;
                List<string> errors;

                if (!TryGetOrCompileSnippet(rawCode, out execMethod, out isVoidStatement, out errors) || execMethod == null)
                {
                    string combinedErrors = string.Join("\n", errors);
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, resultFilePath, false, combinedErrors, 0, null, null);
                    writer.WriteLine($"FAILURE {ProtocolCodec.EscapeLine(combinedErrors)}");
                    return;
                }

                if (cts.IsCancellationRequested)
                {
                    OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, resultFilePath, false, "Evaluation was canceled.", 0, null, null, interrupted: true);
                    writer.WriteLine($"INTERRUPTION {ProtocolCodec.EscapeLine("Evaluation was canceled.")}");
                    return;
                }

                // Acknowledge execution start to client immediately
                try
                {
                    writer.WriteLine("RUNNING");
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                }
                runningSent = true;

                // Execute compiled assembly under async harness
                UnityCommandGate.Update(operationId, OperationStatus.Executing);

                OperationExecutionEngine.Execute(
                    operationId: operationId,
                    operationKind: OperationKinds.Eval,
                    resultFilePath: resultFilePath,
                    isVoid: isVoidStatement,
                    invoker: ct => execMethod.Invoke(null, new object[] { ct }),
                    canCancel: true
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Unhandled exception during Eval: {ex}");
                OperationExecutionEngine.FinishOperation(operationId, OperationKinds.Eval, resultFilePath, false, ex.ToString(), 0, null, null);
                if (!runningSent)
                {
                    try
                    {
                        writer.WriteLine($"FAILURE {ProtocolCodec.EscapeLine(ex.Message)}");
                        writer.Flush();
                    }
                    catch { }
                }
            }
        }

        internal static bool TryGetOrCompileSnippet(string rawCode, out MethodInfo execMethod, out bool isVoidStatement, out List<string> errors)
        {
            string cacheKey = rawCode ?? "";
            if (s_SnippetCache.TryGetValue(cacheKey, out var cached) && cached.ExecuteMethod != null)
            {
                execMethod = cached.ExecuteMethod;
                isVoidStatement = cached.IsVoidStatement;
                errors = new List<string>();
                return true;
            }

            execMethod = null;
            byte[] assemblyBytes;
            if (!TryCompileSnippet(cacheKey, out assemblyBytes, out isVoidStatement, out errors) || assemblyBytes == null)
            {
                return false;
            }

            var asm = Assembly.Load(assemblyBytes);
            var runnerType = asm.GetType("__UnityLeanMcpEvalRunner");
            if (runnerType == null)
            {
                throw new Exception("Evaluation runner type could not be loaded from dynamic assembly.");
            }

            execMethod = runnerType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (execMethod == null)
            {
                throw new Exception("Evaluation runner execute method not found.");
            }

            s_SnippetCache[cacheKey] = new CompiledSnippetEntry(execMethod, isVoidStatement);
            return true;
        }

        private static bool TryCompileSnippet(string rawCode, out byte[] assemblyBytes, out bool isVoidStatement, out List<string> errors)
        {
            assemblyBytes = null;
            errors = new List<string>();

            RoslynCompilerHelper.ExtractUsingDirectives(rawCode, out var usings, out var methodBody);

            bool hasValueReturn = RoslynCompilerHelper.HasTopLevelValueReturn(methodBody, alreadyCleaned: true);
            isVoidStatement = !hasValueReturn;

            string source = BuildSource(methodBody, isVoidStatement, usings);
            return RoslynCompilerHelper.CompileAndEmit(source, out assemblyBytes, out errors);
        }

        internal static readonly string[] DefaultUsingNamespaces =
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "UnityEngine",
            "UnityEditor",
            "UnityLeanMcp"
        };

        internal static string BuildSource(string methodBody, bool isVoid, List<string> userUsings = null)
        {
            var sb = new StringBuilder();
            var importedNamespaces = new HashSet<string>(StringComparer.Ordinal);

            if (userUsings != null && userUsings.Count > 0)
            {
                foreach (var u in userUsings)
                {
                    if (string.IsNullOrWhiteSpace(u))
                    {
                        continue;
                    }

                    sb.AppendLine(u);
                    if (TryExtractImportedNamespace(u, out var ns))
                    {
                        importedNamespaces.Add(ns);
                    }
                }
            }

            foreach (var defaultNs in DefaultUsingNamespaces)
            {
                if (!importedNamespaces.Contains(defaultNs))
                {
                    sb.Append("using ");
                    sb.Append(defaultNs);
                    sb.AppendLine(";");
                }
            }

            sb.AppendLine();
            sb.AppendLine("public static class __UnityLeanMcpEvalRunner");
            sb.AppendLine("{");
            if (isVoid)
            {
                sb.AppendLine("    public static async System.Threading.Tasks.Task Execute(System.Threading.CancellationToken cancellationToken)");
            }
            else
            {
                sb.AppendLine("    public static async System.Threading.Tasks.Task<object> Execute(System.Threading.CancellationToken cancellationToken)");
            }
            sb.AppendLine("    {");
            sb.AppendLine("#line 1 \"eval\"");
            sb.AppendLine(methodBody);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        internal static bool TryExtractImportedNamespace(string directive, out string normalizedNamespace)
        {
            normalizedNamespace = null;
            if (string.IsNullOrWhiteSpace(directive))
            {
                return false;
            }

            string cleaned = StripDirectiveComments(directive).Trim();
            if (cleaned.Length == 0 || cleaned[cleaned.Length - 1] != ';')
            {
                return false;
            }

            cleaned = cleaned.Substring(0, cleaned.Length - 1).Trim();
            if (cleaned.Length == 0 || cleaned.IndexOf('=') >= 0)
            {
                return false;
            }

            TryConsumeLeadingKeyword(cleaned, "global", out cleaned);
            if (!TryConsumeLeadingKeyword(cleaned, "using", out cleaned))
            {
                return false;
            }

            if (TryConsumeLeadingKeyword(cleaned, "static", out _))
            {
                return false;
            }

            var compactBuilder = new StringBuilder(cleaned.Length);
            for (int i = 0; i < cleaned.Length; i++)
            {
                if (!char.IsWhiteSpace(cleaned[i]))
                {
                    compactBuilder.Append(cleaned[i]);
                }
            }

            string ns = compactBuilder.ToString();
            if (ns.StartsWith("global::", StringComparison.Ordinal))
            {
                ns = ns.Substring("global::".Length);
            }

            if (ns.IndexOf('@') >= 0)
            {
                string[] segments = ns.Split('.');
                for (int i = 0; i < segments.Length; i++)
                {
                    if (segments[i].Length > 1 && segments[i][0] == '@')
                    {
                        segments[i] = segments[i].Substring(1);
                    }
                }

                ns = string.Join(".", segments);
            }

            if (string.IsNullOrEmpty(ns))
            {
                return false;
            }

            normalizedNamespace = ns;
            return true;
        }

        private static string StripDirectiveComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '/' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == '/')
                    {
                        i += 2;
                        while (i < text.Length && text[i] != '\r' && text[i] != '\n')
                        {
                            i++;
                        }

                        sb.Append(' ');
                        if (i < text.Length)
                        {
                            sb.Append(text[i]);
                        }

                        continue;
                    }

                    if (next == '*')
                    {
                        i += 2;
                        while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                        {
                            i++;
                        }

                        if (i + 1 < text.Length)
                        {
                            i++;
                        }

                        sb.Append(' ');
                        continue;
                    }
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static bool TryConsumeLeadingKeyword(string text, string keyword, out string remainder)
        {
            remainder = text;
            if (!text.StartsWith(keyword, StringComparison.Ordinal))
            {
                return false;
            }

            if (text.Length == keyword.Length || !char.IsWhiteSpace(text[keyword.Length]))
            {
                return false;
            }

            remainder = text.Substring(keyword.Length).TrimStart();
            return true;
        }


        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            var operation = UnityCommandGate.ReadSnapshot();
            string opId = targetOperationId ?? operation?.OperationId;
            string resultFilePath = UnityLeanMcpPaths.GetEvalResultFile(opId);
            OperationExecutionEngine.MarkInterrupted(OperationKinds.Eval, resultFilePath, message, targetOperationId);
        }
    }
}
