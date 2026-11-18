using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class UnityTestFormattingTests
{
    [Fact]
    public async Task UnityRunTests_CompileError_FormatsAsTestExecutionAborted()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "CompileError",
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0,
            Message = "Assets/Scripts/Test.cs(12,8): error CS1002: ; expected"
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.EndsWith("Test execution aborted: Script compilation failed.", text);
        Assert.Contains("error CS1002", text);
        Assert.DoesNotContain("Tests Failed: 0 failed", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenFailedTestsAreNull_ReturnsInterruptedMessageWithoutThrowing()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            Interrupted = true,
            Message = "Test run interrupted by Unity recompilation.",
            FailedTests = null!
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal("Test run interrupted: Test run interrupted by Unity recompilation.", text);
        Assert.DoesNotContain("Failures:", text);
    }

    [Fact]
    public async Task UnityRunTests_MoreThan25Failures_CapsDetailedOutputAndSummarizesRemainder()
    {
        using var ctx = new ToolFormattingTestContext();
        var failedTests = new List<FailedTestInfo>();
        for (int i = 0; i < 30; i++)
        {
            failedTests.Add(new FailedTestInfo
            {
                Name = $"Test_Method_{i}",
                FullName = $"MySuite.Test_Method_{i}",
                Message = $"Assertion failed in test {i}",
                StackTrace = $"at MySuite.Test_Method_{i}() line {i}",
                Duration = 0.05
            });
        }

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            FailCount = 30,
            PassCount = 10,
            SkipCount = 2,
            FailedTests = failedTests
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);

        Assert.Contains("Tests Failed: 30 failed, 10 passed, 2 skipped.", text);
        Assert.Contains("• MySuite.Test_Method_0 (0.050s)", text);
        Assert.Contains("at MySuite.Test_Method_0() line 0", text);
        Assert.Contains("• MySuite.Test_Method_4 (0.050s)", text);
        Assert.Contains("at MySuite.Test_Method_4() line 4", text);

        Assert.Contains("• MySuite.Test_Method_5: Assertion failed in test 5", text);
        Assert.Contains("• MySuite.Test_Method_24: Assertion failed in test 24", text);
        Assert.DoesNotContain("at MySuite.Test_Method_5()", text);
        Assert.DoesNotContain("at MySuite.Test_Method_24()", text);

        Assert.DoesNotContain("• MySuite.Test_Method_25", text);
        Assert.Contains("... and 5 more failed test(s).", text);
    }

    [Fact]
    public async Task UnityRunTests_LargeDetailedFailures_ReservesBudgetForLaterSummaries()
    {
        using var ctx = new ToolFormattingTestContext();
        string largeStackTrace = new string('S', McpOutputLimits.MaxFailureStackTraceCharacters * 2);
        var failedTests = new List<FailedTestInfo>();
        for (int i = 0; i < 25; i++)
        {
            failedTests.Add(new FailedTestInfo
            {
                Name = $"LargeFailure_{i}",
                FullName = $"Suite.LargeFailure_{i}",
                Message = $"Failure summary {i}",
                StackTrace = largeStackTrace,
                Duration = 0.05
            });
        }

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            FailCount = failedTests.Count,
            FailedTests = failedTests
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);

        Assert.True(text.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.Contains("• Suite.LargeFailure_0 (0.050s)", text);
        Assert.Contains(McpOutputLimits.FailureStackTraceTruncationMarker, text);
        Assert.Contains(McpOutputLimits.DetailedFailureOutputTruncationMarker, text);

        for (int i = 5; i < failedTests.Count; i++)
        {
            Assert.Contains($"• Suite.LargeFailure_{i}: Failure summary {i}", text);
            Assert.DoesNotContain($"• Suite.LargeFailure_{i} (0.050s)", text);
        }
    }

    [Fact]
    public async Task UnityRunTests_Under25Failures_ShowsAllWithoutSummaryLine()
    {
        using var ctx = new ToolFormattingTestContext();
        var failedTests = new List<FailedTestInfo>();
        for (int i = 0; i < 3; i++)
        {
            failedTests.Add(new FailedTestInfo
            {
                Name = $"Test_{i}",
                FullName = $"Suite.Test_{i}",
                Message = $"Fail {i}",
                StackTrace = $"at Suite.Test_{i}() line {i}",
                Duration = 0.01
            });
        }

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            FailCount = 3,
            PassCount = 5,
            SkipCount = 0,
            FailedTests = failedTests
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);

        Assert.Contains("• Suite.Test_0 (0.010s)", text);
        Assert.Contains("Message: Fail 0", text);
        Assert.Contains("at Suite.Test_0() line 0", text);

        Assert.Contains("• Suite.Test_1 (0.010s)", text);
        Assert.Contains("Message: Fail 1", text);
        Assert.Contains("at Suite.Test_1() line 1", text);

        Assert.Contains("• Suite.Test_2 (0.010s)", text);
        Assert.Contains("Message: Fail 2", text);
        Assert.Contains("at Suite.Test_2() line 2", text);

        Assert.DoesNotContain("• Suite.Test_0:", text);
        Assert.DoesNotContain("• Suite.Test_1:", text);
        Assert.DoesNotContain("• Suite.Test_2:", text);
        Assert.DoesNotContain("more failed test(s).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenTenFailures_FirstFiveDetailedNextFiveSummarizedWithoutStackTraces()
    {
        using var ctx = new ToolFormattingTestContext();
        var failedTests = new List<FailedTestInfo>();
        for (int i = 0; i < 10; i++)
        {
            failedTests.Add(new FailedTestInfo
            {
                Name = $"Test_{i}",
                FullName = $"Suite.Test_{i}",
                Message = $"Failure message {i}",
                StackTrace = $"at Suite.Test_{i}() line {i}",
                Duration = 0.02
            });
        }

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            FailCount = 10,
            PassCount = 0,
            SkipCount = 0,
            FailedTests = failedTests
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);

        for (int i = 0; i < 5; i++)
        {
            Assert.Contains($"• Suite.Test_{i} (0.020s)", text);
            Assert.Contains($"Message: Failure message {i}", text);
            Assert.Contains($"at Suite.Test_{i}() line {i}", text);
            Assert.DoesNotContain($"• Suite.Test_{i}:", text);
        }

        for (int i = 5; i < 10; i++)
        {
            Assert.Contains($"• Suite.Test_{i}: Failure message {i}", text);
            Assert.DoesNotContain($"at Suite.Test_{i}()", text);
            Assert.DoesNotContain($"• Suite.Test_{i} (0.020s)", text);
        }

        Assert.DoesNotContain("more failed test(s).", text);
    }

    [Fact]
    public async Task UnityRunTests_SummarizedFailures_ExtractsFirstNonEmptyLineAndTruncatesLongMessages()
    {
        using var ctx = new ToolFormattingTestContext();
        var failedTests = new List<FailedTestInfo>();
        for (int i = 0; i < 5; i++)
        {
            failedTests.Add(new FailedTestInfo
            {
                Name = $"Detailed_{i}",
                FullName = $"Suite.Detailed_{i}",
                Message = $"Detailed message {i}",
                StackTrace = $"at Suite.Detailed_{i}()",
                Duration = 0.01
            });
        }

        failedTests.Add(new FailedTestInfo
        {
            Name = "MultiLineTest",
            FullName = "Suite.MultiLineTest",
            Message = "\r\n   Expected: 42\r\n   But was: 10\r\n   Extra stack",
            StackTrace = "at Suite.MultiLineTest()",
            Duration = 0.01
        });

        string veryLongMsg = new string('A', 250);
        failedTests.Add(new FailedTestInfo
        {
            Name = "LongMsgTest",
            FullName = "Suite.LongMsgTest",
            Message = veryLongMsg,
            StackTrace = "at Suite.LongMsgTest()",
            Duration = 0.01
        });

        failedTests.Add(new FailedTestInfo
        {
            Name = "NoMsgTest",
            FullName = "Suite.NoMsgTest",
            Message = "   ",
            StackTrace = "at Suite.NoMsgTest()",
            Duration = 0.01
        });

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            FailCount = 8,
            PassCount = 0,
            SkipCount = 0,
            FailedTests = failedTests
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);

        Assert.Contains("• Suite.MultiLineTest: Expected: 42", text);
        Assert.DoesNotContain("But was: 10", text);
        Assert.DoesNotContain("at Suite.MultiLineTest()", text);

        string expectedTruncated = new string('A', 197) + "...";
        Assert.Contains($"• Suite.LongMsgTest: {expectedTruncated}", text);
        Assert.DoesNotContain(veryLongMsg, text);

        Assert.Contains("• Suite.NoMsgTest", text);
        Assert.DoesNotContain("• Suite.NoMsgTest:", text);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \r\n \t \n", null)]
    [InlineData("Simple single line", "Simple single line")]
    [InlineData("   Trimmed single line   ", "Trimmed single line")]
    [InlineData("\r\n\r\nFirst non-empty line\r\nSecond line", "First non-empty line")]
    [InlineData("Line 1\nLine 2\nLine 3", "Line 1")]
    public void UnityTools_ExtractOneLineSummaryMessage_ExtractsCorrectly(string? input, string? expected)
    {
        string? actual = UnityToolOutputFormatter.ExtractOneLineSummaryMessage(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnityTools_ExtractOneLineSummaryMessage_TruncatesLongLines()
    {
        string longInput = new string('x', 250);
        string? result = UnityToolOutputFormatter.ExtractOneLineSummaryMessage(longInput, maxLineLength: 200);
        Assert.NotNull(result);
        Assert.Equal(200, result.Length);
        Assert.EndsWith("...", result);
        Assert.Equal(new string('x', 197) + "...", result);
    }

    [Theory]
    [InlineData("HelloWorld", 3, "Hel")]
    [InlineData("HelloWorld", 2, "He")]
    [InlineData("HelloWorld", 1, "H")]
    [InlineData("HelloWorld", 0, "HelloWorld")]
    [InlineData("HelloWorld", -1, "HelloWorld")]
    public void UnityTools_ExtractOneLineSummaryMessage_HandlesShortMaxLineLengthsSafely(string input, int maxLen, string expected)
    {
        string? result = UnityToolOutputFormatter.ExtractOneLineSummaryMessage(input, maxLineLength: maxLen);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task UnityRunTests_WhenFilterSpecifiedAndZeroTestsRun_ReturnsErrorWithDescriptiveMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["SomeFilter"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching filter 'SomeFilter' (mode: editmode).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenCategorySpecifiedAndZeroTestsRun_ReturnsErrorWithDescriptiveMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, categoryNames: ["SomeCat"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching category 'SomeCat' (mode: editmode).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenFilterAndCategorySpecifiedAndZeroTestsRun_ReturnsErrorWithBoth()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["SomeFilter"], categoryNames: ["SomeCat"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching filter 'SomeFilter' and category 'SomeCat' (mode: editmode).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenUnfilteredAndZeroTestsRun_ReturnsSuccessWithEmptySuiteMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Equal("Tests Passed: 0 passed (no tests found in suite).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenTestsPassedAndZeroSkipped_OmitsZeroSkipped()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 27,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Passed: 27 passed.", text);
        Assert.DoesNotContain("skipped", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnityRunTests_ReportsCoverageStatusAlongsidePassingTestOutcome()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 1,
            CoverageStatus = "available",
            CoverageMessage = "Coverage report captured for the completed test run."
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, coverage: true);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Passed: 1 passed.", text);
        Assert.Contains("Coverage captured.", text);
    }

    [Fact]
    public async Task UnityRunTests_ReportsUnavailableCoverageWithoutChangingPassingTestStatus()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 1,
            CoverageStatus = "unavailable",
            CoverageMessage = "Profiler restoration compilation failed."
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, coverage: true);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Passed: 1 passed.", text);
        Assert.Contains("Coverage unavailable: Profiler restoration compilation failed.", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenTestsPassedAndSkippedGreaterThanZero_IncludesSkipped()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 27,
            SkipCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Passed: 27 passed, 1 skipped.", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenTestsFailedAndZeroSkipped_OmitsZeroSkipped()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            FailCount = 1,
            PassCount = 27,
            SkipCount = 0,
            FailedTests = [new FailedTestInfo { Name = "FailTest", FullName = "Suite.FailTest", Message = "Failed assertion" }]
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Failed: 1 failed, 27 passed.", text);
        Assert.DoesNotContain("skipped", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnityRunTests_WhenTestsFailedAndSkippedGreaterThanZero_IncludesSkipped()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            FailCount = 1,
            PassCount = 27,
            SkipCount = 2,
            FailedTests = [new FailedTestInfo { Name = "FailTest", FullName = "Suite.FailTest", Message = "Failed assertion" }]
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Failed: 1 failed, 27 passed, 2 skipped.", text);
    }

    [Fact]
    public async Task UnityRunTests_PreExecutionFailure_PrioritizesFailureOverFilterMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            PassCount = 0,
            FailCount = 0,
            SkipCount = 0,
            Message = "Test framework initialization failed."
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["SomeFilter"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Test run failed: Test framework initialization failed.", text);
        Assert.DoesNotContain("No tests found matching filter", text);
    }

    [Fact]
    public async Task UnityRunTests_FailedOnly_ZeroTests_ReportsNoPreviouslyFailedTestsFound()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 0,
            FailCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, failedOnly: true);

        Assert.False(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No previously failed tests found.", text);
    }

    [Fact]
    public async Task UnityRunTests_InterruptedWithCancelledState_ReportsInterrupted()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            ResultState = "Cancelled",
            Message = "Cancelled by user"
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Test run interrupted: Cancelled by user", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenFailureOccursWithZeroTests_SurfacesFailureMessageInsteadOfNoTestsFound()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0,
            Message = "Regex parsing error: Quantifier * following nothing"
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["Movement.*"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Test run failed: Regex parsing error: Quantifier * following nothing", text);
        Assert.DoesNotContain("No tests found matching", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenTestNamesSpecifiedAndZeroTestsRun_ReturnsDescriptiveErrorMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["MyNamespace.MyTest"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching testNames 'MyNamespace.MyTest' (mode: editmode).", text);
    }

    [Fact]
    public async Task UnityRunTests_WhenAssemblyNamesSpecifiedAndZeroTestsRun_ReturnsDescriptiveErrorMessage()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, assemblyNames: ["MyCompany.MyTests"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching assemblyNames 'MyCompany.MyTests' (mode: editmode).", text);
    }

    [Fact]
    public async Task UnityRunTests_ReturnsStructuredDataWithCountsAndFailures()
    {
        using var ctx = new ToolFormattingTestContext();
        var failed = new List<FailedTestInfo>
        {
            new FailedTestInfo
            {
                Name = "Test_AssertFailure",
                FullName = "MySuite.Test_AssertFailure",
                Duration = 0.25,
                Message = "Expected 10 but got 5",
                StackTrace = "  at MySuite.Test_AssertFailure () [0x00010] in Assets/Tests/Editor/DummyTest.cs:42\n"
            }
        };

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            PassCount = 7,
            FailCount = 1,
            SkipCount = 2,
            FailedTests = failed
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        Assert.Single(result.Content);

        string humanText = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Tests Failed: 1 failed, 7 passed, 2 skipped.", humanText);
        Assert.Contains("• MySuite.Test_AssertFailure (0.250s)", humanText);
        Assert.Contains("Location: file:///", humanText);
        Assert.Contains("#L42", humanText);
        Assert.DoesNotContain("[Assets/Tests/Editor/DummyTest.cs:42]", humanText);
        Assert.DoesNotContain("](file:///", humanText);
        Assert.Contains("Message: Expected 10 but got 5", humanText);
        Assert.Contains("Stack trace:", humanText);
    }

    [Fact]
    public async Task UnityRunTests_WhenStackTraceContainsRunnerPlumbing_SanitizesPlumbingFromOutput()
    {
        using var ctx = new ToolFormattingTestContext();
        var failed = new List<FailedTestInfo>
        {
            new FailedTestInfo
            {
                Name = "Test_AssertFailure",
                FullName = "MySuite.Test_AssertFailure",
                Duration = 0.25,
                Message = "Expected 10 but got 5",
                StackTrace = string.Join(Environment.NewLine, new[]
                {
                    "  at MySuite.Test_AssertFailure () [0x00010] in Assets/Tests/Editor/DummyTest.cs:42",
                    "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
                    "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj, System.Reflection.BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x0006a] in <...>:0",
                    "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0",
                    "  at UnityEditor.TestTools.TestRunner.EditorEnumeratorTestWorkItem.Execute () [0x0003b] in <...>:0"
                })
            }
        };

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "Failed",
            PassCount = 1,
            FailCount = 1,
            SkipCount = 0,
            FailedTests = failed
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string humanText = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("MySuite.Test_AssertFailure ()", humanText);
        Assert.DoesNotContain("System.Reflection", humanText);
        Assert.DoesNotContain("TestMethodCommand", humanText);
        Assert.DoesNotContain("EditorEnumeratorTestWorkItem", humanText);
    }

    [Fact]
    public async Task UnityRunTests_WhenCompileErrorWithWarningsAndErrors_FormatsStructuredDiagnostics()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            ResultState = "CompileError",
            Message = "Assets/Scripts/Util.cs(5,1): warning CS0219: Variable 't' unused\n" +
                      "Assets/Scripts/Test.cs(12,8): error CS1002: ; expected"
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Warnings:", text);
        Assert.Contains("Errors:", text);
        Assert.Contains("Assets/Scripts/Util.cs#L5", text);
        Assert.Contains("Assets/Scripts/Test.cs#L12", text);
        Assert.EndsWith("Test execution aborted: Script compilation failed.", text);

        int warnIdx = text.IndexOf("Util.cs#L5");
        int errIdx = text.IndexOf("Test.cs#L12");
        Assert.True(errIdx < warnIdx, "Errors must precede warnings");
    }
}
