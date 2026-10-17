using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    internal class ExitHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.MainThread;
        public bool IsMutating => false;
        public bool RequiresCompilationSettled => false;

        private static void ExitUnity()
        {
            UnityLeanMcpServer.StopServer();
            EditorApplication.Exit(0);
        }

        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("EXITING");
            writer.Flush();
            Debug.Log("UnityLeanMcp: Shutdown requested via socket. Exiting immediately.");
            ExitUnity();
        }
    }
}
