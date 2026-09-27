using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class GetCoverageHandlerTests
    {
        private const string DummyProjectRoot = "C:/Code/MyUnityProject/";

        [TestCase(".", "")]
        [TestCase("./", "")]
        [TestCase("Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("Assets\\Scripts\\Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("/Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("C:/Code/MyUnityProject/Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("c:/code/myunityproject/Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        [TestCase("./Assets/Scripts/Player.cs", "Assets/Scripts/Player.cs")]
        public void NormalizePathRelativeToProject_HandlesVariousPathShapes(string input, string expected)
        {
            string actual = GetCoverageHandler.NormalizePathRelativeToProject(input, DummyProjectRoot);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void MatchesAnyFilter_WhenFilterIsEmptyOrRoot_MatchesAllProjectFiles()
        {
            var filters = new List<string> { "" };
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Player.cs", filters), Is.True);
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Packages/com.foo/Runtime/Bar.cs", filters), Is.True);

            var rootSlashFilters = new List<string> { "/" };
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Player.cs", rootSlashFilters), Is.True);
        }

        [Test]
        public void MatchesAnyFilter_WhenDirectoryFilter_MatchesSubdirectoriesOnly()
        {
            var filters = new List<string> { "Assets/Scripts/" };
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Player.cs", filters), Is.True);
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Combat/Sword.cs", filters), Is.True);
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/ScriptsOther/Enemy.cs", filters), Is.False);
        }

        [Test]
        public void MatchesAnyFilter_WhenExactFileFilter_MatchesExactFileOnly()
        {
            var filters = new List<string> { "Assets/Scripts/Player.cs" };
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Player.cs", filters), Is.True);
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Player.cs.bak", filters), Is.False);
            Assert.That(GetCoverageHandler.MatchesAnyFilter("Assets/Scripts/Other.cs", filters), Is.False);
        }

        [Test]
        public void TryResolveExistingPath_WhenLeadingSlashProvided_ResolvesRelativeToProjectRoot()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "unity_cov_resolve_" + Guid.NewGuid().ToString("N"));
            string assetsDir = Path.Combine(tempDir, "Assets");
            Directory.CreateDirectory(assetsDir);
            string filePath = Path.Combine(assetsDir, "TestScript.cs");
            File.WriteAllText(filePath, "// test");

            try
            {
                string normProjectRoot = tempDir.Replace('\\', '/').TrimEnd('/') + "/";

                // Relative without leading slash
                bool found1 = GetCoverageHandler.TryResolveExistingPath("Assets/TestScript.cs", normProjectRoot, out string full1);
                Assert.That(found1, Is.True);
                Assert.That(File.Exists(full1), Is.True);

                // Relative with leading slash
                bool found2 = GetCoverageHandler.TryResolveExistingPath("/Assets/TestScript.cs", normProjectRoot, out string full2);
                Assert.That(found2, Is.True);
                Assert.That(File.Exists(full2), Is.True);

                // Current directory
                bool found3 = GetCoverageHandler.TryResolveExistingPath(".", normProjectRoot, out string full3);
                Assert.That(found3, Is.True);
                Assert.That(Directory.Exists(full3), Is.True);

                // Non-existent path
                bool found4 = GetCoverageHandler.TryResolveExistingPath("Assets/NonExistent.cs", normProjectRoot, out _);
                Assert.That(found4, Is.False);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
