using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
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

    }
}
