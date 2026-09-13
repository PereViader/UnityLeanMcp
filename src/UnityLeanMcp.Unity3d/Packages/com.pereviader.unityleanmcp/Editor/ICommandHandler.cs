using System.IO;

namespace UnityLeanMcp
{
    public enum CommandExecutionTarget
    {
        WorkerThread,
        MainThread,
        EditModeOnly
    }

    public interface ICommandHandler
    {
        CommandExecutionTarget ExecutionTarget { get; }
        bool IsMutating => false;
        bool RequiresCompilationSettled => false;
        void Handle(string payload, StreamWriter writer);
    }
}
