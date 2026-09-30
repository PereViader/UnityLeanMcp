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

        [TestCase(false)]
        [TestCase(true)]
        public void EditModeAdmissionRechecksOwnershipBeforeChangingPlayMode(bool retryOwnOperation)
        {
            var snapshotField = typeof(UnityCommandGate).GetField("s_Snapshot", BindingFlags.NonPublic | BindingFlags.Static);
            var execute = typeof(UnityLeanMcpServer).GetMethod("ExecuteMainThreadCommand", BindingFlags.NonPublic | BindingFlags.Static);
            var original = snapshotField.GetValue(null);
            var handler = new EditModeProbeHandler();
            using var stream = new System.IO.MemoryStream();
            using var writer = new System.IO.StreamWriter(stream) { AutoFlush = true };
            try
            {
                snapshotField.SetValue(null, new ActiveOperationSnapshot("refresh", "owner"));
                execute.Invoke(null, new object[] { handler, retryOwnOperation ? "owner" : "queued", writer });
                Assert.That(handler.CallCount, Is.EqualTo(retryOwnOperation ? 1 : 0));
                string response = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                Assert.That(response, Does.Contain(retryOwnOperation ? "PROBE" : "BUSY refresh owner"));
            }
            finally
            {
                snapshotField.SetValue(null, original);
            }
        }

        [Test]
        public void RefreshDiagnosticsCaptureOnlyErrorsWithinTheOwningOperation()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var snapshot = typeof(UnityCommandGate).GetField("s_Snapshot", flags);
            var scoped = typeof(UnityLeanMcpCompilationTracker).GetField("s_ScopedDiagnostics", flags);
            var capture = typeof(UnityLeanMcpCompilationTracker).GetMethod("CaptureOperationError", flags);
            var read = typeof(UnityLeanMcpCompilationTracker).GetMethod("GetCapturedDiagnosticsSnapshot", flags);
            object originalSnapshot = snapshot.GetValue(null), originalScoped = scoped.GetValue(null);
            string historical = Guid.NewGuid().ToString(), informational = Guid.NewGuid().ToString(), current = Guid.NewGuid().ToString();
            try
            {
                snapshot.SetValue(null, null);
                capture.Invoke(null, new object[] { historical, "", UnityEngine.LogType.Error });
                snapshot.SetValue(null, new ActiveOperationSnapshot("refresh", Guid.NewGuid().ToString("N")));
                capture.Invoke(null, new object[] { "AssetPostprocessor " + informational, "", UnityEngine.LogType.Log });
                capture.Invoke(null, new object[] { current, "", UnityEngine.LogType.Error });
                var captured = (System.Collections.Generic.List<string>)read.Invoke(null, null);
                Assert.That(captured.Exists(value => value.Contains(historical)), Is.False);
                Assert.That(captured.Exists(value => value.Contains(informational)), Is.False);
                Assert.That(captured.Exists(value => value.Contains(current)), Is.True);
                snapshot.SetValue(null, new ActiveOperationSnapshot("refresh", Guid.NewGuid().ToString("N")));
                captured = (System.Collections.Generic.List<string>)read.Invoke(null, null);
                Assert.That(captured.Exists(value => value.Contains(current)), Is.False);
            }
            finally
            {
                snapshot.SetValue(null, originalSnapshot);
                scoped.SetValue(null, originalScoped);
            }
        }

        [Test]
        public void NewRefreshRetainsCompilerDiagnosticsUntilAssemblyCompilerUpdatesThem()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var tracker = typeof(UnityLeanMcpCompilationTracker);
            var assemblies = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>)tracker.GetField("s_AssemblyDiagnostics", flags).GetValue(null);
            var scoped = tracker.GetField("s_ScopedDiagnostics", flags);
            object originalScoped = scoped.GetValue(null);
            string originalSession = UnityEditor.SessionState.GetString("UnityLeanMcp_RefreshDiagnostics", "");
            string assembly = "test-assembly-" + Guid.NewGuid().ToString("N");
            const string diagnostic = "Assets/Fixture.cs(1,1): error CS1002: ; expected";
            try
            {
                assemblies[assembly] = new System.Collections.Generic.List<string> { diagnostic };
                tracker.GetMethod("ClearCapturedDiagnostics", flags).Invoke(null, null);
                var captured = (System.Collections.Generic.List<string>)tracker.GetMethod("GetCapturedDiagnosticsSnapshot", flags).Invoke(null, null);
                Assert.That(captured, Does.Contain(diagnostic));
                tracker.GetMethod("OnAssemblyCompilationFinished", flags).Invoke(null, new object[] { assembly, new UnityEditor.Compilation.CompilerMessage[0] });
                Assert.That(assemblies.ContainsKey(assembly), Is.False);
            }
            finally
            {
                assemblies.Remove(assembly);
                scoped.SetValue(null, originalScoped);
                UnityEditor.SessionState.SetString("UnityLeanMcp_RefreshDiagnostics", originalSession);
            }
        }

        private sealed class EditModeProbeHandler : ICommandHandler
        {
            public CommandExecutionTarget ExecutionTarget => CommandExecutionTarget.EditModeOnly;
            public int CallCount { get; private set; }
            public void Handle(string payload, System.IO.StreamWriter writer)
            {
                CallCount++;
                writer.WriteLine("PROBE");
            }
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
