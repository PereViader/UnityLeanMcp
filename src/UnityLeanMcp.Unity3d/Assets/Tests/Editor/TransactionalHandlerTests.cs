using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using Coverage = UnityEngine.TestTools.Coverage;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class TransactionalHandlerTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CompletedOperationReplayDoesNotReenterHandler(bool tests)
        {
            string id = Guid.NewGuid().ToString("N");
            string path = tests ? UnityLeanMcpPaths.GetTestResultsFile(id) : UnityLeanMcpPaths.GetEvalResultFile(id);
            string result = tests ? "{\"runId\":\"" + id + "\",\"success\":true}" : "{\"operationId\":\"" + id + "\",\"success\":true}";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, result);
            bool refreshPending = UnityLeanMcpCompilationTracker.RefreshPending;
            try
            {
                // A retry consumes its completed result even if unrelated compilation
                // started after the original operation finished.
                UnityLeanMcpCompilationTracker.RefreshPending = true;
                using (var stream = new MemoryStream())
                using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                {
                    ICommandHandler handler = tests ? (ICommandHandler)new RunTestsHandler() : new EvalHandler();
                    handler.Handle(id + (tests ? " {\"mode\":\"editmode\"}" : " throw new System.Exception(\"must never run\");"), writer);
                    writer.Flush();
                    Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Does.Contain("RUNNING"));
                    Assert.That(File.ReadAllText(path), Is.EqualTo(result));
                }
            }
            finally
            {
                UnityLeanMcpCompilationTracker.RefreshPending = refreshPending;
                File.Delete(path);
            }
        }

        [Test]
        public void NonOwnedCoverageCleanupLeavesDeveloperProfilerEnabled()
        {
            bool original = Coverage.enabled;
            try
            {
                Coverage.enabled = true;
                RunTestsHandler.RestoreCoverage("not-an-owned-run");
                Assert.That(Coverage.enabled, Is.True);
            }
            finally { Coverage.enabled = original; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoverageInitializationEnablesNativeProfilerBeforeResetAndRestoresPriorSetting(bool originallyEnabled)
        {
            bool original = Coverage.enabled;
            string previousOwner = SessionState.GetString("UnityLeanMcp_CoverageOwner", "");
            string previousSnapshot = SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "");
            string runId = Guid.NewGuid().ToString("N");
            try
            {
                Coverage.enabled = originallyEnabled;
                Assert.DoesNotThrow(() => RunTestsHandler.InitializeCoverage(runId));
                Assert.That(Coverage.enabled, Is.True);
                Assert.That(SessionState.GetString("UnityLeanMcp_CoverageOwner", ""),
                    Is.EqualTo(runId + (originallyEnabled ? ":enabled" : ":disabled")));
                RunTestsHandler.RestoreCoverage(runId);
                Assert.That(Coverage.enabled, Is.EqualTo(originallyEnabled));
                Assert.That(SessionState.GetString("UnityLeanMcp_CoverageOwner", ""), Is.Empty);
            }
            finally
            {
                Coverage.enabled = original;
                SessionState.SetString("UnityLeanMcp_CoverageOwner", previousOwner);
                SessionState.SetString(GetCoverageHandler.CapturedCoverageKey, previousSnapshot);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OwnedCoverageRestoresOriginalFlagOnce(bool originallyEnabled)
        {
            bool original = Coverage.enabled;
            string previousOwner = SessionState.GetString("UnityLeanMcp_CoverageOwner", "");
            string previousSnapshot = SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "");
            try
            {
                SessionState.SetString("UnityLeanMcp_CoverageOwner", "coverage-regression" + (originallyEnabled ? ":enabled" : ":disabled"));
                Coverage.enabled = true;
                RunTestsHandler.RestoreCoverage("coverage-regression");
                Assert.That(Coverage.enabled, Is.EqualTo(originallyEnabled));
                Coverage.enabled = !originallyEnabled;
                RunTestsHandler.RestoreCoverage("coverage-regression");
                Assert.That(Coverage.enabled, Is.EqualTo(!originallyEnabled));
            }
            finally
            {
                Coverage.enabled = original;
                SessionState.SetString("UnityLeanMcp_CoverageOwner", previousOwner);
                SessionState.SetString(GetCoverageHandler.CapturedCoverageKey, previousSnapshot);
            }
        }
    }
}
