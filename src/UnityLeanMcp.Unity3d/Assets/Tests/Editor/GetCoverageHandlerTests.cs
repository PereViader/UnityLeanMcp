using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityLeanMcp;
using Coverage = UnityEngine.TestTools.Coverage;

namespace UnityLeanMcpTests
{
    public class GetCoverageHandlerTests
    {
        private const string DummyProjectRoot = "C:/Code/MyUnityProject/";

        [TestCase(false)]
        [TestCase(true)]
        public void QueryAfterRecordingPreservesCapturedHitsAndProfilerSetting(bool originallyEnabled)
        {
            bool original = Coverage.enabled;
            string previousOwner = UnityEditor.SessionState.GetString("UnityLeanMcp_CoverageOwner", "");
            string previousSnapshot = UnityEditor.SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "");
            string runId = Guid.NewGuid().ToString("N");
            string projectRoot = UnityLeanMcpPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            var paths = new List<string> { Path.Combine(projectRoot, "Assets/Tests/Editor/GetCoverageHandlerTests.cs") };
            var method = typeof(GetCoverageHandlerTests).GetMethod(nameof(CoverageQuerySample));
            try
            {
                Coverage.enabled = originallyEnabled;
                RunTestsHandler.InitializeCoverage(runId);
                Assert.That(CoverageQuerySample(10), Is.EqualTo(11));
                var recordedPoints = Coverage.GetSequencePointsFor(method).Where(p => p.line > 0 && p.line != 0xfeefee).ToArray();
                Assert.That(recordedPoints.All(p => p.hitCount > 0), Is.True, "Fixture must record real native coverage.");
                Assert.That(recordedPoints, Is.Not.Empty);
                RunTestsHandler.RestoreCoverage(runId);
                Assert.That(Coverage.enabled, Is.EqualTo(originallyEnabled));
                string capturedJson = UnityEditor.SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, "");
                Assert.That(capturedJson, Is.Not.Empty, "Completed coverage must survive a managed domain reload.");

                // Remove all coverage state, then restore only the native session
                // payload that survives reload. Query must not rely on a static cache.
                GetCoverageHandler.ClearCapturedCoverage();
                UnityEditor.SessionState.SetString(GetCoverageHandler.CapturedCoverageKey, capturedJson);

                // The completed report must survive destruction of native counters.
                Coverage.enabled = true;
                Coverage.ResetAll();
                // A publication retry after reload must not overwrite the persisted
                // result with freshly reset native counters for the same run.
                GetCoverageHandler.CaptureCoverage(runId);
                Assert.That(UnityEditor.SessionState.GetString(GetCoverageHandler.CapturedCoverageKey, ""), Is.EqualTo(capturedJson));
                Coverage.enabled = originallyEnabled;

                using (var stream = new MemoryStream())
                using (var writer = new StreamWriter(stream))
                {
                    GetCoverageHandler.WriteCoverage(paths, projectRoot, writer);
                    writer.Flush();
                    Assert.That(Coverage.enabled, Is.EqualTo(originallyEnabled));
                    string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');
                    Assert.That(response, Does.StartWith("SUCCESS "));
                    var report = UnityEngine.JsonUtility.FromJson<CoverageResponsePayload>(
                        ProtocolCodec.UnescapeLine(response.Substring("SUCCESS ".Length).TrimEnd()));
                    Assert.That(report.files.Single().coveredPoints, Is.GreaterThan(0));
                    foreach (var point in recordedPoints)
                        Assert.That(report.files.Single().uncoveredLines.Contains((int)point.line), Is.False);
                }
            }
            finally
            {
                Coverage.enabled = original;
                UnityEditor.SessionState.SetString("UnityLeanMcp_CoverageOwner", previousOwner);
                UnityEditor.SessionState.SetString(GetCoverageHandler.CapturedCoverageKey, previousSnapshot);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedCoverageQueryRestoresProfilerSetting(bool originallyEnabled)
        {
            bool original = Coverage.enabled;
            try
            {
                Coverage.enabled = originallyEnabled;
                using (var stream = new MemoryStream())
                using (var writer = new FailingCoverageWriter(stream))
                {
                    Assert.Throws<IOException>(() => GetCoverageHandler.WriteCoverage(
                        new List<string> { "Assets/Tests/Editor/GetCoverageHandlerTests.cs" },
                        UnityLeanMcpPaths.ProjectRoot, writer));
                    Assert.That(Coverage.enabled, Is.EqualTo(originallyEnabled));
                }
            }
            finally { Coverage.enabled = original; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int CoverageQuerySample(int value) => value + 1;

        private sealed class FailingCoverageWriter : StreamWriter
        {
            public FailingCoverageWriter(Stream stream) : base(stream) { }
            public override void WriteLine(string value) => throw new IOException("Injected coverage output failure.");
        }

        [TestCase(".", "")]
        [TestCase("./", "")]
        [TestCase("C:/Code/MyUnityProject", "")]
        [TestCase("C:/Code/MyUnityProject/", "")]
        [TestCase("C:\\Code\\MyUnityProject", "")]
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

        [TestCase("Assets/Child/../Player.cs", "Assets/Player.cs")]
        [TestCase("Assets/Child/..", "Assets/Player.cs")]
        public void ExistingPathWithDotSegmentsMatchesCanonicalSource(string requested, string source)
        {
            string root = Path.Combine(Path.GetTempPath(), "coverage-canonical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Child"));
            File.WriteAllText(Path.Combine(root, "Assets", "Player.cs"), "// fixture");
            try
            {
                Assert.That(GetCoverageHandler.TryResolveExistingPath(requested, root, out _), Is.True);
                string filter = GetCoverageHandler.NormalizePathRelativeToProject(requested, root);
                if (!filter.EndsWith(".cs")) filter += "/";
                Assert.That(GetCoverageHandler.MatchesAnyFilter(source, new List<string> { filter }), Is.True);
                Assert.That(GetCoverageHandler.MatchesAnyFilter("AssetsOther/Player.cs", new List<string> { filter }), Is.False);
            }
            finally { Directory.Delete(root, true); }
        }

        [TestCase("2021.3.15f1", false)]
        [TestCase("2022.3.20f1", false)]
        [TestCase("6000.0.1f1", false)]
        [TestCase("6000.6.9f1", false)]
        [TestCase("6000.7.0f1", true)]
        [TestCase("6000.8.0f1", true)]
        [TestCase("7000.0.0f1", true)]
        public void IsPlayModeCoverageSupported_EvaluatesUnityVersionsCorrectly(string version, bool expected)
        {
            Assert.That(RunTestsHandler.IsPlayModeCoverageSupported(version), Is.EqualTo(expected));
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

        [Test]
        public void GetDeclaredMethodsAndConstructors_WhenTypeHasStaticAndInstanceConstructors_CapturesEachExactlyOnce()
        {
            var methods = GetCoverageHandler.GetDeclaredMethodsAndConstructors(typeof(TypeWithStaticAndInstanceCtors));

            // Verify no duplicates
            Assert.That(methods.Count, Is.EqualTo(new HashSet<System.Reflection.MethodBase>(methods).Count));

            // Verify static constructor (.cctor) is captured exactly once
            Assert.That(methods.FindAll(m => m.Name == ".cctor").Count, Is.EqualTo(1));

            // Verify instance constructor (.ctor) is captured exactly once
            Assert.That(methods.FindAll(m => m.Name == ".ctor").Count, Is.EqualTo(1));

            // Verify normal instance and static methods are captured
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithStaticAndInstanceCtors.InstanceMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithStaticAndInstanceCtors.StaticMethod)), Is.True);
        }

        [Test]
        public void GetDeclaredMethodsAndConstructors_WhenTypeHasNoStaticConstructor_DoesNotEmitCctor()
        {
            var methods = GetCoverageHandler.GetDeclaredMethodsAndConstructors(typeof(TypeWithOnlyInstanceCtor));

            Assert.That(methods.Count, Is.EqualTo(new HashSet<System.Reflection.MethodBase>(methods).Count));
            Assert.That(methods.FindAll(m => m.Name == ".cctor").Count, Is.EqualTo(0));
            Assert.That(methods.FindAll(m => m.Name == ".ctor").Count, Is.EqualTo(1));
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_AsyncMethods_DiscoversKickOffAndStateMachines()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithAsyncMethods));

            // Verify kick-off methods exist
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithAsyncMethods.AsyncVoidMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithAsyncMethods.AsyncTaskMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithAsyncMethods.AsyncAwaitableMethod)), Is.True);

            // Verify compiler-generated async state machine MoveNext methods are discovered
            var moveNextMethods = methods.FindAll(m => m.Name == "MoveNext");
            Assert.That(moveNextMethods.Count, Is.GreaterThanOrEqualTo(3));

            // Verify state machines are declared on compiler-generated nested types
            Assert.That(moveNextMethods.TrueForAll(m => m.DeclaringType.IsNested), Is.True);
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_Iterators_DiscoversKickOffAndIteratorTypes()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithIterators));

            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithIterators.YieldEnumeratorMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithIterators.YieldEnumerableMethod)), Is.True);

            var moveNextMethods = methods.FindAll(m => m.Name == "MoveNext");
            Assert.That(moveNextMethods.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(moveNextMethods.TrueForAll(m => m.DeclaringType.IsNested), Is.True);
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_LambdasAndClosures_DiscoversNestedAndNestedNestedLambdas()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithLambdas));

            // Declaring methods
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithLambdas.SimpleLambdaMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithLambdas.LambdaWithClosureMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithLambdas.NestedLambdaMethod)), Is.True);
            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithLambdas.NestedNestedLambdaMethod)), Is.True);

            // Compiler-generated lambda methods (contain "b__")
            var lambdaMethods = methods.FindAll(m => m.Name.Contains("b__"));
            // At least: 1 simple + 1 closure + 2 nested (outer+inner) + 3 nested-nested (l1+l2+l3) = 7 lambdas
            Assert.That(lambdaMethods.Count, Is.GreaterThanOrEqualTo(7));
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_PropertiesAndEvents_DiscoversAllAccessors()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithPropertiesAndEvents));

            // Auto property accessors
            Assert.That(methods.Exists(m => m.Name == "get_AutoProperty"), Is.True);
            Assert.That(methods.Exists(m => m.Name == "set_AutoProperty"), Is.True);

            // Manual property accessors
            Assert.That(methods.Exists(m => m.Name == "get_ManualProperty"), Is.True);
            Assert.That(methods.Exists(m => m.Name == "set_ManualProperty"), Is.True);

            // Indexer accessors
            Assert.That(methods.Exists(m => m.Name == "get_Item"), Is.True);
            Assert.That(methods.Exists(m => m.Name == "set_Item"), Is.True);

            // Auto event accessors
            Assert.That(methods.Exists(m => m.Name == "add_AutoEvent"), Is.True);
            Assert.That(methods.Exists(m => m.Name == "remove_AutoEvent"), Is.True);

            // Custom event accessors
            Assert.That(methods.Exists(m => m.Name == "add_CustomEvent"), Is.True);
            Assert.That(methods.Exists(m => m.Name == "remove_CustomEvent"), Is.True);
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_LocalFunctions_DiscoversStaticRegularAsyncAndIteratorLocalFunctions()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithLocalFunctions));

            Assert.That(methods.Exists(m => m.Name == nameof(TypeWithLocalFunctions.HostMethod)), Is.True);

            // Compiler-generated local function methods contain "g__"
            var localFunctions = methods.FindAll(m => m.Name.Contains("g__"));
            Assert.That(localFunctions.Count, Is.GreaterThanOrEqualTo(4));

            // Async and iterator local functions also generate state machine MoveNext
            var moveNextMethods = methods.FindAll(m => m.Name == "MoveNext");
            Assert.That(moveNextMethods.Count, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_ExplicitInterfaceImplementation_DiscoversExplicitMembers()
        {
            var methods = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(TypeWithExplicitInterface));

            // Explicit methods are private and qualified with interface name
            Assert.That(methods.Exists(m => m.Name.EndsWith("ITestCoverageInterface.ExplicitMethod")), Is.True);
            Assert.That(methods.Exists(m => m.Name.EndsWith("ITestCoverageInterface.get_ExplicitProperty")), Is.True);
            Assert.That(methods.Exists(m => m.Name.EndsWith("ITestCoverageInterface.set_ExplicitProperty")), Is.True);
            Assert.That(methods.Exists(m => m.Name.EndsWith("ITestCoverageInterface.add_ExplicitEvent")), Is.True);
            Assert.That(methods.Exists(m => m.Name.EndsWith("ITestCoverageInterface.remove_ExplicitEvent")), Is.True);
        }

        [Test]
        public void GetAllMethodsAndConstructorsRecursive_MoreThanOneClass_StrictSeparationAndNoDuplicates()
        {
            var visitedTypes = new HashSet<Type>();
            var methodsClassA = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(MultiClassA), visitedTypes);
            var methodsClassB = GetCoverageHandler.GetAllMethodsAndConstructorsRecursive(typeof(MultiClassB), visitedTypes);

            Assert.That(methodsClassA.Exists(m => m.Name == nameof(MultiClassA.MethodA)), Is.True);
            Assert.That(methodsClassB.Exists(m => m.Name == nameof(MultiClassB.MethodB)), Is.True);

            // Verify disjoint sets
            var setA = new HashSet<System.Reflection.MethodBase>(methodsClassA);
            var setB = new HashSet<System.Reflection.MethodBase>(methodsClassB);
            setA.IntersectWith(setB);
            Assert.That(setA.Count, Is.EqualTo(0));
        }

        private class TypeWithStaticAndInstanceCtors
        {
            static TypeWithStaticAndInstanceCtors() { }
            public TypeWithStaticAndInstanceCtors() { }
            public void InstanceMethod() { }
            public static void StaticMethod() { }
        }

        private class TypeWithOnlyInstanceCtor
        {
            public TypeWithOnlyInstanceCtor() { }
        }

        private class TypeWithAsyncMethods
        {
            public async void AsyncVoidMethod()
            {
                await System.Threading.Tasks.Task.Yield();
            }

            public async System.Threading.Tasks.Task AsyncTaskMethod()
            {
                await System.Threading.Tasks.Task.Yield();
            }

            public async TestCustomAwaitable AsyncAwaitableMethod()
            {
                await System.Threading.Tasks.Task.Yield();
            }
        }

        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(TestCustomAwaitableBuilder))]
        public struct TestCustomAwaitable
        {
            public TestCustomAwaiter GetAwaiter() => new TestCustomAwaiter();
        }

        public struct TestCustomAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        {
            public bool IsCompleted => true;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => continuation();
        }

        public struct TestCustomAwaitableBuilder
        {
            public static TestCustomAwaitableBuilder Create() => new TestCustomAwaitableBuilder();
            public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine => stateMachine.MoveNext();
            public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { }
            public void SetResult() { }
            public void SetException(Exception exception) { }
            public TestCustomAwaitable Task => default;
            public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion
                where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine => awaiter.OnCompleted(stateMachine.MoveNext);
            public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                where TAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion
                where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine => awaiter.OnCompleted(stateMachine.MoveNext);
        }

        private class TypeWithIterators
        {
            public System.Collections.IEnumerator YieldEnumeratorMethod()
            {
                yield return 1;
                yield return 2;
            }

            public System.Collections.Generic.IEnumerable<string> YieldEnumerableMethod()
            {
                yield return "hello";
                yield return "world";
            }
        }

        private class TypeWithLambdas
        {
            public void SimpleLambdaMethod()
            {
                Action a = () => { };
                a();
            }

            public void LambdaWithClosureMethod()
            {
                int x = 10;
                Action a = () => { x++; };
                a();
            }

            public void NestedLambdaMethod()
            {
                int x = 1;
                Func<Func<int>> outer = () =>
                {
                    int y = 2;
                    return () => x + y;
                };
                outer()();
            }

            public void NestedNestedLambdaMethod()
            {
                int a = 1;
                Func<Func<Func<int>>> l1 = () =>
                {
                    int b = 2;
                    return () =>
                    {
                        int c = 3;
                        return () => a + b + c;
                    };
                };
                l1()()();
            }
        }

        private class TypeWithPropertiesAndEvents
        {
            public int AutoProperty { get; set; }

            private int _manual;
            public int ManualProperty
            {
                get => _manual;
                set => _manual = value;
            }

            public string this[int index]
            {
                get => index.ToString();
                set { }
            }

            public event Action AutoEvent;
            public void RaiseAuto() => AutoEvent?.Invoke();

            private Action _custom;
            public event Action CustomEvent
            {
                add => _custom += value;
                remove => _custom -= value;
            }
        }

        private class TypeWithLocalFunctions
        {
            public void HostMethod()
            {
                int localVal = 10;
                int RegularLocal() => localVal + 1;
                static int StaticLocal(int x) => x * 2;

                async System.Threading.Tasks.Task AsyncLocal()
                {
                    await System.Threading.Tasks.Task.Yield();
                }

                System.Collections.Generic.IEnumerable<int> IteratorLocal()
                {
                    yield return localVal;
                }

                RegularLocal();
                StaticLocal(5);
                AsyncLocal();
                IteratorLocal();
            }
        }

        public interface ITestCoverageInterface
        {
            void ExplicitMethod();
            int ExplicitProperty { get; set; }
            event Action ExplicitEvent;
        }

        private class TypeWithExplicitInterface : ITestCoverageInterface
        {
            void ITestCoverageInterface.ExplicitMethod() { }
            int ITestCoverageInterface.ExplicitProperty { get; set; }
            event Action ITestCoverageInterface.ExplicitEvent
            {
                add { }
                remove { }
            }
        }

        private class MultiClassA
        {
            public void MethodA() { }
        }

        private class MultiClassB
        {
            public void MethodB() { }
        }
    }
}
