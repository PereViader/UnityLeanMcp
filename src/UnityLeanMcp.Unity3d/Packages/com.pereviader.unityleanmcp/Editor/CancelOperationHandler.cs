using System;
using System.IO;

namespace UnityLeanMcp
{
    internal class CancelOperationHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            CancelActiveOperation(operationId, writer);
        }

        public static void CancelActiveOperation(string operationId, StreamWriter writer)
        {
            operationId = operationId?.Trim();
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
            if (operation == null)
            {
                if (!string.IsNullOrEmpty(operationId)
                    && (IsTerminalInterruptedResult(UnityLeanMcpPaths.GetWorkerTestResultsFile(operationId), operationId)
                        || IsTerminalInterruptedResult(UnityLeanMcpPaths.GetWorkerEvalResultFile(operationId), operationId)))
                {
                    writer.WriteLine("CANCELLED");
                    writer.Flush();
                    return;
                }

                writer.WriteLine("NO_OPERATION");
                writer.Flush();
                return;
            }

            if (!string.IsNullOrEmpty(operationId) && operation.OperationId != operationId)
            {
                writer.WriteLine($"MISMATCH {operation.OperationId}");
                writer.Flush();
                return;
            }

            OperationCancelResult result = OperationLifecycleRegistry.Cancel(operation.Kind, operation.OperationId);

            switch (result)
            {
                case OperationCancelResult.Cancelled:
                    writer.WriteLine("CANCELLED");
                    break;
                case OperationCancelResult.NotFound:
                    writer.WriteLine("NO_OPERATION");
                    break;
                case OperationCancelResult.NotCancelable:
                default:
                    writer.WriteLine("NOT_CANCELABLE");
                    break;
            }
            writer.Flush();
        }

        private static bool IsTerminalInterruptedResult(string path, string operationId)
        {
            return !string.IsNullOrEmpty(path)
                && WorkerThreadSnapshots.TryReadOperationResult(path, out var result)
                && result.OperationId == operationId
                && (result.Interrupted || result.ResultState == OperationStatus.Cancelled);
        }
    }
}
