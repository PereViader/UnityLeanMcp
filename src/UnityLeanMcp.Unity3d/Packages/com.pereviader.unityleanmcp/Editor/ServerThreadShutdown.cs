using System;
using System.Threading;

namespace UnityLeanMcp
{
    internal static class ServerThreadShutdown
    {
        public static void StopListenerAndWait(Thread serverThread, Action stopListener)
        {
            stopListener?.Invoke();

            if (serverThread == null || ReferenceEquals(Thread.CurrentThread, serverThread))
            {
                return;
            }

            // Closing the listener wakes AcceptTcpClient. Cleanup must not
            // proceed until the server thread has released its endpoint.
            serverThread.Join();
        }
    }
}
