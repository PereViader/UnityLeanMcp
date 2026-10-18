using System;
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
            if (File.Exists(UnityLeanMcpPaths.GetEvalResultFile(operationId)))
            {
                writer.WriteLine("RUNNING");
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

                // Attempt compilation with smart wrapping
                byte[] assemblyBytes;
                bool isVoidStatement;
                List<string> errors;

                if (!TryCompileSnippet(rawCode, out assemblyBytes, out isVoidStatement, out errors) || assemblyBytes == null)
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
                    invoker: ct =>
                    {
                        var asm = Assembly.Load(assemblyBytes);
                        var runnerType = asm.GetType("__UnityLeanMcpEvalRunner");
                        if (runnerType == null)
                        {
                            throw new Exception("Evaluation runner type could not be loaded from dynamic assembly.");
                        }

                        var execMethod = runnerType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                        if (execMethod == null)
                        {
                            throw new Exception("Evaluation runner execute method not found.");
                        }

                        return execMethod.Invoke(null, new object[] { ct });
                    },
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

        private static string BuildSource(string methodBody, bool isVoid, List<string> userUsings = null)
        {
            var sb = new StringBuilder();

            if (userUsings != null && userUsings.Count > 0)
            {
                foreach (var u in userUsings)
                {
                    sb.AppendLine(u);
                }
            }

            sb.AppendLine("using UnityLeanMcp;");
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


        public static void MarkInterrupted(string message, string targetOperationId = null)
        {
            var operation = UnityCommandGate.ReadSnapshot();
            string opId = targetOperationId ?? operation?.OperationId;
            string resultFilePath = UnityLeanMcpPaths.GetEvalResultFile(opId);
            OperationExecutionEngine.MarkInterrupted(OperationKinds.Eval, resultFilePath, message, targetOperationId);
        }
    }
}
