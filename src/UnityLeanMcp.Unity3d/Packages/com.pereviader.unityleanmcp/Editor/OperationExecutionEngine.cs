using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityLeanMcp
{
    internal static class OperationExecutionEngine
    {
        private static readonly object s_CtsLock = new object();
        private static CancellationTokenSource s_ActiveCts;
        private static string s_ActiveOperationId;
        private static bool s_ActiveIsCancelable;
        private static ConsoleLogCapture s_ActiveLogCapture;

        public static CancellationTokenSource RegisterActiveOperation(string operationId, bool isCancelable)
        {
            CancellationTokenSource cts;
            CancellationTokenSource oldCtsToDispose = null;
            ConsoleLogCapture oldCaptureToDispose = null;
            lock (s_CtsLock)
            {
                oldCtsToDispose = s_ActiveCts;
                oldCaptureToDispose = s_ActiveLogCapture;
                cts = new CancellationTokenSource();
                s_ActiveCts = cts;
                s_ActiveOperationId = operationId;
                s_ActiveIsCancelable = isCancelable;
                s_ActiveLogCapture = null;
            }

            oldCtsToDispose?.Dispose();
            DisposeCapture(oldCaptureToDispose);
            return cts;
        }

        public static bool TryCancel(string operationId)
        {
            CancellationTokenSource ctsToCancel = null;
            lock (s_CtsLock)
            {
                if (s_ActiveCts != null && s_ActiveIsCancelable && (string.IsNullOrEmpty(operationId) || s_ActiveOperationId == operationId))
                {
                    ctsToCancel = s_ActiveCts;
                }
            }

            if (ctsToCancel != null)
            {
                try
                {
                    ctsToCancel.Cancel();
                    return true;
                }
                catch (Exception)
                {
                    // Cancellation is requested from a socket worker thread.
                    // Do not call Unity logging here; the main-thread
                    // operation will publish its terminal state if needed.
                }
            }
            return false;
        }

        public static void MarkInterrupted(string operationKind, string resultFilePath, string message, string targetOperationId = null)
        {
            var operation = UnityLeanMcpOperationStore.Read();
            string opId = targetOperationId ?? operation?.operationId;
            if (string.IsNullOrEmpty(opId))
            {
                return;
            }
            if (operation != null && operation.kind != operationKind && targetOperationId == null)
            {
                return;
            }

            try
            {
                var result = new UnityOperationResult
                {
                    operationId = opId,
                    success = false,
                    interrupted = true,
                    message = message,
                    duration = 0,
                    payload = null,
                    logs = null
                };
                UnityLeanMcpOperationStore.WriteAtomic(resultFilePath, JsonUtility.ToJson(result, true), opId);
                UnityLeanMcpOperationStore.Complete(opId);
                ReleaseActiveOperation(opId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to persist interrupted {operationKind} result: {ex}");
            }
        }

        public static void Execute(
            string operationId,
            string operationKind,
            string resultFilePath,
            bool isVoid,
            Func<CancellationToken, object> invoker,
            bool canCancel = true,
            Action<Exception> onInvocationException = null)
        {
            UnityLeanMcpDispatcher.EnsureInitialized();

            CancellationTokenSource cts;
            CancellationTokenSource oldCtsToDispose = null;
            ConsoleLogCapture oldCaptureToDispose = null;
            lock (s_CtsLock)
            {
                if (s_ActiveOperationId == operationId && s_ActiveCts != null)
                {
                    cts = s_ActiveCts;
                    s_ActiveIsCancelable = canCancel;
                }
                else
                {
                    oldCtsToDispose = s_ActiveCts;
                    oldCaptureToDispose = s_ActiveLogCapture;
                    s_ActiveCts = new CancellationTokenSource();
                    s_ActiveOperationId = operationId;
                    s_ActiveIsCancelable = canCancel;
                    s_ActiveLogCapture = null;
                    cts = s_ActiveCts;
                }
            }

            oldCtsToDispose?.Dispose();
            DisposeCapture(oldCaptureToDispose);

            if (cts.IsCancellationRequested)
            {
                string cancelMsg = GetCancellationMessage(operationKind);
                FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, 0, null, new List<ConsoleLogEntry>(), interrupted: true);
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var logCapture = new ConsoleLogCapture();
            bool isActive;
            lock (s_CtsLock)
            {
                isActive = (s_ActiveOperationId == operationId);
                if (isActive)
                {
                    s_ActiveLogCapture = logCapture;
                }
            }

            if (!isActive)
            {
                DisposeCapture(logCapture);
                string cancelMsg = GetCancellationMessage(operationKind);
                FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, 0, null, new List<ConsoleLogEntry>(), interrupted: true);
                return;
            }

            object result = null;
            try
            {
                result = invoker(cts.Token);
            }
            catch (TargetInvocationException tie)
            {
                stopwatch.Stop();
                var inner = tie.InnerException != null ? tie.InnerException : tie;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                if (inner is OperationCanceledException)
                {
                    string cancelMsg = GetCancellationMessage(operationKind);
                    Debug.LogWarning($"UnityLeanMcp: {operationKind} execution was canceled: {inner.Message}");
                    FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                    return;
                }
                onInvocationException?.Invoke(inner);
                string errorMsg = inner.ToString();
                FinishOperation(operationId, operationKind, resultFilePath, false, errorMsg, stopwatch.Elapsed.TotalSeconds, null, logs);
                return;
            }
            catch (OperationCanceledException oce)
            {
                stopwatch.Stop();
                string cancelMsg = GetCancellationMessage(operationKind);
                Debug.LogWarning($"UnityLeanMcp: {operationKind} execution was canceled: {oce.Message}");
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: true);
                return;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                onInvocationException?.Invoke(ex);
                string errorMsg = ex.ToString();
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishOperation(operationId, operationKind, resultFilePath, false, errorMsg, stopwatch.Elapsed.TotalSeconds, null, logs);
                return;
            }

            try
            {
                UnwrapAndFinish(result, operationId, operationKind, resultFilePath, isVoid, stopwatch, logCapture, onInvocationException);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishOperation(operationId, operationKind, resultFilePath, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
            }
        }

        private static void UnwrapAndFinish(
            object rawResult,
            string operationId,
            string operationKind,
            string resultFilePath,
            bool isVoid,
            System.Diagnostics.Stopwatch stopwatch,
            ConsoleLogCapture logCapture,
            Action<Exception> onInvocationException)
        {
            // Check if rawResult is a ValueTask or ValueTask<T>
            if (rawResult != null)
            {
                Type resultType = rawResult.GetType();
                if (resultType.FullName != null && resultType.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal))
                {
                    var asTaskMethod = resultType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
                    if (asTaskMethod != null && asTaskMethod.GetParameters().Length == 0 && typeof(Task).IsAssignableFrom(asTaskMethod.ReturnType))
                    {
                        try
                        {
                            rawResult = asTaskMethod.Invoke(rawResult, null);
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                            var logs = logCapture.GetLogs();
                            DisposeCapture(logCapture);
                            bool isCanceled = inner is OperationCanceledException;
                            string msg = isCanceled ? GetCancellationMessage(operationKind) : inner.ToString();
                            if (!isCanceled)
                            {
                                onInvocationException?.Invoke(inner);
                            }
                            FinishOperation(operationId, operationKind, resultFilePath, false, msg, stopwatch.Elapsed.TotalSeconds, null, logs, interrupted: isCanceled);
                            return;
                        }
                    }
                }
            }

            // Check if rawResult is a Task
            if (rawResult is Task innerTask)
            {
                if (!innerTask.IsCompleted)
                {
                    innerTask.ContinueWith(t => UnityLeanMcpDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            UnwrapCompletedTask(t, operationId, operationKind, resultFilePath, isVoid, stopwatch, logCapture, onInvocationException);
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            var logs = logCapture.GetLogs();
                            DisposeCapture(logCapture);
                            FinishOperation(operationId, operationKind, resultFilePath, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                        }
                    }));
                    return;
                }

                try
                {
                    UnwrapCompletedTask(innerTask, operationId, operationKind, resultFilePath, isVoid, stopwatch, logCapture, onInvocationException);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    var logs = logCapture.GetLogs();
                    DisposeCapture(logCapture);
                    FinishOperation(operationId, operationKind, resultFilePath, false, ex.ToString(), stopwatch.Elapsed.TotalSeconds, null, logs);
                }
                return;
            }

            // Not a task, finish directly
            stopwatch.Stop();
            double duration = stopwatch.Elapsed.TotalSeconds;
            var finalLogs = logCapture.GetLogs();
            DisposeCapture(logCapture);
            string payload = null;
            if (!isVoid)
            {
                try
                {
                    payload = CommandHelper.FormatResult(rawResult, false, false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"UnityLeanMcp: Failed to format result: {ex.Message}");
                    payload = rawResult?.ToString();
                }
            }
            FinishOperation(operationId, operationKind, resultFilePath, true, "", duration, payload, finalLogs);
        }

        private static void UnwrapCompletedTask(
            Task innerTask,
            string operationId,
            string operationKind,
            string resultFilePath,
            bool isVoid,
            System.Diagnostics.Stopwatch stopwatch,
            ConsoleLogCapture logCapture,
            Action<Exception> onInvocationException)
        {
            if (innerTask.IsFaulted)
            {
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                var ex = innerTask.Exception != null
                    ? (innerTask.Exception.InnerExceptions.Count == 1 ? innerTask.Exception.InnerExceptions[0] : innerTask.Exception)
                    : new Exception("Unknown task failure");
                if (ex is OperationCanceledException)
                {
                    string cancelMsg = GetCancellationMessage(operationKind);
                    Debug.LogWarning($"UnityLeanMcp: {operationKind} execution was canceled: {ex.Message}");
                    FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, duration, null, logs, interrupted: true);
                    return;
                }
                onInvocationException?.Invoke(ex);
                string errorMsg = ex.ToString();
                FinishOperation(operationId, operationKind, resultFilePath, false, errorMsg, duration, null, logs);
                return;
            }

            if (innerTask.IsCanceled)
            {
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                string cancelMsg = GetCancellationMessage(operationKind);
                Debug.LogWarning($"UnityLeanMcp: {operationKind} execution was canceled.");
                FinishOperation(operationId, operationKind, resultFilePath, false, cancelMsg, duration, null, logs, interrupted: true);
                return;
            }

            // Check if generic Task<T>
            Type tType = innerTask.GetType();
            PropertyInfo resultProp = null;
            if (!isVoid)
            {
                Type cur = tType;
                while (cur != null && cur != typeof(object))
                {
                    if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var genericArgs = cur.GetGenericArguments();
                        if (genericArgs.Length > 0 && genericArgs[0].Name != "VoidTaskResult")
                        {
                            resultProp = cur.GetProperty("Result");
                        }
                        break;
                    }
                    cur = cur.BaseType;
                }
            }

            if (resultProp != null)
            {
                object innerVal = resultProp.GetValue(innerTask);
                UnwrapAndFinish(innerVal, operationId, operationKind, resultFilePath, isVoid, stopwatch, logCapture, onInvocationException);
            }
            else
            {
                // Non-generic Task (void completion)
                stopwatch.Stop();
                double duration = stopwatch.Elapsed.TotalSeconds;
                var logs = logCapture.GetLogs();
                DisposeCapture(logCapture);
                FinishOperation(operationId, operationKind, resultFilePath, true, "", duration, null, logs);
            }
        }

        public static void FinishOperation(
            string operationId,
            string operationKind,
            string resultFilePath,
            bool success,
            string message,
            double duration,
            string payload,
            List<ConsoleLogEntry> logs = null,
            bool interrupted = false)
        {
            if (!UnityLeanMcpOperationStore.IsOwnedBy(operationId, operationKind))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(UnityLeanMcpPaths.TempDir))
                {
                    Directory.CreateDirectory(UnityLeanMcpPaths.TempDir);
                }

                var runResult = new UnityOperationResult
                {
                    operationId = operationId,
                    success = success,
                    interrupted = interrupted,
                    message = message,
                    duration = duration,
                    payload = payload,
                    logs = logs
                };
                string json = JsonUtility.ToJson(runResult, true);
                UnityLeanMcpOperationStore.WriteAtomic(resultFilePath, json, operationId);
                UnityLeanMcpOperationStore.Complete(operationId);
                ReleaseActiveOperation(operationId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityLeanMcp: Failed to write {operationKind} result: {ex}");
            }
        }

        private static void ReleaseActiveOperation(string operationId)
        {
            CancellationTokenSource ctsToDispose = null;
            ConsoleLogCapture captureToDispose = null;
            lock (s_CtsLock)
            {
                if (s_ActiveOperationId != operationId)
                {
                    return;
                }

                ctsToDispose = s_ActiveCts;
                s_ActiveCts = null;
                s_ActiveOperationId = null;
                s_ActiveIsCancelable = false;
                captureToDispose = s_ActiveLogCapture;
                s_ActiveLogCapture = null;
            }

            ctsToDispose?.Dispose();
            DisposeCapture(captureToDispose);
        }

        private static void DisposeCapture(ConsoleLogCapture logCapture)
        {
            if (logCapture == null) return;
            lock (s_CtsLock)
            {
                if (s_ActiveLogCapture == logCapture)
                {
                    s_ActiveLogCapture = null;
                }
            }
            try
            {
                logCapture.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to dispose log capture: {ex.Message}");
            }
        }

        private static string GetCancellationMessage(string operationKind)
        {
            return operationKind == OperationKinds.Eval
                ? "Evaluation was canceled."
                : "Method execution was canceled.";
        }
    }
}
