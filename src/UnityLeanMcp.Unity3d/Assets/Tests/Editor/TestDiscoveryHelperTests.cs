using System.Collections.Generic;
using NUnit.Framework;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class TestDiscoveryHelperTests
    {
        [Test]
        [Category("TestDiscoveryUnit")]
        public void DiscoverAllTests_ReturnsTestsInActiveAppDomain()
        {
            var tests = TestDiscoveryHelper.DiscoverAllTests();
            Assert.That(tests, Is.Not.Null);
            Assert.That(tests.Count, Is.GreaterThan(0));

            bool foundSelf = tests.Exists(t => t.FullName.Contains("TestDiscoveryHelperTests"));
            Assert.That(foundSelf, Is.True, "Should discover tests in current assembly");
        }

        [TestCase(1, 2)]
        [TestCase(3, 4)]
        [Category("TestDiscoveryParameterized")]
        public void InspectTests_ReportsModeCategoryAndParameterizedCaseNames(int a, int b)
        {
            Assert.That(a + b, Is.GreaterThan(0));

            string result = Inspect.Tests(
                filter: nameof(InspectTests_ReportsModeCategoryAndParameterizedCaseNames),
                mode: "editmode",
                category: "TestDiscoveryParameterized");

            StringAssert.Contains("[editmode]", result);
            StringAssert.Contains("[TestDiscoveryParameterized]", result);
            StringAssert.Contains($"{nameof(InspectTests_ReportsModeCategoryAndParameterizedCaseNames)}(1,2)", result);
            StringAssert.Contains($"{nameof(InspectTests_ReportsModeCategoryAndParameterizedCaseNames)}(3,4)", result);
        }

        [Test]
        public void DiscoverHints_WithPartialTestName_SuggestsNearMatches()
        {
            TestDiscoveryHelper.DiscoverHints(
                currentMode: "editmode",
                testNames: new[] { "Hierarchy" },
                groupNames: null,
                categoryNames: null,
                assemblyNames: null,
                out var otherModeMatches,
                out var nearMatches);

            Assert.That(nearMatches, Is.Not.Null);
            Assert.That(nearMatches.Count, Is.GreaterThan(0));
            Assert.That(nearMatches.Count, Is.LessThanOrEqualTo(5));
            Assert.That(nearMatches.TrueForAll(m => m.IndexOf("Hierarchy", System.StringComparison.OrdinalIgnoreCase) >= 0), Is.True);
        }

        [Test]
        public void DiscoverHints_WithFixtureName_SuggestsTestsUnderFixture()
        {
            TestDiscoveryHelper.DiscoverHints(
                currentMode: "editmode",
                testNames: new[] { "TestDiscoveryHelperTests" },
                groupNames: null,
                categoryNames: null,
                assemblyNames: null,
                out var otherModeMatches,
                out var nearMatches);

            Assert.That(nearMatches, Is.Not.Null);
            Assert.That(nearMatches.Count, Is.GreaterThan(0));
            Assert.That(nearMatches[0], Does.Contain("TestDiscoveryHelperTests"));
        }

        [Test]
        public void DiscoverHints_WithOppositeModeFilter_ReportsOtherModeMatches()
        {
            var mockItems = new List<TestDiscoveryHelper.DiscoveredItem>
            {
                new TestDiscoveryHelper.DiscoveredItem
                {
                    FullName = "MyGame.Tests.CombatPlayModeTests.AttackHitsTarget",
                    FixtureFullName = "MyGame.Tests.CombatPlayModeTests",
                    Name = "AttackHitsTarget",
                    AssemblyName = "Game.PlayModeTests",
                    Mode = "playmode"
                }
            };

            TestDiscoveryHelper.DiscoverHints(
                currentMode: "editmode",
                testNames: new[] { "MyGame.Tests.CombatPlayModeTests.AttackHitsTarget" },
                groupNames: null,
                categoryNames: null,
                assemblyNames: null,
                out var otherModeMatches,
                out var nearMatches,
                preloadedItems: mockItems);

            Assert.That(otherModeMatches, Is.Not.Null);
            Assert.That(otherModeMatches.Count, Is.EqualTo(1));
            Assert.That(otherModeMatches[0], Is.EqualTo("MyGame.Tests.CombatPlayModeTests.AttackHitsTarget"));
        }
    }
}
