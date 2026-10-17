using System;
using System.IO;

namespace UnityLeanMcp
{
    internal class PollRefreshHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string response = GetRefreshPollResponse(payload);
            writer.WriteLine(response);
        }

        private string GetRefreshPollResponse(string payload)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) &&
                operationId.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            {
                return "ERROR Invalid refresh operation id.";
            }

            bool isCorrelated = !string.IsNullOrEmpty(operationId);

            if (isCorrelated && UnityLeanMcpCompilationTracker.TryReadRefreshResultThreadSafe(operationId, out var result))
            {
                if (result.Interrupted) return $"INTERRUPTION {PollHelper.EscapeLine(result.Message)}";
                return result.Success ? "READY" : "COMPILATION_ERROR";
            }

            var operation = UnityCommandGate.ReadSnapshot();
            if (operation != null)
            {
                if (isCorrelated)
                {
                    if (operation.OperationId == operationId)
                    {
                        if (operation.Status == OperationStatus.Interrupted)
                        {
                            return "INTERRUPTION Unity editor restarted before the operation completed.";
                        }
                        return "COMPILING";
                    }

                    return $"BUSY {operation.Kind}";
                }
                else
                {
                    if (operation.Kind == OperationKinds.Refresh || operation.Kind == OperationKinds.Recompile)
                    {
                        if (operation.Status == OperationStatus.Interrupted)
                        {
                            return "INTERRUPTION Unity editor restarted before the operation completed.";
                        }
                        return "COMPILING";
                    }

                    return $"BUSY {operation.Kind}";
                }
            }

            if (isCorrelated)
            {
                if (UnityLeanMcpCompilationTracker.TryReadRefreshResultThreadSafe(operationId, out result))
                {
                    if (result.Interrupted) return $"INTERRUPTION {PollHelper.EscapeLine(result.Message)}";
                    return result.Success ? "READY" : "COMPILATION_ERROR";
                }

                return "IDLE";
            }

            if (UnityLeanMcpCompilationTracker.RefreshPending ||
                UnityLeanMcpCompilationTracker.AwaitingCompilationStart ||
                UnityLeanMcpCompilationTracker.CompilationInProgress ||
                UnityLeanMcpCompilationTracker.IsCompiling)
            {
                return "COMPILING";
            }

            if (UnityLeanMcpCompilationTracker.IsUpdating)
            {
                return "UPDATING";
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                string diagnosticsPath = UnityLeanMcpPaths.DiagnosticsFile;
                if (HasNonEmptyDiagnosticsFile(diagnosticsPath))
                {
                    return "COMPILATION_ERROR";
                }

                if (FileExistsSafe(diagnosticsPath))
                {
                    return "COMPILING";
                }

                return "COMPILATION_ERROR";
            }

            if (UnityLeanMcpServer.IsPlayModeActive)
            {
                return "PLAYMODE";
            }

            if (UnityLeanMcpServer.IsPlayModeChanging)
            {
                return "BUSY playmode";
            }

            return "READY";
        }

        private static bool FileExistsSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        internal static Func<string, long> DiagnosticsFileLengthProvider { get; set; }

        internal static bool HasNonEmptyDiagnosticsFile(string diagnosticsPath)
        {
            if (string.IsNullOrEmpty(diagnosticsPath))
            {
                return false;
            }

            try
            {
                if (!File.Exists(diagnosticsPath))
                {
                    return false;
                }

                var lengthProvider = DiagnosticsFileLengthProvider;
                long length = lengthProvider != null
                    ? lengthProvider(diagnosticsPath)
                    : new FileInfo(diagnosticsPath).Length;
                return length > 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
