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
            string[] parts = (payload ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bool isReadinessCheck = parts.Length == 2 && string.Equals(parts[0], "CHECK", StringComparison.OrdinalIgnoreCase);
            string operationId = isReadinessCheck ? parts[1] : payload?.Trim();

            if (isReadinessCheck)
            {
                var readiness = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
                if (readiness != null)
                {
                    if (readiness.Kind == OperationKinds.Refresh || readiness.Kind == OperationKinds.Recompile)
                    {
                        return readiness.Status == OperationStatus.Interrupted
                            ? "INTERRUPTION Unity editor restarted before the operation completed."
                            : "COMPILING";
                    }

                    return $"BUSY {readiness.Kind} {readiness.OperationId}";
                }

                if (UnityLeanMcpCompilationTracker.RefreshPending ||
                    UnityLeanMcpCompilationTracker.CompilationRequested ||
                    UnityLeanMcpCompilationTracker.IsCompiling)
                {
                    return "COMPILING";
                }

                if (UnityLeanMcpCompilationTracker.IsUpdating)
                {
                    return "UPDATING";
                }

                if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed ||
                    UnityLeanMcpCompilationTracker.RefreshRequired)
                {
                    return "REFRESH_REQUIRED";
                }

                return "READY";
            }

            if (!string.IsNullOrEmpty(operationId) && UnityLeanMcpCompilationTracker.TryReadRefreshResultThreadSafe(operationId, out var result))
            {
                if (result.Interrupted) return $"INTERRUPTION {PollHelper.EscapeLine(result.Message)}";
                return result.Success ? "READY" : "COMPILATION_ERROR";
            }
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            if (operation != null)
            {
                if (operation.Kind == OperationKinds.Refresh || operation.Kind == OperationKinds.Recompile)
                {
                    if (operation.Status == OperationStatus.Interrupted)
                    {
                        return "INTERRUPTION Unity editor restarted before the operation completed.";
                    }
                    return "COMPILING";
                }

                if (string.IsNullOrEmpty(operationId) || operation.OperationId != operationId)
                {
                    return $"BUSY {operation.Kind} {operation.OperationId}";
                }
            }

            if (UnityLeanMcpCompilationTracker.RefreshPending || UnityLeanMcpCompilationTracker.CompilationRequested)
            {
                return "COMPILING";
            }

            if (UnityLeanMcpCompilationTracker.IsCompiling)
            {
                return "COMPILING";
            }

            if (UnityLeanMcpCompilationTracker.IsUpdating)
            {
                return "UPDATING";
            }

            if (UnityLeanMcpCompilationTracker.ScriptCompilationFailed)
            {
                if (!string.IsNullOrEmpty(operationId))
                {
                    return "IDLE";
                }

                string diagnosticsPath = UnityLeanMcpPaths.WorkerDiagnosticsFile;
                if (File.Exists(diagnosticsPath) && new FileInfo(diagnosticsPath).Length > 0)
                {
                    return "COMPILATION_ERROR";
                }

                return "COMPILING";
            }

            return string.IsNullOrEmpty(operationId) ? "READY" : "IDLE";
        }
    }
}
