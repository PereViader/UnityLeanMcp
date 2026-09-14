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
        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestCompileErrorsAndWarnings_FailsOnScriptCompileError()
    {
        await using var _ = await _fixture.UseFixtureAsync("TestCompileErrorsAndWarnings");
        var client = _fixture.SharedClient;

        var result = await client.CallToolAsync("unity_run_tests", new { mode = "editmode" });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("CS", result.Text);
    }

    [Fact]
    public async Task TestCompileWarningsAndPass_RunsTestsDespiteCompilerWarnings()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "PassWithWarningTest"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndFailures_ReportsFailedAssertionsAndStackTraces()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "FailTest"
        });

        Assert.True(result.IsError);
        Assert.Contains("Tests Failed:", result.Text);
        Assert.Contains("Failures:", result.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndSkipped_ReportsSkippedTests()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "IgnoreTest"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("skipped", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestFilterCategory_ExcludesOrIncludesCategories()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            categoryNames = "!LongRunning"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestFilterByName_RunsOnlyTargetedTest()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "SpecificTargetTest"
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed: 1 passed", result.Text);
    }

    [Fact]
    public async Task TestEverythingPasses_RunsAllTestsByDefault()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestEverythingPasses_RunsAllTestsExplicitly()
    {
        var client = _fixture.SharedClient;
        var result = await client.CallToolAsync("unity_run_tests", new { mode = "all" });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Tests Passed:", result.Text);
    }

    [Fact]
    public async Task TestEverythingPasses_FailedOnlyWhenNoFailures_ReturnsNoFailedTestsFound()
    {
        var client = _fixture.SharedClient;

        var initialRun = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "PassTest"
        });
        Assert.False(initialRun.IsError, initialRun.Text);

        var failedRun = await client.CallToolAsync("unity_run_tests", new { failedOnly = true });
        Assert.False(failedRun.IsError, failedRun.Text);
        Assert.Contains("No previously failed tests found.", failedRun.Text);
    }

    [Fact]
    public async Task TestNoWarningsAndFailures_FailedOnlyRerunsFailedTests()
    {
        var client = _fixture.SharedClient;

        var initialRun = await client.CallToolAsync("unity_run_tests", new
        {
            mode = "editmode",
            groupNames = "FailTest"
        });
        Assert.True(initialRun.IsError);
        Assert.Contains("Failures:", initialRun.Text);

        var failedRun = await client.CallToolAsync("unity_run_tests", new { failedOnly = true });
        Assert.True(failedRun.IsError);
        Assert.Contains("Tests Failed:", failedRun.Text);
        Assert.Contains("Failures:", failedRun.Text);
        Assert.Contains("FailTest", failedRun.Text);
    }
}
