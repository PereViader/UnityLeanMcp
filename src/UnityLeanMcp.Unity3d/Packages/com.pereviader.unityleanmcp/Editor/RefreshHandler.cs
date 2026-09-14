using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class RefreshHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && File.Exists(UnityLeanMcpPaths.GetRefreshResultFile(operationId)))
            {
                writer.WriteLine("REFRESHING");
                return;
            }
            var begin = UnityLeanMcpOperationStore.TryBegin(operationId, OperationKinds.Refresh, OperationStatus.Requested, out var existing);
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

            writer.WriteLine("REFRESHING");
            writer.Flush();

            // A retry with the same identity is an acknowledgement, never a
            // second AssetDatabase.Refresh invocation.
            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();

            UnityLeanMcpCompilationTracker.RefreshPending = true;
            UnityLeanMcpCompilationTracker.CompilationRequested = true;
            UnityLeanMcpOperationStore.Update(operationId, OperationStatus.Refreshing);
            try
            {
                Debug.Log("UnityLeanMcp: Triggering AssetDatabase.Refresh()");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                UnityLeanMcpCompilationTracker.RefreshPending = false;
                UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
                UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}
