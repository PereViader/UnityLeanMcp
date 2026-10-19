using System;
using System.IO;
using UnityEngine;
using UnityLeanMcp;

namespace PereViader.UnityLeanMcp.Editor
{
    internal abstract class CompilationCommandHandlerBase : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => false;

        internal string Kind => OperationKind;
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

            var begin = UnityCommandGate.TryBegin(OperationKind, operationId, OperationStatus.Requested, out var busyReason);
            if (begin == BeginGateResult.Cancelled)
            {
                writer.WriteLine("INTERRUPTION Operation was cancelled before admission.");
                return;
            }
            if (begin == BeginGateResult.Invalid)
            {
                writer.WriteLine("ERROR: Missing or invalid operation id");
                return;
            }

            if (begin == BeginGateResult.Busy)
            {
                writer.WriteLine($"BUSY {busyReason}");
                return;
            }

            // A disconnected caller must not prevent already admitted work.
            try
            {
                writer.WriteLine(StatusWord);
                writer.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            if (begin == BeginGateResult.AlreadyStarted)
            {
                return;
            }

            UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();
            RefreshErrorLogCapture.StartCapture(operationId);
            UnityLeanMcpCompilationTracker.RefreshPending = true;
            UnityLeanMcpCompilationTracker.AwaitingCompilationStart = true;
            UnityCommandGate.Update(operationId, OperationKind == OperationKinds.Refresh ? OperationStatus.Refreshing : OperationStatus.Recompiling);

            try
            {
                ExecuteCompilation();
            }
            catch (Exception ex)
            {
                RefreshErrorLogCapture.StopActiveCapture();
                UnityLeanMcpCompilationTracker.AwaitingCompilationStart = false;
                var result = new UnityRefreshResult { operationId = operationId, success = false, message = ex.ToString() };
                UnityCommandGate.PublishResult(OperationKind, operationId,
                    UnityLeanMcpPaths.GetRefreshResultFile(operationId), JsonUtility.ToJson(result, true));
            }
            finally
            {
                UnityLeanMcpCompilationTracker.RefreshPending = false;
                UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}
