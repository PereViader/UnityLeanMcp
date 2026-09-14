using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class RecompileHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
        public bool IsMutating => true;
        public bool RequiresCompilationSettled => false;

        public void Handle(string payload, StreamWriter writer)
        {
            string operationId = payload?.Trim();
            if (!string.IsNullOrEmpty(operationId) && File.Exists(UnityLeanMcpPaths.GetRefreshResultFile(operationId)))
            {
                writer.WriteLine("RECOMPILING");
                return;
            }
            var begin = UnityLeanMcpOperationStore.TryBegin(operationId, OperationKinds.Recompile, OperationStatus.Requested, out var existing);
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

            writer.WriteLine("RECOMPILING");
            writer.Flush();
            if (begin == BeginOperationResult.AlreadyStarted)
            {
                return;
            }

            UnityLeanMcpCompilationTracker.ClearCapturedDiagnostics();

            UnityLeanMcpCompilationTracker.RefreshPending = true;
            UnityLeanMcpCompilationTracker.CompilationRequested = true;
            UnityLeanMcpOperationStore.Update(operationId, OperationStatus.Recompiling);
            try
            {
                Debug.Log("UnityLeanMcp: Triggering force recompilation via CompilationPipeline.RequestScriptCompilation()");
                UnityLeanMcpCompilationTracker.ClearActiveEntries();
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
            }
            finally
            {
                UnityLeanMcpCompilationTracker.RefreshPending = false;
                UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
            }
        }
    }
}
