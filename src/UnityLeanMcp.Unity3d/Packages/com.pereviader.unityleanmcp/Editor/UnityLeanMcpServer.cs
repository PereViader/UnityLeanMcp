using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    // Static type initialization must remain managed-only. Unity invokes the
    // explicit bootstrap method on the Editor main thread; an external
    // assembly may otherwise be the first caller of this public API.
    public static class UnityLeanMcpServer
    {
        private const int AnyAvailablePort = 0;

        private static TcpListener _tcpListener;
        private static Thread _serverThread;
        private static readonly object s_ServerLifecycleLock = new object();
        private static volatile bool _isRunning;
        private static volatile bool _shutdownRequested;
        private static volatile bool _isReloading;
        private static readonly ManualResetEvent s_ShutdownEvent = new ManualResetEvent(false);
        private static readonly ConcurrentDictionary<TcpClient, byte> s_ActiveClients = new ConcurrentDictionary<TcpClient, byte>();

        public static bool IsRunning => _isRunning;

        private static readonly ConcurrentDictionary<string, ICommandHandler> s_Handlers =
            new ConcurrentDictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        private static bool s_DefaultHandlersRegistered;
        private static readonly object s_HandlersLock = new object();
        private static int s_BootstrapState;

        public static void RegisterHandler(string command, ICommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("Command name cannot be null or empty.", nameof(command));
            }
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            // External [InitializeOnLoad] code may be the first caller of the
            // public registry. Seed defaults before retaining the custom
            // handler so the built-in graph is initialized exactly once and
            // custom registrations continue to override built-ins.
            EnsureDefaultHandlers();
            s_Handlers[command.Trim()] = handler;
        }

        public static bool UnregisterHandler(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            EnsureDefaultHandlers();
            return s_Handlers.TryRemove(command.Trim(), out _);
        }

        public static bool TryGetHandler(string command, out ICommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                handler = null;
                return false;
            }

            EnsureDefaultHandlers();
            return s_Handlers.TryGetValue(command.Trim(), out handler);
        }

        private static void EnsureDefaultHandlers()
        {
            if (Volatile.Read(ref s_DefaultHandlersRegistered)) return;
            lock (s_HandlersLock)
            {
                if (Volatile.Read(ref s_DefaultHandlersRegistered)) return;
                RegisterDefaultHandlers();
                Volatile.Write(ref s_DefaultHandlersRegistered, true);
            }
        }

        private static void RegisterDefaultHandlers()
        {
            s_Handlers.TryAdd("PING", new PingHandler());
            s_Handlers.TryAdd("EXIT", new ExitHandler());
            s_Handlers.TryAdd("REFRESH", new RefreshHandler());
            s_Handlers.TryAdd("POLL_REFRESH", new PollRefreshHandler());
            s_Handlers.TryAdd("RECOMPILE", new RecompileHandler());
            s_Handlers.TryAdd("RUN_TESTS", new RunTestsHandler());
            s_Handlers.TryAdd("POLL_TESTS", new PollTestsHandler());
            s_Handlers.TryAdd("CANCEL_OPERATION", new CancelOperationHandler());
            s_Handlers.TryAdd("EVAL", new EvalHandler());
            s_Handlers.TryAdd("POLL_EVAL", new PollEvalHandler());
        }

        [InitializeOnLoadMethod]
        private static void BootstrapOnMainThread()
        {
            // This hook is Unity's main-thread initialization boundary. Do not
            // move any of the calls below into a static constructor or static
            // field initializer: background references to the public registry
            // are valid before this method runs.
            if (Interlocked.CompareExchange(ref s_BootstrapState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                if (CommandHelper.IsAssetImportWorkerProcess())
                {
                    Volatile.Write(ref s_BootstrapState, 2);
                    return;
                }

                CommandHelper.EnsureInitialized();
                UnityLeanMcpPaths.EnsureInitialized();
                UnityLeanMcpOperationStore.EnsureInitialized();
                UnityLeanMcpCompilationTracker.EnsureInitialized();
                UnityLeanMcpDispatcher.EnsureInitialized();
                RoslynCompilerHelper.EnsureInitialized();
                OperationLifecycleRegistry.EnsureInitialized();
                EnsureDefaultHandlers();
                UnityResultFormatter.EnsureInitialized();

                RecoverOperationsOnDomainLoad();

                // Register callbacks for tests
                RunTestsHandler.RegisterCallbacks();

                // Register lifecycle callbacks before opening the socket. No
                // external connection can be accepted until every dependency
                // and recovery hook is ready.
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting -= OnEditorQuitting;
                EditorApplication.quitting += OnEditorQuitting;

                // Start server only after all main-thread services and
                // callbacks have been initialized.
                StartServer();
                Volatile.Write(ref s_BootstrapState, 2);
            }
            catch
            {
                // Allow Unity's next initialization pass or a controlled test
                // retry to attempt bootstrap again if a dependency fails.
                Volatile.Write(ref s_BootstrapState, 0);
                throw;
            }
        }

        private static void RecoverOperationsOnDomainLoad()
        {
            var operation = UnityLeanMcpOperationStore.Read();
            if (operation == null)
            {
                return;
            }

            bool isRestart = operation.editorSessionId != UnityLeanMcpOperationStore.EditorSessionId;
            OperationLifecycleRegistry.RecoverOnDomainLoad(operation, isRestart);
        }

        private static void StartServer()
        {
            lock (s_ServerLifecycleLock)
            {
                if (_isRunning)
                    return;

                _isRunning = true;
                _shutdownRequested = false;
                _isReloading = false;
                s_ShutdownEvent.Reset();
                _serverThread = new Thread(ServerLoop)
                {
                    IsBackground = true,
                    Name = "UnityLeanMcpServerThread"
                };
                _serverThread.Start();
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            _isReloading = true;
            var operation = UnityLeanMcpOperationStore.Read();
            if (operation != null)
            {
                UnityLeanMcpOperationStore.Update(operation.operationId, OperationStatus.Reloading);
            }
            UnityLeanMcpCompilationTracker.WriteActiveErrorsToFile();
            RunTestsHandler.MarkTransportInterruption(OperationStatus.Reloading);
            StopServer();
        }

        private static void OnEditorQuitting()
        {
            var operation = UnityLeanMcpOperationStore.Read();
            OperationLifecycleRegistry.NotifyQuitting(operation);
            StopServer();
        }

        internal static void StopServer()
        {
            Thread serverThread;
            TcpListener listener;
            lock (s_ServerLifecycleLock)
            {
                _shutdownRequested = true;
                _isRunning = false;
                s_ShutdownEvent.Set();
                serverThread = _serverThread;
                listener = _tcpListener;
            }

            try
            {
                ServerThreadShutdown.StopListenerAndWait(
                    serverThread,
                    () => listener?.Stop());
            }
            finally
            {
                foreach (var client in s_ActiveClients.Keys)
                {
                    try { client.Close(); } catch { }
                }
            }

            lock (s_ServerLifecycleLock)
            {
                if (ReferenceEquals(_tcpListener, listener))
                    _tcpListener = null;

                if (ReferenceEquals(_serverThread, serverThread) &&
                    (serverThread == null || !serverThread.IsAlive))
                {
                    _serverThread = null;
                }
            }

            // This runs only after the server thread has exited, preventing
            // a racing startup path from recreating the endpoint metadata.
            DeletePortFile();

            Debug.Log("UnityLeanMcp: Socket server stopped.");
        }

        private static void ServerLoop()
        {
            TcpListener listener = null;
            try
            {
                int stickyPort = ReadPortFile();
                if (IsShuttingDown())
                {
                    return;
                }

                listener = CreateStartedListener(stickyPort);
                lock (s_ServerLifecycleLock)
                {
                    if (IsShuttingDown())
                    {
                        return;
                    }

                    _tcpListener = listener;
                }

                int port = ((IPEndPoint) listener.LocalEndpoint).Port;

                WritePortFile(port);
                WorkerDiagnosticsLogger.Info(
                    UnityLeanMcpPaths.WorkerLogFile,
                    $"Socket server started on 127.0.0.1:{port}");

                while(_isRunning)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch(SocketException)
                    {
                        // listener stopped
                        break;
                    }
                    catch(ObjectDisposedException)
                    {
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(state => ProcessClient((TcpClient)state), client);
                }
            }
            catch(ThreadAbortException) when (IsShuttingDown())
            {
                // Unity aborts managed threads while reloading its scripting domain.
                // This is a transport interruption, not a command failure.
            }
            catch(Exception e)
            {
                if(!IsShuttingDown())
                {
                    LogUnexpectedException("server loop", e);
                }
            }
            finally
            {
                try
                {
                    listener?.Stop();
                }
                catch (Exception e)
                {
                    WorkerDiagnosticsLogger.Warning(
                        UnityLeanMcpPaths.WorkerLogFile,
                        $"Failed to stop socket listener: {e}");
                }

                lock (s_ServerLifecycleLock)
                {
                    if (ReferenceEquals(_tcpListener, listener))
                        _tcpListener = null;

                    if (ReferenceEquals(_serverThread, Thread.CurrentThread))
                        _serverThread = null;

                    _isRunning = false;
                }

                s_ShutdownEvent.Set();
            }
        }

        private static TcpListener CreateStartedListener(int preferredPort)
        {
            if(preferredPort > AnyAvailablePort)
            {
                try
                {
                    return CreateStartedListenerForPort(preferredPort);
                }
                catch(SocketException e)
                {
                    WorkerDiagnosticsLogger.Warning(
                        UnityLeanMcpPaths.WorkerLogFile,
                        $"Sticky port {preferredPort} is unavailable ({e.SocketErrorCode}); selecting a new port.");
                }
            }

            return CreateStartedListenerForPort(AnyAvailablePort);
        }

        private static TcpListener CreateStartedListenerForPort(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                listener.Start();
                return listener;
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }

        private static void ProcessClient(TcpClient client)
        {
            s_ActiveClients.TryAdd(client, 0);
            if (IsShuttingDown())
            {
                try { client.Close(); } catch { }
                s_ActiveClients.TryRemove(client, out _);
                return;
            }
            try
            {
                client.ReceiveTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.UTF8);
                // We use new UTF8Encoding(false) to disable emitting a UTF-8 Byte Order Mark (BOM).
                // Emitting a BOM (\xEF\xBB\xBF in bytes) is non-standard for sockets and would be prepended
                // to our responses, breaking string comparisons (e.g. [ "$response" = "READY" ]) in the bash script.
                using StreamWriter writer = new(stream, new UTF8Encoding(false));
                writer.AutoFlush = true;

                try
                {
                    string line = reader.ReadLine();
                    if(string.IsNullOrEmpty(line))
                    {
                        writer.WriteLine("ERROR: Empty command");
                        return;
                    }

                    line = line.Trim();
                    string[] parts = line.Split(new[] { ' ' }, 2);
                    string command = parts[0];
                    string payload = parts.Length > 1 ? parts[1].Trim() : "";

                    if (!TryGetHandler(command, out var handler))
                    {
                        writer.WriteLine($"ERROR: Unknown command: {command}");
                        return;
                    }

                    // Reject mutating operations early on the worker thread if another operation is active,
                    // or if Unity is compiling. This prevents main thread deadlock and dispatcher queue pollution
                    // when an operation (e.g. eval) is executing synchronously on the main thread.
                    if (handler.IsMutating)
                    {
                        var activeOp = UnityLeanMcpOperationStore.ReadThreadSafeSnapshot();
                        if (activeOp != null)
                        {
                            string requestOpId = ExtractOperationId(payload);
                            if (string.IsNullOrEmpty(requestOpId) || activeOp.OperationId != requestOpId)
                            {
                                writer.WriteLine($"BUSY {activeOp.Kind} {activeOp.OperationId}");
                                return;
                            }
                        }

                        if (handler.RequiresCompilationSettled)
                        {
                            if (UnityLeanMcpCompilationTracker.IsCompiling ||
                                UnityLeanMcpCompilationTracker.RefreshPending ||
                                UnityLeanMcpCompilationTracker.RefreshRequired)
                            {
                                writer.WriteLine("BUSY compile");
                                return;
                            }
                        }
                    }

                    // Worker thread execution target (e.g. PING, POLL_REFRESH)
                    if (handler.ExecutionTarget == CommandExecutionTarget.WorkerThread)
                    {
                        handler.Handle(payload, writer);
                    }
                    else
                    {
                        using (var finishedEvent = new ManualResetEvent(false))
                        {
                            WaitHandle[] requestWaitHandles = { finishedEvent, s_ShutdownEvent };
                            Exception dispatchException = null;
                            UnityLeanMcpDispatcher.Enqueue(() =>
                            {
                                Action executeAction = () =>
                                {
                                    try
                                    {
                                        handler.Handle(payload, writer);
                                    }
                                    catch (Exception ex)
                                    {
                                        dispatchException = ex;
                                    }
                                    finally
                                    {
                                        finishedEvent.Set();
                                    }
                                };

                                if (handler.ExecutionTarget == CommandExecutionTarget.EditModeOnly)
                                {
                                    CommandHelper.RunActionAfterStoppingPlaymode(executeAction);
                                }
                                else
                                {
                                    executeAction();
                                }
                            });
                            while (WaitHandle.WaitAny(requestWaitHandles, 100) == WaitHandle.WaitTimeout)
                            {
                                // Keep waiting while Unity is healthy. The
                                // shutdown event wakes this thread immediately
                                // when a reload or editor shutdown begins.
                            }
                            if (s_ShutdownEvent.WaitOne(0) || IsShuttingDown())
                                return;
                            if (dispatchException != null)
                            {
                                throw dispatchException;
                            }
                        }
                    }
                }
                catch(ThreadAbortException) when (IsShuttingDown())
                {
                    // Do not turn Unity's reload/shutdown thread abort into a
                    // protocol-level ERROR response.
                }
                catch (Exception e)
                {
                    if (!IsShuttingDown())
                    {
                        LogUnexpectedException("client request", e);
                        try { writer.WriteLine($"ERROR: {e.Message}"); } catch { }
                    }
                }
            }
            catch(ThreadAbortException) when (IsShuttingDown())
            {
                // See the inner handler: reload aborts are expected transport
                // interruptions and must not be sent to the client.
            }
            catch (Exception e)
            {
                if (!IsShuttingDown())
                {
                    LogUnexpectedException("client connection", e);
                }
            }
            finally
            {
                try { client.Close(); } catch { }
                s_ActiveClients.TryRemove(client, out _);
            }
        }

        private static string ExtractOperationId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            int spaceIndex = payload.IndexOf(' ');
            return spaceIndex > 0 ? payload.Substring(0, spaceIndex).Trim() : payload.Trim();
        }

        private static bool IsShuttingDown()
        {
            return _shutdownRequested || _isReloading || !_isRunning;
        }

        private static void LogUnexpectedException(string context, Exception exception)
        {
            WorkerDiagnosticsLogger.Error(
                UnityLeanMcpPaths.WorkerLogFile,
                $"Unexpected {context} exception. " +
                $"Type={exception.GetType().FullName}, " +
                $"Thread={Thread.CurrentThread.Name ?? "unnamed"}, " +
                $"Reloading={_isReloading}, Exception={exception}");
        }

        private static void WritePortFile(int port)
        {
            try
            {
                string path = UnityLeanMcpPaths.WorkerPortFile;
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                UnityLeanMcpOperationStore.WriteAtomic(path, port.ToString(), "port");
            }
            catch(Exception e)
            {
                WorkerDiagnosticsLogger.Error(
                    UnityLeanMcpPaths.WorkerLogFile,
                    $"Failed to write port file: {e}");
            }
        }

        internal static void DeletePortFile()
        {
            try
            {
                if(File.Exists(UnityLeanMcpPaths.PortFile))
                {
                    File.Delete(UnityLeanMcpPaths.PortFile);
                }
            }
            catch(Exception e)
            {
                Debug.LogWarning($"UnityLeanMcp: Failed to remove port file: {e}");
            }
        }

        private static int ReadPortFile()
        {
            try
            {
                string path = UnityLeanMcpPaths.WorkerPortFile;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return AnyAvailablePort;
                }

                string portText = WorkerThreadSnapshots.ReadFileWithRetry(path);
                return int.TryParse(portText, out int port)
                    ? port
                    : AnyAvailablePort;
            }
            catch(Exception e)
            {
                WorkerDiagnosticsLogger.Warning(
                    UnityLeanMcpPaths.WorkerLogFile,
                    $"Failed to read port file: {e}");
                return AnyAvailablePort;
            }
        }
    }
}
