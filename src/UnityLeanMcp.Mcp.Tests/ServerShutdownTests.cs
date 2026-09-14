using System.Threading;

namespace UnityLeanMcp.Mcp.Tests;

public sealed class ServerShutdownTests
{
    [Fact]
    public void StopListenerAndWait_StopsListenerBeforeReturningAfterBlockedLoopExits()
    {
        using var loopStarted = new ManualResetEventSlim();
        using var releaseLoop = new ManualResetEventSlim();
        bool listenerStopped = false;
        bool loopExited = false;

        var serverThread = new Thread(() =>
        {
            loopStarted.Set();
            releaseLoop.Wait();
            loopExited = true;
        });
        serverThread.Start();
        loopStarted.Wait();

        ServerThreadShutdown.StopListenerAndWait(serverThread, () =>
        {
            listenerStopped = true;
            releaseLoop.Set();
        });

        Assert.True(listenerStopped);
        Assert.True(loopExited);
        Assert.False(serverThread.IsAlive);
    }

    [Fact]
    public void StopListenerAndWait_PropagatesListenerCloseFailure()
    {
        using var releaseLoop = new ManualResetEventSlim();
        var serverThread = new Thread(releaseLoop.Wait);
        serverThread.Start();

        Assert.Throws<InvalidOperationException>(() =>
            ServerThreadShutdown.StopListenerAndWait(
                serverThread,
                () => throw new InvalidOperationException("listener close failed")));

        releaseLoop.Set();
        serverThread.Join();
    }
}
