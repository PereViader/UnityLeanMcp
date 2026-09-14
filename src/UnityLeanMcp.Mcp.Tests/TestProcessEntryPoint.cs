using System.Threading;

namespace UnityLeanMcp.Mcp.Tests;

internal static class TestProcessEntryPoint
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--unity-lean-mcp-dummy-process")
        {
            using var waitHandle = new ManualResetEventSlim(false);
            waitHandle.Wait();
        }
    }
}
