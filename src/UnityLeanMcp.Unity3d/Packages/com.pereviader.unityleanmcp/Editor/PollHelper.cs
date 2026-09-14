using System;
using System.IO;

namespace UnityLeanMcp
{
    internal static class PollHelper
    {
        public static string EscapeLine(string text)
        {
            return ProtocolCodec.EscapeLine(text);
        }

        public static void WriteOperationResultResponse(WorkerOperationResultSnapshot res, StreamWriter writer)
        {
            if (res.Success)
            {
                if (!string.IsNullOrEmpty(res.Payload))
                {
                    writer.WriteLine($"SUCCESS {EscapeLine(res.Payload)}");
                }
                else
                {
                    writer.WriteLine("SUCCESS");
                }
            }
            else if (res.Interrupted)
            {
                writer.WriteLine($"INTERRUPTION {EscapeLine(res.Message)}");
            }
            else
            {
                writer.WriteLine($"FAILURE {EscapeLine(res.Message)}");
            }
        }

        public static void PollOperationResult(
            string operationId,
            string resultFilePath,
            string runningFilePath,
            StreamWriter writer,
            Action<WorkerOperationResultSnapshot, StreamWriter> writeResultResponse,
            Func<string, string, bool> isRunningMatch = null)
        {
            bool TryWriteTerminalResult()
            {
                if (!string.IsNullOrEmpty(resultFilePath) && File.Exists(resultFilePath))
                {
                    try
                    {
                        if (WorkerThreadSnapshots.TryReadOperationResult(resultFilePath, out var res))
                        {
                            if (string.IsNullOrEmpty(operationId) || res.OperationId == operationId)
                            {
                                writeResultResponse(res, writer);
                                return true;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Transient read or deserialization error during reload/transition; fall through to running check
                    }
                }
                return false;
            }

            // 1. Matching terminal results are authoritative
            if (TryWriteTerminalResult())
            {
                return;
            }

            // 2. Active running state for this operation
            if (isRunningMatch != null)
            {
                if (isRunningMatch(runningFilePath, operationId))
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(runningFilePath) && File.Exists(runningFilePath))
            {
                bool matches = false;
                try
                {
                    string runningOperationId = WorkerThreadSnapshots.ReadFileWithRetry(runningFilePath)?.Trim();
                    matches = string.IsNullOrEmpty(operationId) || runningOperationId == operationId;
                }
                catch (IOException)
                {
                    matches = false;
                }
                catch (Exception)
                {
                    matches = false;
                }

                if (matches)
                {
                    writer.WriteLine("RUNNING");
                    return;
                }
            }

            // 3. Fallback to operation store for busy vs idle state
            var operation = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
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
            }
            else
            {
                // Re-check terminal result file before declaring IDLE to avoid boundary race condition
                // where the result file was durable and ownership was cleared right as polling occurred.
                if (TryWriteTerminalResult())
                {
                    return;
                }

                writer.WriteLine("IDLE");
            }
        }
    }
}
