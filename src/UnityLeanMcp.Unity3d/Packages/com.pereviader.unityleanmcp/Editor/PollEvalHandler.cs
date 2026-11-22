using System.IO;

namespace UnityLeanMcp
{
    internal class PollEvalHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && !UnityLeanMcpPaths.IsValidOperationId(operationId))
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }

            PollHelper.PollOperationResult(
                operationId,
                OperationKinds.Eval,
                UnityLeanMcpPaths.GetEvalResultFile(operationId),
                writer,
                PollHelper.WriteOperationResultResponse);
        }
    }
}
