using System.IO;

namespace UnityLeanMcp
{
    internal class PollTestsHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            PollHelper.PollOperationResult(
                operationId,
                UnityLeanMcpPaths.GetTestResultsFile(operationId),
                UnityLeanMcpPaths.TestRunningFile,
                writer,
                (res, w) =>
                {
                    string skipStr = res.SkipCount > 0 ? $", {res.SkipCount} skipped" : "";
                    if (res.Success)
                    {
                        w.WriteLine($"SUCCESS {res.PassCount} passed{skipStr}");
                    }
                    else if (res.Interrupted)
                    {
                        w.WriteLine($"INTERRUPTION {PollHelper.EscapeLine(res.Message)}");
                    }
                    else if (!string.IsNullOrEmpty(res.Message))
                    {
                        w.WriteLine($"FAILURE {PollHelper.EscapeLine(res.Message)}");
                    }
                    else
                    {
                        w.WriteLine($"FAILURE {res.FailCount} failed, {res.PassCount} passed{skipStr}");
                    }
                },
                (runningPath, opId) =>
                {
                    var running = RunTestsHandler.ReadThreadSafeSnapshot();
                    return running != null && (string.IsNullOrEmpty(opId) || running.RunId == opId);
                });
        }
    }
}
