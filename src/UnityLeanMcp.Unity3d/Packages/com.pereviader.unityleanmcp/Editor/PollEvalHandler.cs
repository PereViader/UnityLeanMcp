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
            PollHelper.PollOperationResult(
                operationId,
                UnityLeanMcpPaths.GetEvalResultFile(operationId),
                null,
                writer,
                PollHelper.WriteOperationResultResponse);
        }
    }
}
