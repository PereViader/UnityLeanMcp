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
        OperationCancelResult TryCancel(string operationId);
        void OnDomainReloaded(string operationId, string message);
        void OnEditorQuitting(string operationId);
    }
}
