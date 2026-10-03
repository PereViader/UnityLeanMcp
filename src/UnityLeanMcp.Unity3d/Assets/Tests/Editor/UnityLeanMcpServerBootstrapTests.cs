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

        [Test]
        public void FormatOperationDiagnostic_ShaderErrors_FormattedWithLocation()
        {
            // Trailing shader location
            string msg1 = "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45)";
            string formatted1 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg1, "", true);
            Assert.That(formatted1, Is.EqualTo("Assets/Shaders/Water.shader(45): error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45)"));

            // Leading shader location
            string msg2 = "Assets/Shaders/Water.shader(45): error undeclared identifier 'foo'";
            string formatted2 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg2, "", true);
            Assert.That(formatted2, Is.EqualTo("Assets/Shaders/Water.shader(45): error undeclared identifier 'foo'"));
        }

        [Test]
        public void FormatOperationDiagnostic_ShaderErrors_WithColumnsAndColonDelimiters()
        {
            // Trailing shader location with column in parenthesis
            string msg1 = "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45,12)";
            string formatted1 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg1, "", true);
            Assert.That(formatted1, Is.EqualTo("Assets/Shaders/Water.shader(45,12): error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45,12)"));

            // Trailing shader location with colon delimiter and column
            string msg2 = "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader:45:12";
            string formatted2 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg2, "", true);
            Assert.That(formatted2, Is.EqualTo("Assets/Shaders/Water.shader(45,12): error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader:45:12"));

            // Trailing shader location colon-delimited without column
            string msg3 = "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader:45";
            string formatted3 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg3, "", true);
            Assert.That(formatted3, Is.EqualTo("Assets/Shaders/Water.shader(45): error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader:45"));
        }

        [Test]
        public void IsErrorRegex_MatchesWindowsPathsAndStandaloneErrors()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var isErrorRegexField = typeof(UnityLeanMcpCompilationTracker).GetField("s_IsErrorRegex", flags);
            var regex = (System.Text.RegularExpressions.Regex)isErrorRegexField.GetValue(null);

            // Windows absolute paths
            Assert.That(regex.IsMatch(@"C:\Project\Assets\Scripts\Player.cs(10,5): error CS0103: The name 'foo' does not exist"), Is.True);
            Assert.That(regex.IsMatch(@"C:/Project/Assets/Shaders/Water.shader:45:12: error undeclared identifier 'foo'"), Is.True);
            Assert.That(regex.IsMatch(@"C:\Project\Assets\Prefabs\Player.prefab: error Missing script"), Is.True);

            // Relative paths and Unix paths
            Assert.That(regex.IsMatch("Assets/Shaders/Water.shader(45,12): error undeclared identifier 'foo'"), Is.True);
            Assert.That(regex.IsMatch("/Users/dev/Project/Assets/Player.cs:10: error CS0103: foo"), Is.True);

            // Standalone errors
            Assert.That(regex.IsMatch("error: The referenced script on this Behaviour is missing!"), Is.True);
            Assert.That(regex.IsMatch("error CS0103: The name 'foo' does not exist"), Is.True);

            // Non-errors (warnings, even if mentioning error in body)
            Assert.That(regex.IsMatch(@"C:\Project\Assets\Scripts\Player.cs(10,5): warning CS0168: The variable 'unused' is declared but never used"), Is.False);
            Assert.That(regex.IsMatch("Assets/Scripts/Player.cs(10,5): warning CS1234: Syntax error recovery used"), Is.False);
            Assert.That(regex.IsMatch("warning: error handling was bypassed"), Is.False);
            Assert.That(regex.IsMatch("warning CS1234: error in configuration"), Is.False);
            Assert.That(regex.IsMatch("Assets/Scripts/Player.cs: warning: error in configuration"), Is.False);
        }

        [Test]
        public void FormatOperationDiagnostic_MissingMonoBehaviourScript_FormattedCorrectly()
        {
            // Standalone without file
            string msg1 = "The referenced script on this Behaviour (Game Object 'Player') is missing!";
            string formatted1 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg1, "", true);
            Assert.That(formatted1, Is.EqualTo("error: The referenced script on this Behaviour (Game Object 'Player') is missing!"));

            // With prefab path in quotes
            string msg2 = "The referenced script on this Behaviour (Game Object 'Player') in prefab 'Assets/Prefabs/Player.prefab' is missing!";
            string formatted2 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg2, "", true);
            Assert.That(formatted2, Is.EqualTo("Assets/Prefabs/Player.prefab: error The referenced script on this Behaviour (Game Object 'Player') in prefab 'Assets/Prefabs/Player.prefab' is missing!"));
        }

        [Test]
        public void FormatOperationDiagnostic_AssetImportFailure_FormattedWithAssetPath()
        {
            string msg = "Asset import failed: \"Assets/Textures/bad.png\"";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Textures/bad.png: error Asset import failed: \"Assets/Textures/bad.png\""));
        }

        [Test]
        public void FormatOperationDiagnostic_AssetPostprocessorException_ExtractsSourceFileAndLine()
        {
            string msg = "System.NullReferenceException: Object reference not set to an instance of an object";
            string trace = "  at MyAssetPostprocessor.OnPostprocessAllAssets (System.String[] importedAssets) [0x00001] in Assets/Editor/MyAssetPostprocessor.cs:15\n  at UnityEditor.AssetPostprocessingInternal.PostprocessAllAssets ()";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace, true);
            Assert.That(formatted, Is.EqualTo("Assets/Editor/MyAssetPostprocessor.cs(15): error System.NullReferenceException: Object reference not set to an instance of an object"));
        }

        [Test]
        public void FormatOperationDiagnostic_ShaderError_WithConflictingLeadingAndTrailingMarkers_ExtractsLineAndColumn()
        {
            string msg = "Assets/Shaders/Water.shader: Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45,12)";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Shaders/Water.shader(45,12): error Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45,12)"));
        }

        [Test]
        public void FormatOperationDiagnostic_PathWithParenthesesAndSpaces_ExtractsLocationCorrectly()
        {
            string msg = "Assets/Plugins (x86)/Plugin.cs(10, 5): error CS0103: The name 'foo' does not exist";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Plugins (x86)/Plugin.cs(10,5): error CS0103: The name 'foo' does not exist"));
        }

        [Test]
        public void FormatOperationDiagnostic_UnquotedAssetImportFailure_FormattedWithAssetPath()
        {
            string msg = "Asset import failed: Assets/Textures/bad.png";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Textures/bad.png: error Asset import failed: Assets/Textures/bad.png"));
        }

        [Test]
        public void FormatOperationDiagnostic_SentenceEndingWithPunctuation_ExtractsAssetPath()
        {
            string msg = "Asset import failed: Assets/Textures/bad.png. Please check header.";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Textures/bad.png: error Asset import failed: Assets/Textures/bad.png. Please check header."));
        }

        [Test]
        public void FormatOperationDiagnostic_EnglishSentenceMentioningAsset_ExtractsCleanAssetPath()
        {
            string msg = "Unhandled exception while importing Assets/Prefabs/Player.prefab: NullReferenceException";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, "", true);
            Assert.That(formatted, Is.EqualTo("Assets/Prefabs/Player.prefab: error Unhandled exception while importing Assets/Prefabs/Player.prefab: NullReferenceException"));
        }

        [Test]
        public void FormatOperationDiagnostic_StackTraceWithParenthesesInPath_ExtractsSourceFileAndLine()
        {
            string msg = "System.Exception: failed";
            string trace = "  at Tool.Run () in C:\\Build (x64)\\Assets\\Editor\\Tool.cs:line 30";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace, true);
            Assert.That(formatted, Is.EqualTo("C:/Build (x64)/Assets/Editor/Tool.cs(30): error System.Exception: failed"));
        }

        [Test]
        public void FormatOperationDiagnostic_StackTraceWithRelativePathContainingSpacesAndParentheses()
        {
            string msg = "System.Exception: failed";
            string trace = "  at Tool.Run () in Plugins (x86)/Tool.cs:line 30";
            string formatted = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace, true);
            Assert.That(formatted, Is.EqualTo("Plugins (x86)/Tool.cs(30): error System.Exception: failed"));

            string trace2 = "  at Tool.Run () in My Folder/Tool.cs:line 42";
            string formatted2 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace2, true);
            Assert.That(formatted2, Is.EqualTo("My Folder/Tool.cs(42): error System.Exception: failed"));

            string trace3 = "  at Tool.Run (in int value) in Plugins (x86)/Tool.cs:line 30";
            string formatted3 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace3, true);
            Assert.That(formatted3, Is.EqualTo("Plugins (x86)/Tool.cs(30): error System.Exception: failed"));

            string trace4 = "  at Plugins (x86)/Tool.cs:line 30";
            string formatted4 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace4, true);
            Assert.That(formatted4, Is.EqualTo("Plugins (x86)/Tool.cs(30): error System.Exception: failed"));

            string trace5 = "  at My Folder/Tool.cs:42";
            string formatted5 = UnityLeanMcpCompilationTracker.FormatOperationDiagnostic(msg, trace5, true);
            Assert.That(formatted5, Is.EqualTo("My Folder/Tool.cs(42): error System.Exception: failed"));
        }

        [Test]
        public void ScopedDiagnostics_PersistsAcrossSimulatedDomainReload()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var snapshot = typeof(UnityCommandGate).GetField("s_Snapshot", flags);
            var scoped = typeof(UnityLeanMcpCompilationTracker).GetField("s_ScopedDiagnostics", flags);
            var read = typeof(UnityLeanMcpCompilationTracker).GetMethod("GetCapturedDiagnosticsSnapshot", flags);
            var init = typeof(UnityLeanMcpCompilationTracker).GetMethod("InitializeMainThread", flags);
            object originalSnapshot = snapshot.GetValue(null);
            object originalScoped = scoped.GetValue(null);
            string originalActiveOp = UnityEditor.SessionState.GetString("UnityLeanMcp_ActiveOp", "");
            string originalSession = UnityEditor.SessionState.GetString("UnityLeanMcp_RefreshDiagnostics", "");
            string opId = Guid.NewGuid().ToString("N");

            try
            {
                var activeOp = new ActiveOperationSnapshot("refresh", opId, OperationStatus.Refreshing);
                UnityEditor.SessionState.SetString("UnityLeanMcp_ActiveOp", UnityEngine.JsonUtility.ToJson(activeOp));
                snapshot.SetValue(null, activeOp);
                UnityLeanMcpCompilationTracker.CaptureOperationError(
                    "Shader error in 'Custom/Water': undeclared identifier 'foo' at Assets/Shaders/Water.shader(45)",
                    "",
                    UnityEngine.LogType.Error);

                // Verify captured in memory
                var capturedBefore = (System.Collections.Generic.List<string>)read.Invoke(null, null);
                Assert.That(capturedBefore.Count, Is.EqualTo(1));
                Assert.That(capturedBefore[0], Does.Contain("Assets/Shaders/Water.shader(45)"));

                // Verify persisted to SessionState
                string persistedJson = UnityEditor.SessionState.GetString("UnityLeanMcp_RefreshDiagnostics", "");
                Assert.That(persistedJson, Does.Contain(opId));
                Assert.That(persistedJson, Does.Contain("Assets/Shaders/Water.shader(45)"));

                // Simulate domain reload: clear in-memory static state and run InitializeMainThread
                scoped.SetValue(null, Activator.CreateInstance(scoped.FieldType));
                var clearedList = (System.Collections.Generic.List<string>)read.Invoke(null, null);
                Assert.That(clearedList.Count, Is.EqualTo(0));

                init.Invoke(null, null);

                // Verify restored from SessionState
                var restoredList = (System.Collections.Generic.List<string>)read.Invoke(null, null);
                Assert.That(restoredList.Count, Is.EqualTo(1));
                Assert.That(restoredList[0], Does.Contain("Assets/Shaders/Water.shader(45)"));
            }
            finally
            {
                snapshot.SetValue(null, originalSnapshot);
                scoped.SetValue(null, originalScoped);
                UnityEditor.SessionState.SetString("UnityLeanMcp_RefreshDiagnostics", originalSession);
                if (string.IsNullOrEmpty(originalActiveOp))
                    UnityEditor.SessionState.EraseString("UnityLeanMcp_ActiveOp");
                else
                    UnityEditor.SessionState.SetString("UnityLeanMcp_ActiveOp", originalActiveOp);
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
