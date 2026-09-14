using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    // Unity does not guarantee ordering between InitializeOnLoad types. This
    // deliberately exercises the public registry from a worker while the
    // server's main-thread bootstrap may not have run yet. The probe never
    // blocks Unity's load callback; the test joins it after bootstrap.
    [UnityEditor.InitializeOnLoad]
    internal static class BackgroundFirstServerReferenceProbe
    {
        internal static Exception Failure { get; private set; }
        internal static bool LookupSucceeded { get; private set; }
        private static Thread s_Worker;

        static BackgroundFirstServerReferenceProbe()
        {
            s_Worker = new Thread(() =>
            {
                try
                {
                    var handler = new ManagedProbeHandler();
                    UnityLeanMcpServer.RegisterHandler("BACKGROUND_FIRST_PROBE", handler);
                    LookupSucceeded = UnityLeanMcpServer.TryGetHandler("BACKGROUND_FIRST_PROBE", out var registered)
                        && ReferenceEquals(handler, registered);
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
            });

            s_Worker.Start();
        }

        internal static void WaitForCompletion()
        {
            s_Worker?.Join();
        }
    }

    internal sealed class ManagedProbeHandler : ICommandHandler
    {
        public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.WorkerThread;

        public void Handle(string payload, System.IO.StreamWriter writer)
        {
            writer.WriteLine("PROBE");
        }
    }

    public class UnityLeanMcpServerBootstrapTests
    {
        private static readonly FieldInfo HandlersField = typeof(UnityLeanMcpServer).GetField(
            "s_Handlers", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo DefaultsRegisteredField = typeof(UnityLeanMcpServer).GetField(
            "s_DefaultHandlersRegistered", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo RegisterDefaultHandlersMethod = typeof(UnityLeanMcpServer).GetMethod(
            "RegisterDefaultHandlers", BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void ClearRegistryBeforeTest()
        {
            SetRegistryDefaultsRegistered(false);
            GetHandlers().Clear();
        }

        [TearDown]
        public void RestoreRegistryAfterTest()
        {
            GetHandlers().Clear();
            SetRegistryDefaultsRegistered(false);
            RegisterDefaultHandlersMethod.Invoke(null, null);
            SetRegistryDefaultsRegistered(true);
        }

        [Test]
        public void ExplicitMainThreadBootstrapStartsServerAfterInitialization()
        {
            Assert.That(UnityLeanMcpServer.IsRunning, Is.True);
            Assert.That(UnityLeanMcpServer.TryGetHandler("PING", out var handler), Is.True);
            Assert.That(handler, Is.Not.Null);
        }

        [Test]
        public void HandlerLookupIsSafeFromBackgroundThreadAfterBootstrap()
        {
            Exception failure = null;
            bool found = false;
            Thread worker = new Thread(() =>
            {
                try
                {
                    found = UnityLeanMcpServer.TryGetHandler("PING", out _);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });

            worker.Start();
            worker.Join();

            Assert.That(failure, Is.Null);
            Assert.That(found, Is.True);
        }

        [Test]
        public void BackgroundFirstRegistryReferenceDoesNotPoisonServerType()
        {
            BackgroundFirstServerReferenceProbe.WaitForCompletion();
            Assert.That(BackgroundFirstServerReferenceProbe.Failure, Is.Null);
            Assert.That(BackgroundFirstServerReferenceProbe.LookupSucceeded, Is.True);
        }

        [Test]
        public void TryGetHandlerSeedsBuiltInsBeforeFirstRegistryAccess()
        {
            Assert.That(UnityLeanMcpServer.TryGetHandler("PING", out var handler), Is.True);
            Assert.That(handler, Is.Not.Null);
        }

        [Test]
        public void RegisterHandlerSeedsBuiltInsBeforeRetainingCustomHandler()
        {
            var customHandler = new ManagedProbeHandler();

            UnityLeanMcpServer.RegisterHandler("PRE_BOOTSTRAP_REGISTER", customHandler);

            Assert.That(UnityLeanMcpServer.TryGetHandler("PING", out var builtIn), Is.True);
            Assert.That(builtIn, Is.Not.Null);
            Assert.That(UnityLeanMcpServer.TryGetHandler("PRE_BOOTSTRAP_REGISTER", out var registered), Is.True);
            Assert.That(registered, Is.SameAs(customHandler));

            UnityLeanMcpServer.RegisterHandler("PING", customHandler);
            Assert.That(UnityLeanMcpServer.TryGetHandler("PING", out var overridden), Is.True);
            Assert.That(overridden, Is.SameAs(customHandler));
        }

        [Test]
        public void UnregisterHandlerSeedsBuiltInsBeforeRemovingHandler()
        {
            Assert.That(UnityLeanMcpServer.UnregisterHandler("PING"), Is.True);
            Assert.That(UnityLeanMcpServer.TryGetHandler("PING", out _), Is.False);
        }

        private static ConcurrentDictionary<string, ICommandHandler> GetHandlers()
        {
            return (ConcurrentDictionary<string, ICommandHandler>)HandlersField.GetValue(null);
        }

        private static void SetRegistryDefaultsRegistered(bool value)
        {
            DefaultsRegisteredField.SetValue(null, value);
        }
    }
}
