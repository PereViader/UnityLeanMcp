using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class ReviewRegressionTests
    {
        [Test]
        public void EvalTerminalPayloadSurvivesUnavailableResultDirectory()
        {
            using (var scope = new IsolatedOperation(OperationKinds.Eval))
            {
                string blocker = Path.Combine(scope.Root, "blocked");
                File.WriteAllText(blocker, "directory unavailable");
                scope.SetPath("s_TempDir", blocker);
                string resultPath = Path.Combine(blocker, "result.json");
                bool ignore = LogAssert.ignoreFailingMessages;
                try
                {
                    LogAssert.ignoreFailingMessages = true;
                    OperationExecutionEngine.FinishOperation(scope.Id, OperationKinds.Eval,
                        resultPath, true, "completed", 1.5, "original-payload");
                    Assert.That(UnityLeanMcpOperationStore.IsOwnedBy(scope.Id, OperationKinds.Eval), Is.True);
                    Assert.That(SessionState.GetString("UnityLeanMcp_PendingResult", ""), Does.Contain("original-payload"),
                        "Exact completion must be persisted before any filesystem operation can fail.");
                    File.Delete(blocker);
                    // Reinitialize the real gate as domain reload recovery does.
                    UnityCommandGate.InitializeMainThread();
                    Assert.That(File.Exists(resultPath), Is.True);
                    var result = JsonUtility.FromJson<UnityOperationResult>(File.ReadAllText(resultPath));
                    Assert.That(result.success, Is.True);
                    Assert.That(result.payload, Is.EqualTo("original-payload"));
                    Assert.That(UnityLeanMcpOperationStore.IsOwnedBy(scope.Id, OperationKinds.Eval), Is.False);
                }
                finally { LogAssert.ignoreFailingMessages = ignore; }
            }
        }

        [TestCase("{\"keep\":\"untouched\"}")]
        [TestCase("{\"keep\":\"untouched\",\"mcpServers\":{\"unity-lean-mcp\":{\"command\":\"old } quoted brace\"},\"other\":{\"command\":\"preserved\"}}}")]
        public void InstallerPreservesOtherSettingsAndReplacesOnlyItsServer(string existing)
        {
            string root = Path.Combine(Path.GetTempPath(), "installer-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "mcp.json");
                File.WriteAllText(path, existing);
                UnityLeanMcpInstaller.UpdateOrWriteMcpConfig(path, root, "mcpServers", root);
                string actual = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<InstallerDocument>(actual);
                Assert.That(parsed.keep, Is.EqualTo("untouched"));
                Assert.That(actual, Does.Contain("unity-lean-mcp"));
                Assert.That(actual, Does.Not.Contain("old } quoted brace"));
                if (existing.Contains("preserved"))
                    Assert.That(parsed.mcpServers.other.command, Is.EqualTo("preserved"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void RecreatedTestCallbacksRecoverFailuresFromFinalTree()
        {
            using (var scope = new IsolatedOperation(OperationKinds.Test))
            {
                var state = new UnityTestRunState { runId = scope.Id, failCount = 0, completedTests = 0, totalTests = 2 };
                File.WriteAllText(UnityLeanMcpPaths.TestRunningFile, JsonUtility.ToJson(state));
                RunTestsHandler.ClearCachedRunState();
                // The old callback had received TestFinished, but managed state vanished.
                var leaf = new ResultNode { FullName = "Suite.FailedBeforeReload", Name = "FailedBeforeReload", Message = "assertion detail", StackTrace = "fixture.cs:12" };
                var original = new MyTestCallbacks();
                original.BindRun(scope.Id);
                original.TestFinished(leaf);
                RunTestsHandler.ClearCachedRunState();
                var recovered = new MyTestCallbacks();
                recovered.BindRun(scope.Id);
                recovered.RunFinished(new ResultNode { Children = new[] { new ResultNode { Children = new[] { leaf } } } });
                var result = JsonUtility.FromJson<UnityTestRunResult>(File.ReadAllText(UnityLeanMcpPaths.GetTestResultsFile(scope.Id)));
                Assert.That(result.failCount, Is.EqualTo(1));
                Assert.That(result.failedTests.Count, Is.EqualTo(1));
                Assert.That(result.failedTests[0].fullName, Is.EqualTo(leaf.FullName));
                Assert.That(result.failedTests[0].message, Is.EqualTo(leaf.Message));
                Assert.That(result.failedTests[0].stackTrace, Is.EqualTo(leaf.StackTrace));
            }
        }

        [Serializable] public class InstallerDocument { public string keep; public InstallerServers mcpServers; }
        [Serializable] public class InstallerServers { public InstallerServer other; }
        [Serializable] public class InstallerServer { public string command; }

        private sealed class ResultNode : ITestResultAdaptor
        {
            public ITestAdaptor Test => null;
            public string Name { get; set; } = "Suite";
            public string FullName { get; set; } = "Suite";
            public string ResultState => "Failed";
            public UnityEditor.TestTools.TestRunner.Api.TestStatus TestStatus => UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed;
            public double Duration => 1;
            public DateTime StartTime => default;
            public DateTime EndTime => default;
            public string Message { get; set; } = "";
            public string StackTrace { get; set; } = "";
            public int AssertCount => 1;
            public int FailCount => 1;
            public int PassCount => 0;
            public int SkipCount => 0;
            public int InconclusiveCount => 0;
            public bool HasChildren => Children.Any();
            public IEnumerable<ITestResultAdaptor> Children { get; set; } = new ITestResultAdaptor[0];
            public string Output => "";
            public TNode ToXml() => new TNode("test-result");
        }

        // Tests execute synchronously and restore native + managed ownership and paths.
        // No real listener or TestRunner operation is admitted or cancelled by this fixture.
        private sealed class IsolatedOperation : IDisposable
        {
            private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
            private readonly Dictionary<FieldInfo, object> paths = new Dictionary<FieldInfo, object>();
            private readonly FieldInfo snapshot = typeof(UnityCommandGate).GetField("s_Snapshot", Flags);
            private readonly object previousSnapshot;
            private readonly string previousOwner = SessionState.GetString("UnityLeanMcp_ActiveOp", "");
            private readonly string previousPending = SessionState.GetString("UnityLeanMcp_PendingResult", "");
            private readonly FieldInfo cachedRun = typeof(RunTestsHandler).GetField("s_CachedRunState", Flags);
            private readonly object previousRun;
            private readonly Dictionary<FieldInfo, object> managed = new Dictionary<FieldInfo, object>();
            private readonly FieldInfo updateField = typeof(EditorApplication).GetField("update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            private readonly object previousUpdate;
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "operation-regression-" + Guid.NewGuid().ToString("N"));
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public IsolatedOperation(string kind)
            {
                Directory.CreateDirectory(Root);
                previousSnapshot = snapshot.GetValue(null);
                previousRun = cachedRun.GetValue(null);
                previousUpdate = updateField.GetValue(null);
                foreach (string name in new[] { "s_PendingResult", "s_LastRetryTicks" })
                {
                    var field = typeof(UnityCommandGate).GetField(name, Flags);
                    managed.Add(field, field.GetValue(null));
                    field.SetValue(null, name == "s_LastRetryTicks" ? (object)0L : null);
                }
                foreach (string name in new[] { "s_CurrentTestJobGuid", "s_Callbacks" })
                {
                    var field = typeof(RunTestsHandler).GetField(name, Flags);
                    managed.Add(field, field.GetValue(null));
                    field.SetValue(null, null);
                }
                foreach (var field in typeof(UnityLeanMcpPaths).GetFields(Flags).Where(f => f.FieldType == typeof(string)))
                {
                    paths.Add(field, field.GetValue(null));
                    field.SetValue(null, Path.Combine(Root, field.Name + ".json"));
                }
                SetPath("s_TempDir", Root);
                SetPath("s_ProjectRoot", Root);
                var owner = new ActiveOperationSnapshot(kind, Id);
                snapshot.SetValue(null, owner);
                SessionState.SetString("UnityLeanMcp_ActiveOp", JsonUtility.ToJson(owner));
                SessionState.EraseString("UnityLeanMcp_PendingResult");
            }
            public void SetPath(string name, string value) => typeof(UnityLeanMcpPaths).GetField(name, Flags).SetValue(null, value);
            public void Dispose()
            {
                UnityCommandGate.Complete(Id);
                snapshot.SetValue(null, previousSnapshot);
                cachedRun.SetValue(null, previousRun);
                foreach (var pair in managed) pair.Key.SetValue(null, pair.Value);
                updateField.SetValue(null, previousUpdate);
                SessionState.SetString("UnityLeanMcp_ActiveOp", previousOwner);
                SessionState.SetString("UnityLeanMcp_PendingResult", previousPending);
                foreach (var pair in paths) pair.Key.SetValue(null, pair.Value);
                Directory.Delete(Root, true);
            }
        }
    }
}
