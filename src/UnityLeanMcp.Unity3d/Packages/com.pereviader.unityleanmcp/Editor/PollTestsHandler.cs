using System;
using System.IO;
using UnityEngine;

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
            string resultFilePath = UnityLeanMcpPaths.GetTestResultsFile(operationId);

            if (!string.IsNullOrEmpty(resultFilePath) && File.Exists(resultFilePath))
            {
                try
                {
                    if (WorkerThreadSnapshots.TryReadOperationResult(resultFilePath, out var res))
                    {
                        if (string.IsNullOrEmpty(operationId) || res.OperationId == operationId)
                        {
                            WriteTerminalResult(res, writer);
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            var runningState = RunTestsHandler.ReadRunningState();
            if (runningState != null && (string.IsNullOrEmpty(operationId) || runningState.runId == operationId))
            {
                string json = JsonUtility.ToJson(runningState);
                string escapedJson = ProtocolCodec.EscapeLine(json);
                writer.WriteLine($"RUNNING {escapedJson}");
                return;
            }

            var operation = UnityCommandGate.ReadSnapshot();
            if (operation != null)
            {
                if (!string.IsNullOrEmpty(operationId) && operation.OperationId != operationId)
                {
                    writer.WriteLine($"BUSY {operation.Kind} {operation.OperationId}");
                }
                else if (operation.Status == OperationStatus.Interrupted)
                {
                    writer.WriteLine("INTERRUPTION Unity editor restarted before the operation completed.");
                }
                else
                {
                    writer.WriteLine("RUNNING");
                }
                return;
            }

            if (!string.IsNullOrEmpty(resultFilePath) && File.Exists(resultFilePath))
            {
                if (WorkerThreadSnapshots.TryReadOperationResult(resultFilePath, out var res) &&
                    (string.IsNullOrEmpty(operationId) || res.OperationId == operationId))
                {
                    WriteTerminalResult(res, writer);
                    return;
                }
            }

            writer.WriteLine("IDLE");
        }

        private static void WriteTerminalResult(WorkerOperationResultSnapshot res, StreamWriter writer)
        {
            string skipStr = res.SkipCount > 0 ? $", {res.SkipCount} skipped" : "";
            if (res.Success)
            {
                writer.WriteLine($"SUCCESS {res.PassCount} passed{skipStr}");
            }
            else if (res.Interrupted)
            {
                writer.WriteLine($"INTERRUPTION {PollHelper.EscapeLine(res.Message)}");
            }
            else if (!string.IsNullOrEmpty(res.Message))
            {
                writer.WriteLine($"FAILURE {PollHelper.EscapeLine(res.Message)}");
            }
            else
            {
                writer.WriteLine($"FAILURE {res.FailCount} failed, {res.PassCount} passed{skipStr}");
            }
        }
    }
}
