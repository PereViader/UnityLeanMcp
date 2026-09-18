using System.IO;
using UnityLeanMcp;

namespace PereViader.UnityLeanMcp.Editor
{
    internal abstract class CompilationCommandHandlerBase : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => false;

        protected abstract string OperationKind { get; }
        protected abstract string StatusWord { get; }
        protected abstract void ExecuteCompilation();

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && File.Exists(UnityLeanMcpPaths.GetRefreshResultFile(operationId)))
            {
                writer.WriteLine(StatusWord);
                return;
            }

            var begin = UnityLeanMcpOperationStore.TryBegin(operationId, OperationKind, OperationStatus.Requested, out var existing);
            if (begin == BeginOperationResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }

            if (begin == BeginOperationResult.Busy)
            {
                writer.WriteLine($"BUSY {existing.kind} {existing.operationId}");
                return;
            }

            writer.WriteLine(StatusWord);
            writer.Flush();

            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();
            UnityLeanMcpCompilationTracker.RefreshPending = true;
            UnityLeanMcpCompilationTracker.CompilationRequested = true;
            UnityLeanMcpOperationStore.Update(operationId, OperationKind == OperationKinds.Refresh ? OperationStatus.Refreshing : OperationStatus.Recompiling);

            try
            {
                ExecuteCompilation();
            }
            finally
            {
                UnityLeanMcpCompilationTracker.RefreshPending = false;
                UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}
