using System;
using System.IO;
using System.Threading;

namespace UnityLeanMcp
{
    internal enum OperationCancelResult
    {
        Cancelled,
        Cancelling,
        NotCancelable,
        NotFound
    }

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
            if (string.IsNullOrWhiteSpace(operationId))
            {
                writer.WriteLine("NO_OPERATION");
                writer.Flush();
                return;
            }

            var operation = UnityCommandGate.ReadSnapshot();
            if (operation == null)
            {
                if (IsTerminalInterruptedResult(operationId))
                {
                    writer.WriteLine("CANCELLED");
                    writer.Flush();
                    return;
                }

                writer.WriteLine("NO_OPERATION");
                writer.Flush();
                return;
            }

            if (operation.OperationId != operationId)
            {
                writer.WriteLine($"MISMATCH {operation.OperationId}");
                writer.Flush();
                return;
            }

            OperationCancelResult result = CancelRouteHook != null
                ? CancelRouteHook(operation.Kind, operationId)
                : RouteCancellation(operation.Kind, operationId);

            if (IsTerminalInterruptedResult(operationId) ||
                result == OperationCancelResult.Cancelled ||
                result == OperationCancelResult.Cancelling)
            {
                var deadline = DateTime.UtcNow.AddSeconds(2.5);
                while (DateTime.UtcNow < deadline)
                {
                    if (IsTerminalInterruptedResult(operationId))
                    {
                        break;
                    }
                    var currentOp = UnityCommandGate.ReadSnapshot();
                    if (currentOp == null || !string.Equals(currentOp.OperationId, operationId, StringComparison.Ordinal))
                    {
                        break;
                    }
                    Thread.Sleep(20);
                }

                writer.WriteLine("CANCELLED");
                writer.Flush();
                return;
            }

            switch (result)
            {
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

#nullable enable
        internal static Func<string, string, OperationCancelResult>? CancelRouteHook;
#nullable restore

        private static OperationCancelResult RouteCancellation(string kind, string operationId)
        {
            if (string.Equals(kind, OperationKinds.Test, StringComparison.OrdinalIgnoreCase))
                return RunTestsHandler.CancelActiveTestRun(operationId);
            if (string.Equals(kind, OperationKinds.Eval, StringComparison.OrdinalIgnoreCase))
            {
                if (!OperationExecutionEngine.TryCancel(operationId))
                {
                    return OperationCancelResult.NotCancelable;
                }
                return IsTerminalInterruptedResult(operationId)
                    ? OperationCancelResult.Cancelled
                    : OperationCancelResult.Cancelling;
            }
            return OperationCancelResult.NotCancelable;
        }

        internal static bool IsTerminalInterruptedResult(string operationId)
        {
            return IsTerminalInterruptedResult(UnityLeanMcpPaths.GetTestResultsFile(operationId), operationId)
                || IsTerminalInterruptedResult(UnityLeanMcpPaths.GetEvalResultFile(operationId), operationId)
                || IsTerminalInterruptedRefreshResult(UnityLeanMcpPaths.GetRefreshResultFile(operationId), operationId);
        }

        private static bool IsTerminalInterruptedResult(string path, string operationId)
        {
            return !string.IsNullOrEmpty(path)
                && WorkerThreadSnapshots.TryReadOperationResult(path, out var result)
                && result.OperationId == operationId
                && (result.Interrupted
                    || result.ResultState == OperationStatus.Cancelled
                    || result.ResultState == OperationStatus.Interrupted);
        }

        private static bool IsTerminalInterruptedRefreshResult(string path, string operationId)
        {
            return !string.IsNullOrEmpty(path)
                && WorkerThreadSnapshots.TryReadRefreshResult(path, out var result)
                && result.OperationId == operationId
                && result.Interrupted;
        }
    }
}
