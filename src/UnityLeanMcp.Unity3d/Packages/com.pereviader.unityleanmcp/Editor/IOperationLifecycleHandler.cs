using System.IO;

namespace UnityLeanMcp
{
    internal enum OperationCancelResult
    {
        Cancelled,
        NotCancelable,
        NotFound
    }

    internal interface IOperationLifecycleHandler
    {
        string OperationKind { get; }
        bool TryRequestCancelFromWorker(string operationId);
        OperationCancelResult TryCancel(string operationId);
        void OnEditorRestarted(string operationId, string message);
        void OnDomainReloaded(string operationId, string message);
        void OnEditorQuitting(string operationId);
    }
}
