using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEditor;

namespace UnityLeanMcp
{
    internal static class OperationLifecycleRegistry
    {
        private static readonly ConcurrentDictionary<string, IOperationLifecycleHandler> s_Handlers =
            new ConcurrentDictionary<string, IOperationLifecycleHandler>(StringComparer.OrdinalIgnoreCase);

        private static bool s_Initialized;
        private static readonly object s_InitLock = new object();

        public static void EnsureInitialized()
        {
            if (Volatile.Read(ref s_Initialized)) return;
            lock (s_InitLock)
            {
                if (Volatile.Read(ref s_Initialized)) return;
                InitializeDefaultHandlers();
                Volatile.Write(ref s_Initialized, true);
            }
        }

        private static void InitializeDefaultHandlers()
        {
            RegisterDefault(new TestLifecycleHandler());
            RegisterDefault(new EvalLifecycleHandler());
            RegisterDefault(new CompilationLifecycleHandler(OperationKinds.Refresh));
            RegisterDefault(new CompilationLifecycleHandler(OperationKinds.Recompile));
        }

        private static void RegisterDefault(IOperationLifecycleHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.OperationKind))
            {
                return;
            }

            // Preserve an extension registered before the explicit bootstrap.
            s_Handlers.TryAdd(handler.OperationKind, handler);
        }

        public static void Register(IOperationLifecycleHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.OperationKind))
            {
                return;
            }

            s_Handlers[handler.OperationKind] = handler;
        }

        public static bool TryGetHandler(string kind, out IOperationLifecycleHandler handler)
        {
            if (string.IsNullOrEmpty(kind))
            {
                handler = null;
                return false;
            }

            return s_Handlers.TryGetValue(kind, out handler);
        }

        public static OperationCancelResult Cancel(string operationKind, string operationId)
        {
            if (TryGetHandler(operationKind, out var handler))
            {
                return handler.TryCancel(operationId);
            }

            return OperationCancelResult.NotCancelable;
        }

        public static void RecoverOnDomainLoad(UnityLeanMcpOperationState operation, bool isRestart)
        {
            if (operation == null)
            {
                return;
            }

            if (isRestart)
            {
                const string message = "Unity editor restarted before the operation completed.";
                if (TryGetHandler(operation.kind, out var handler))
                {
                    handler.OnEditorRestarted(operation.operationId, message);
                }
                else
                {
                    UnityLeanMcpOperationStore.Complete(operation.operationId);
                }
            }
            else
            {
                const string message = "Operation was interrupted by Unity domain reload.";
                if (TryGetHandler(operation.kind, out var handler))
                {
                    handler.OnDomainReloaded(operation.operationId, message);
                }
                else
                {
                    UnityLeanMcpOperationStore.Complete(operation.operationId);
                }
            }
        }

        public static void NotifyQuitting(UnityLeanMcpOperationState operation)
        {
            if (operation == null)
            {
                return;
            }

            UnityLeanMcpOperationStore.Update(operation.operationId, OperationStatus.ShuttingDown);

            if (TryGetHandler(operation.kind, out var handler))
            {
                handler.OnEditorQuitting(operation.operationId);
            }
            else
            {
                UnityLeanMcpOperationStore.Complete(operation.operationId);
            }
        }
    }

    internal class TestLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Test;

        public OperationCancelResult TryCancel(string operationId)
        {
            return RunTestsHandler.CancelActiveTestRun(operationId);
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            RunTestsHandler.WriteInterruptedResult(message, operationId);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            // Test runner api callbacks survive domain reload and are bound during server initialization.
        }

        public void OnEditorQuitting(string operationId)
        {
            RunTestsHandler.WriteInterruptedResult("Command interrupted by Unity editor shutdown.", operationId);
        }
    }

    internal class EvalLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind => OperationKinds.Eval;

        public OperationCancelResult TryCancel(string operationId)
        {
            bool cancelled = EvalHandler.CancelActiveEval(operationId);
            return cancelled ? OperationCancelResult.Cancelled : OperationCancelResult.NotCancelable;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            EvalHandler.MarkInterrupted(message, operationId);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            EvalHandler.MarkInterrupted(message, operationId);
        }

        public void OnEditorQuitting(string operationId)
        {
            EvalHandler.MarkInterrupted("Command interrupted by Unity editor shutdown.", operationId);
        }
    }

    internal class CompilationLifecycleHandler : IOperationLifecycleHandler
    {
        public string OperationKind { get; }

        public CompilationLifecycleHandler(string operationKind)
        {
            OperationKind = operationKind;
        }

        public OperationCancelResult TryCancel(string operationId)
        {
            return OperationCancelResult.NotCancelable;
        }

        public void OnEditorRestarted(string operationId, string message)
        {
            UnityLeanMcpCompilationTracker.WriteInterruptedRefreshResult(operationId, message);
        }

        public void OnDomainReloaded(string operationId, string message)
        {
            UnityLeanMcpCompilationTracker.ObserveOperationUntilSettled();
        }

        public void OnEditorQuitting(string operationId)
        {
            UnityLeanMcpCompilationTracker.WriteInterruptedRefreshResult(operationId, "Command interrupted by Unity editor shutdown.");
        }
    }
}
