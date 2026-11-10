using System;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Collection("UnityIntegration")]
[Trait("Category", "UnityIntegration")]
public class TestFrameworkTests
{
    private readonly UnityIntegrationFixture _fixture;

    public TestFrameworkTests(UnityIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestEverythingPasses_RunsEditModeTestsSuccessfully()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new { mode = "editmode" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestCompileErrorsAndWarnings_FailsOnScriptCompileError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestCompileErrorsAndWarnings");
        var client = _fixture.SharedClient;

        var result = await client.CallToolAsync("unity_test", new { mode = "editmode" });

        Assert.True(result.IsError, result.Text);
        Assert.True(result.Text.Contains("CS", StringComparison.Ordinal), result.Text);
    }

    [Fact]
    public async Task TestCompileWarningsAndPass_RunsTestsDespiteCompilerWarnings()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "PassWithWarningTest" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndFailures_ReportsFailedAssertionsAndStackTraces()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "FailTest" }
        });

        Assert.True(result.IsError);
        Assert.Contains("Tests Failed:", result.Text);
        Assert.Contains("Failures:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndSkipped_ReportsSkippedTests()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "IgnoreTest" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("skipped", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestFilterCategory_ExcludesOrIncludesCategories()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            categoryNames = new[] { "!LongRunning" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestFilterByName_RunsOnlyTargetedTest()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "SpecificTargetTest" }
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed: 1 passed", result.Text);
    }

    [Fact]
    public async Task TestRunTests_WithoutMode_FailsValidation()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test");

        Assert.True(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestRunTests_WithRemovedAllMode_FailsValidation()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_test", new { mode = "all" });

        Assert.True(result.IsError, result.Text);
    }

    [Fact]
    public async Task TestEverythingPasses_FailedOnlyWhenNoFailures_ReturnsNoFailedTestsFound()
    {
        var client = _fixture.SharedClient;

        var initialRun = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "PassTest" }
        });
        Assert.False(initialRun.IsError, initialRun.Text);

        var failedRun = await client.CallToolAsync("unity_test", new { mode = "editmode", failedOnly = true });
        Assert.False(failedRun.IsError, failedRun.Text);
        Assert.Contains("No previously failed tests found.", failedRun.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndFailures_FailedOnlyRerunsFailedTests()
    {
        var client = _fixture.SharedClient;

        var initialRun = await client.CallToolAsync("unity_test", new
        {
            mode = "editmode",
            groupNames = new[] { "FailTest" }
        });
        Assert.True(initialRun.IsError);
        Assert.Contains("Failures:", initialRun.Text);

        var failedRun = await client.CallToolAsync("unity_test", new { mode = "editmode", failedOnly = true });
        Assert.True(failedRun.IsError);
        Assert.Contains("Tests Failed:", failedRun.Text);
        Assert.Contains("Failures:", failedRun.Text);
        Assert.Contains("FailTest", failedRun.Text);
    }

    [Fact]
    public async Task TestEditModeCoverage_WhenCoverageInitiallyDisabled_RemainsValidAfterProfilerRestoration()
    {
        var client = _fixture.SharedClient;

        var initResult = await client.CallToolAsync("unity_eval", new
        {
            code = @"
                bool wasRelease = UnityEditor.Compilation.CompilationPipeline.codeOptimization == UnityEditor.Compilation.CodeOptimization.Release;
                if (wasRelease)
                {
                    UnityEditor.Compilation.CompilationPipeline.codeOptimization = UnityEditor.Compilation.CodeOptimization.Debug;
                }
                UnityEngine.TestTools.Coverage.enabled = false;
                return wasRelease;
            "
        });
        Assert.False(initResult.IsError, initResult.Text);
        bool restoreRelease = initResult.Text.Contains("true", StringComparison.OrdinalIgnoreCase);

        try
        {
            var testResult = await client.CallToolAsync("unity_test", new
            {
                mode = "editmode",
                groupNames = new[] { "SpecificTargetTest" },
                coverage = true
            }, timeout: TimeSpan.FromSeconds(120));
            Assert.False(testResult.IsError, testResult.Text);
            Assert.Contains("Tests Passed: 1 passed", testResult.Text);
            Assert.Contains("Coverage captured.", testResult.Text);

            var coverageResult = await client.CallToolAsync("unity_coverage", new
            {
                paths = new[] { "Assets/Tests/Editor/DummyTest.cs" }
            });
            Assert.False(coverageResult.IsError, coverageResult.Text);
            Assert.Contains("DummyTest.cs", coverageResult.Text);

            var repeatedCoverageResult = await client.CallToolAsync("unity_coverage", new
            {
                paths = new[] { "Assets/Tests/Editor/DummyTest.cs" }
            });
            Assert.False(repeatedCoverageResult.IsError, repeatedCoverageResult.Text);
            Assert.Contains("DummyTest.cs", repeatedCoverageResult.Text);

            var restoredCheck = await client.CallToolAsync("unity_eval", new
            {
                code = "return UnityEngine.TestTools.Coverage.enabled;"
            });
            Assert.False(restoredCheck.IsError, restoredCheck.Text);
            Assert.Contains("false", restoredCheck.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (restoreRelease)
            {
                await client.CallToolAsync("unity_eval", new
                {
                    code = "UnityEditor.Compilation.CompilationPipeline.codeOptimization = UnityEditor.Compilation.CodeOptimization.Release;"
                });
            }
        }
    }
}
