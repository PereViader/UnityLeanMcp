using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class TestFilterAndDiscoveryTests
{
    [Fact]
    public async Task UnityRunTests_WhenModeIsUnsupported_ReturnsValidationErrorBeforeCallingClient()
    {
        using var ctx = new ToolFormattingTestContext();
        var result = await ctx.Tools.UnityTestAsync(mode: (UnityTestMode)999);

        Assert.True(result.IsError);
        Assert.Equal(TestModeParser.InvalidModeMessage, ToolFormattingTestContext.GetResultText(result));
        Assert.Equal(0, ctx.Client.RunTestsCallCount);
    }

    [Fact]
    public async Task UnityRunTests_WhenModeEnumIsInvalid_ReturnsValidationErrorBeforeCallingClient()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(mode: (UnityTestMode)999);

        Assert.True(result.IsError);
        Assert.Equal(TestModeParser.InvalidModeMessage, ToolFormattingTestContext.GetResultText(result));
        Assert.Equal(0, ctx.Client.RunTestsCallCount);
    }

    [Theory]
    [InlineData(UnityTestMode.EditMode, "editmode")]
    [InlineData(UnityTestMode.PlayMode, "playmode")]
    public async Task UnityRunTests_WhenModeIsValid_PassesCanonicalModeToClient(UnityTestMode mode, string expectedMode)
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(mode: mode);

        Assert.False(result.IsError);
        Assert.Equal(expectedMode, ctx.Client.LastMode);
        Assert.Equal(1, ctx.Client.RunTestsCallCount);
    }

    [Fact]
    public async Task UnityRunTests_ArrayParameters_PassThroughToClient()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(
            mode: UnityTestMode.EditMode,
            testNames: ["MyNamespace.MyTestClass.MyMethod"],
            groupNames: ["MyNamespace\\.MyTestClass"],
            categoryNames: ["Integration"],
            assemblyNames: ["MyProject.Tests"]);

        Assert.False(result.IsError);
        Assert.NotNull(ctx.Client.LastTestNames);
        Assert.Equal(["MyNamespace.MyTestClass.MyMethod"], ctx.Client.LastTestNames);
        Assert.NotNull(ctx.Client.LastGroupNames);
        Assert.Equal(["MyNamespace\\.MyTestClass"], ctx.Client.LastGroupNames);
        Assert.NotNull(ctx.Client.LastCategoryNames);
        Assert.Equal(["Integration"], ctx.Client.LastCategoryNames);
        Assert.NotNull(ctx.Client.LastAssemblyNames);
        Assert.Equal(["MyProject.Tests"], ctx.Client.LastAssemblyNames);
    }

    [Fact]
    public async Task UnityRunTests_ArrayParameters_PassThroughToClientVerbatim()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 2
        };

        var testNames = new[] { "TestA", "TestB" };
        var groupNames = new[] { "GroupA.*", "GroupB.*" };
        var catNames = new[] { "Cat1", "Cat2" };
        var asmNames = new[] { "Asm1", "Asm2" };

        var result = await ctx.Tools.UnityTestAsync(
            mode: UnityTestMode.EditMode,
            testNames: testNames,
            groupNames: groupNames,
            categoryNames: catNames,
            assemblyNames: asmNames);

        Assert.False(result.IsError);
        Assert.Equal(testNames, ctx.Client.LastTestNames);
        Assert.Equal(groupNames, ctx.Client.LastGroupNames);
        Assert.Equal(catNames, ctx.Client.LastCategoryNames);
        Assert.Equal(asmNames, ctx.Client.LastAssemblyNames);
    }

    [Fact]
    public async Task UnityRunTests_SingleAndMultipleArrayParameters_PassThroughProperly()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 2
        };

        var singleResult = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["TestA"]);
        Assert.False(singleResult.IsError);
        Assert.NotNull(ctx.Client.LastTestNames);
        Assert.Equal(["TestA"], ctx.Client.LastTestNames);

        var arrayResult = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["TestA", "TestB"]);
        Assert.False(arrayResult.IsError);
        Assert.NotNull(ctx.Client.LastTestNames);
        Assert.Equal(["TestA", "TestB"], ctx.Client.LastTestNames);
    }

    [Theory]
    [InlineData("testNames", "")]
    [InlineData("testNames", " ")]
    [InlineData("groupNames", "")]
    [InlineData("groupNames", " ")]
    [InlineData("categoryNames", "")]
    [InlineData("categoryNames", " ")]
    [InlineData("assemblyNames", "")]
    [InlineData("assemblyNames", " ")]
    public async Task UnityRunTests_BlankFilterArray_ReturnsErrorBeforeCallingClient(string filterName, string value)
    {
        using var ctx = new ToolFormattingTestContext();
        CallToolResult result = filterName switch
        {
            "testNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: [value]),
            "groupNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: [value]),
            "categoryNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, categoryNames: [value]),
            "assemblyNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, assemblyNames: [value]),
            _ => throw new ArgumentOutOfRangeException(nameof(filterName))
        };

        Assert.True(result.IsError);
        Assert.Contains($"Invalid test filter '{filterName}[0]'", ToolFormattingTestContext.GetResultText(result));
        Assert.Equal(0, ctx.Client.RunTestsCallCount);
    }

    [Theory]
    [InlineData("testNames")]
    [InlineData("groupNames")]
    [InlineData("categoryNames")]
    [InlineData("assemblyNames")]
    public async Task UnityRunTests_BlankArrayElement_ReturnsErrorBeforeCallingClient(string filterName)
    {
        using var ctx = new ToolFormattingTestContext();
        CallToolResult result = filterName switch
        {
            "testNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["Valid", " ", "AlsoValid"]),
            "groupNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["Valid.*", " ", "AlsoValid.*"]),
            "categoryNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, categoryNames: ["Valid", " ", "AlsoValid"]),
            "assemblyNames" => await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, assemblyNames: ["Valid", " ", "AlsoValid"]),
            _ => throw new ArgumentOutOfRangeException(nameof(filterName))
        };

        Assert.True(result.IsError);
        Assert.Contains($"Invalid test filter '{filterName}[1]'", ToolFormattingTestContext.GetResultText(result));
        Assert.Equal(0, ctx.Client.RunTestsCallCount);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(")]
    [InlineData("*Movement*")]
    public async Task UnityRunTests_InvalidRegex_ReturnsIndexedParseErrorBeforeCallingClient(string pattern)
    {
        using var ctx = new ToolFormattingTestContext();
        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["Valid.*", pattern]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("Invalid test filter 'groupNames[1]'", text);
        Assert.Contains("invalid .NET regular expression:", text);
        Assert.Contains(pattern, text);
        Assert.Equal(0, ctx.Client.RunTestsCallCount);
    }

    [Theory]
    [InlineData("testNames")]
    [InlineData("categoryNames")]
    [InlineData("assemblyNames")]
    public void LiteralTestFilters_DoNotRequireValidRegex(string filterName)
    {
        Assert.True(TestFilterValidation.TryValidate(filterName, ["[", "("], out _));
    }

    [Fact]
    public async Task UnityRunTests_ValidArrayFilters_PassThroughWithoutBroadening()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(
            mode: UnityTestMode.EditMode,
            testNames: ["Namespace.Fixture.Test"],
            groupNames: ["Namespace.Fixture.*", "OtherFixture.*"],
            categoryNames: ["Fast"],
            assemblyNames: ["Project.Tests", "Project.EditorTests"]);

        Assert.False(result.IsError);
        Assert.NotNull(ctx.Client.LastTestNames);
        Assert.Equal(["Namespace.Fixture.Test"], ctx.Client.LastTestNames);
        Assert.NotNull(ctx.Client.LastGroupNames);
        Assert.Equal(["Namespace.Fixture.*", "OtherFixture.*"], ctx.Client.LastGroupNames);
        Assert.NotNull(ctx.Client.LastCategoryNames);
        Assert.Equal(["Fast"], ctx.Client.LastCategoryNames);
        Assert.NotNull(ctx.Client.LastAssemblyNames);
        Assert.Equal(["Project.Tests", "Project.EditorTests"], ctx.Client.LastAssemblyNames);
        Assert.Equal(1, ctx.Client.RunTestsCallCount);
    }

    [Fact]
    public async Task UnityRunTests_PluralArrayParameters_PassThroughToClient()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            PassCount = 1
        };

        var result = await ctx.Tools.UnityTestAsync(
            mode: UnityTestMode.EditMode,
            groupNames: ["MyLegacyFilter"],
            categoryNames: ["MyLegacyCat"]);

        Assert.False(result.IsError);
        Assert.NotNull(ctx.Client.LastGroupNames);
        Assert.Equal(["MyLegacyFilter"], ctx.Client.LastGroupNames);
        Assert.NotNull(ctx.Client.LastCategoryNames);
        Assert.Equal(["MyLegacyCat"], ctx.Client.LastCategoryNames);
    }

    [Theory]
    [InlineData("testNames")]
    [InlineData("groupNames")]
    [InlineData("categoryNames")]
    [InlineData("assemblyNames")]
    public async Task UnityTest_IndividualFilters_SupportSingleAndMultipleArrayValues(string filterParam)
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult { Success = true, PassCount = 1 };

        switch (filterParam)
        {
            case "testNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["MyTest"]);
                Assert.NotNull(ctx.Client.LastTestNames);
                Assert.Equal(["MyTest"], ctx.Client.LastTestNames);
                break;
            case "groupNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["MyGroup.*"]);
                Assert.NotNull(ctx.Client.LastGroupNames);
                Assert.Equal(["MyGroup.*"], ctx.Client.LastGroupNames);
                break;
            case "categoryNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, categoryNames: ["Unit"]);
                Assert.NotNull(ctx.Client.LastCategoryNames);
                Assert.Equal(["Unit"], ctx.Client.LastCategoryNames);
                break;
            case "assemblyNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, assemblyNames: ["MyAssembly"]);
                Assert.NotNull(ctx.Client.LastAssemblyNames);
                Assert.Equal(["MyAssembly"], ctx.Client.LastAssemblyNames);
                break;
        }

        switch (filterParam)
        {
            case "testNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["Test1", "Test2"]);
                Assert.NotNull(ctx.Client.LastTestNames);
                Assert.Equal(["Test1", "Test2"], ctx.Client.LastTestNames);
                break;
            case "groupNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: ["Group1.*", "Group2.*"]);
                Assert.NotNull(ctx.Client.LastGroupNames);
                Assert.Equal(["Group1.*", "Group2.*"], ctx.Client.LastGroupNames);
                break;
            case "categoryNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, categoryNames: ["Unit", "Integration"]);
                Assert.NotNull(ctx.Client.LastCategoryNames);
                Assert.Equal(["Unit", "Integration"], ctx.Client.LastCategoryNames);
                break;
            case "assemblyNames":
                await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, assemblyNames: ["Asm1", "Asm2"]);
                Assert.NotNull(ctx.Client.LastAssemblyNames);
                Assert.Equal(["Asm1", "Asm2"], ctx.Client.LastAssemblyNames);
                break;
        }
    }

    [Theory]
    [InlineData("unity_refresh", "Refreshes AssetDatabase and returns compiler and asset import diagnostics. C# compiler errors persist across no-op refreshes, while non-C# diagnostics (shaders, import failures) are captured only during active imports and not re-emitted if assets are unchanged. Normal refresh is fast when unchanged; set clean to true only when a full script recompilation is needed to recover from a stale or corrupted compiler cache; clean refreshes are more expensive.")]
    [InlineData("unity_eval", "Evaluates C# top-level statements in the active Unity Editor with top-level await. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. Use 'return <value>;' to return results. Call 'return Inspect.Help();' for cheatsheet.")]
    [InlineData("unity_test", "Runs Unity tests in 'editmode' or 'playmode'. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. The mode parameter is required; callers must determine whether target tests are EditMode or PlayMode before calling. Use 'editmode' for fast unit tests, editor utilities, and tests without player/runtime lifecycle. Use 'playmode' for integration tests, tests that load scenes, use MonoBehaviour lifecycle, or yield frames via UnityTest/IEnumerator. Use testNames for exact fully qualified name filters and groupNames for .NET regex filters; categoryNames and assemblyNames are also supported as string arrays. To discover available tests or verify exact test names before running, evaluate Inspect.Tests(\"<filter>\") via unity_eval. Set failedOnly to re-run only failed tests (recommended for slow suites). Set coverage to true to capture in-memory code coverage.")]
    [InlineData("unity_coverage", "Queries in-memory code coverage for specified files or directories from the latest test run. Does not run tests. Accepts individual C# files and directory paths (which include all scripts in that folder and its subdirectories). Returns line coverage percentages and lists uncovered line spans for each file.")]
    public void UnityTools_Methods_HaveExpectedRefinedDescriptions(string toolName, string expectedDescription)
    {
        var methods = typeof(UnityTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var targetMethod = methods.FirstOrDefault(m =>
            m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        Assert.NotNull(targetMethod);
        var descAttr = targetMethod.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal(expectedDescription, descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityEval_CodeParameter_HasAccurateDescription()
    {
        var evalMethod = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityEvalAsync));
        Assert.NotNull(evalMethod);
        var codeParam = evalMethod.GetParameters().FirstOrDefault(p => p.Name == "code");
        Assert.NotNull(codeParam);
        var descAttr = codeParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal("Raw C# source text to evaluate. Send plain text directly—do not wrap in JSON.", descAttr.Description);
    }

    [Theory]
    [InlineData("return 1 + 1;", "return 1 + 1;")]
    [InlineData("using System.IO;\nreturn 42;", "using System.IO;\nreturn 42;")]
    [InlineData("{\"code\": \"return 50;\"}", "return 50;")]
    [InlineData("{\n  \"code\": \"using UnityEngine;\\nDebug.Log(1);\"\n}", "using UnityEngine;\nDebug.Log(1);")]
    public void UnityTools_UnwrapJsonCodeIfPresent_ExtractsCodeProperly(string input, string expected)
    {
        string actual = UnityTools.UnwrapJsonCodeIfPresent(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnityTools_UnityRefresh_CleanParameter_HasAccurateDescription()
    {
        var refreshMethod = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityRefreshAsync));
        Assert.NotNull(refreshMethod);
        var cleanParam = refreshMethod.GetParameters().FirstOrDefault(p => p.Name == "clean");
        Assert.NotNull(cleanParam);
        var descAttr = cleanParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Contains("full script recompilation", descAttr.Description);
    }

    [Fact]
    public void UnityTools_AllTools_HaveNonEmptyDescriptions()
    {
        var methods = typeof(UnityTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        Assert.NotEmpty(toolMethods);
        foreach (var m in toolMethods)
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(desc);
            Assert.False(string.IsNullOrWhiteSpace(desc.Description));
        }
    }

    [Fact]
    public void UnityTools_UnityTest_Parameters_OnlyExposeCanonicalPluralParameters()
    {
        var method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync));
        Assert.NotNull(method);

        var paramNames = method.GetParameters().Select(p => p.Name).ToList();

        Assert.Contains("testNames", paramNames);
        Assert.Contains("groupNames", paramNames);
        Assert.Contains("categoryNames", paramNames);
        Assert.Contains("assemblyNames", paramNames);
        Assert.Contains("mode", paramNames);
        Assert.Contains("failedOnly", paramNames);
        Assert.Contains("coverage", paramNames);

        Assert.DoesNotContain("testName", paramNames);
        Assert.DoesNotContain("group", paramNames);
        Assert.DoesNotContain("filter", paramNames);
        Assert.DoesNotContain("category", paramNames);
        Assert.DoesNotContain("assembly", paramNames);

        var modeParam = method.GetParameters().First(p => p.Name == "mode");
        Assert.False(modeParam.HasDefaultValue);

        var failedOnlyParam = method.GetParameters().First(p => p.Name == "failedOnly");
        Assert.Equal(false, failedOnlyParam.DefaultValue);

        var coverageParam = method.GetParameters().First(p => p.Name == "coverage");
        Assert.Equal(false, coverageParam.DefaultValue);
    }

    [Fact]
    public void UnityTools_UnityTest_GroupNames_HasAccurateDescription()
    {
        var method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync));
        Assert.NotNull(method);

        var groupNamesParam = method.GetParameters().FirstOrDefault(p => p.Name == "groupNames");
        Assert.NotNull(groupNamesParam);

        var descAttr = groupNamesParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal(
            ".NET Regular Expression patterns to match test names, fixtures, or namespaces (e.g. ['.*Movement.*']). Evaluated as .NET Regex (do not use glob syntax like *Test*).",
            descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityTest_FailedOnly_HasAccurateDescription()
    {
        var method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync));
        Assert.NotNull(method);

        var failedOnlyParam = method.GetParameters().FirstOrDefault(p => p.Name == "failedOnly");
        Assert.NotNull(failedOnlyParam);

        var descAttr = failedOnlyParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal(
            "Only run tests that previously failed. Recommended for slow test suites; skip if tests run quickly.",
            descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityTest_Mode_HasAccurateDescription()
    {
        var method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync));
        Assert.NotNull(method);

        var modeParam = method.GetParameters().FirstOrDefault(p => p.Name == "mode");
        Assert.NotNull(modeParam);

        var descAttr = modeParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Equal(
            "Test execution mode: 'editmode' or 'playmode' (required). Must be specified explicitly. Use 'editmode' for unit tests and editor utilities; use 'playmode' for integration tests, scene loading, and MonoBehaviour runtime tests.",
            descAttr.Description);
    }

    [Fact]
    public void UnityTools_UnityTest_Coverage_HasAccurateDescription()
    {
        var method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync));
        Assert.NotNull(method);

        var coverageParam = method.GetParameters().FirstOrDefault(p => p.Name == "coverage");
        Assert.NotNull(coverageParam);

        var descAttr = coverageParam.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(descAttr);
        Assert.Contains("in-memory code coverage", descAttr.Description);
    }

    [Fact]
    public async Task UnityTest_ZeroTests_WithOtherModeMatches_FormatsCrossModeHint()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0,
            OtherModeMatches = ["MyCompany.GameplayTests.CombatTest"]
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, groupNames: [".*Combat.*"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching filter '.*Combat.*' (mode: editmode).", text);
        Assert.Contains("Matching tests were found in mode 'playmode' (1 matching test exists, e.g. 'MyCompany.GameplayTests.CombatTest'). Did you mean to run with mode 'playmode'?", text);
        Assert.Contains("Hint: Use unity_eval with 'return Inspect.Tests(\"Combat\");' to discover available tests in loaded assemblies.", text);
    }

    [Fact]
    public async Task UnityTest_ZeroTests_WithMultipleOtherModeMatches_FormatsCountAndExample()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0,
            OtherModeMatches = ["Suite.Test1", "Suite.Test2", "Suite.Test3"]
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.PlayMode, testNames: ["Suite.Test1"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching testNames 'Suite.Test1' (mode: playmode).", text);
        Assert.Contains("Matching tests were found in mode 'editmode' (3 matching tests exist, e.g. 'Suite.Test1'). Did you mean to run with mode 'editmode'?", text);
        Assert.Contains("Hint: Use unity_eval with 'return Inspect.Tests(\"Suite.Test1\");' to discover available tests in loaded assemblies.", text);
    }

    [Fact]
    public async Task UnityTest_ZeroTests_WithNearMatches_FormatsDidYouMeanSuggestions()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0,
            NearMatches =
            [
                "UnityLeanMcpTests.UnityInspectTests.Hierarchy_ActiveScene_ReturnsFormattedHierarchy",
                "UnityLeanMcpTests.UnityInspectTests.Hierarchy_TransformRoot_ReturnsFormattedHierarchy"
            ]
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["UnityInspectTests"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching testNames 'UnityInspectTests' (mode: editmode).", text);
        Assert.Contains("Did you mean one of these tests?", text);
        Assert.Contains("  • UnityLeanMcpTests.UnityInspectTests.Hierarchy_ActiveScene_ReturnsFormattedHierarchy", text);
        Assert.Contains("  • UnityLeanMcpTests.UnityInspectTests.Hierarchy_TransformRoot_ReturnsFormattedHierarchy", text);
        Assert.Contains("Hint: Use unity_eval with 'return Inspect.Tests(\"UnityInspectTests\");' to discover available tests in loaded assemblies.", text);
    }

    [Fact]
    public async Task UnityTest_ZeroTests_WithoutMatches_FormatsGenericModeHintAndInspectHint()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = true,
            FailCount = 0,
            PassCount = 0,
            SkipCount = 0
        };

        var result = await ctx.Tools.UnityTestAsync(mode: UnityTestMode.EditMode, testNames: ["NonExistentTest"]);

        Assert.True(result.IsError);
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("No tests found matching testNames 'NonExistentTest' (mode: editmode).", text);
        Assert.Contains("Hint: If the target tests require scene loading, MonoBehaviour lifecycle, or runtime execution (such as PlayMode integration tests), try running with mode 'playmode'.", text);
        Assert.Contains("Hint: Use unity_eval with 'return Inspect.Tests(\"NonExistentTest\");' to discover available tests in loaded assemblies.", text);
    }

    [Fact]
    public void TestDiscoveryHelper_DiscoverHints_FindsCrossModeAndNearMatches()
    {
        var mockItems = new List<TestDiscoveryHelper.DiscoveredItem>
        {
            new()
            {
                FullName = "MyCompany.Tests.PlayModeCombatTests.AttackTest",
                FixtureFullName = "MyCompany.Tests.PlayModeCombatTests",
                Name = "AttackTest",
                AssemblyName = "CombatTests",
                Mode = "playmode"
            },
            new()
            {
                FullName = "MyCompany.Tests.EditModeTests.MovementTest",
                FixtureFullName = "MyCompany.Tests.EditModeTests",
                Name = "MovementTest",
                AssemblyName = "EditTests",
                Mode = "editmode"
            },
            new()
            {
                FullName = "MyCompany.Tests.EditModeTests.JumpTest",
                FixtureFullName = "MyCompany.Tests.EditModeTests",
                Name = "JumpTest",
                AssemblyName = "EditTests",
                Mode = "editmode"
            }
        };

        TestDiscoveryHelper.DiscoverHints(
            currentMode: "editmode",
            testNames: ["MyCompany.Tests.PlayModeCombatTests.AttackTest"],
            groupNames: null,
            categoryNames: null,
            assemblyNames: null,
            out var otherModeMatches,
            out var nearMatches,
            preloadedItems: mockItems);

        Assert.Single(otherModeMatches);
        Assert.Equal("MyCompany.Tests.PlayModeCombatTests.AttackTest", otherModeMatches[0]);

        TestDiscoveryHelper.DiscoverHints(
            currentMode: "editmode",
            testNames: ["Movement"],
            groupNames: null,
            categoryNames: null,
            assemblyNames: null,
            out otherModeMatches,
            out nearMatches,
            preloadedItems: mockItems);

        Assert.Empty(otherModeMatches);
        Assert.Single(nearMatches);
        Assert.Equal("MyCompany.Tests.EditModeTests.MovementTest", nearMatches[0]);

        TestDiscoveryHelper.DiscoverHints(
            currentMode: "editmode",
            testNames: ["EditModeTests"],
            groupNames: null,
            categoryNames: null,
            assemblyNames: null,
            out otherModeMatches,
            out nearMatches,
            preloadedItems: mockItems);

        Assert.Equal(2, nearMatches.Count);
        Assert.Contains("MyCompany.Tests.EditModeTests.MovementTest", nearMatches);
        Assert.Contains("MyCompany.Tests.EditModeTests.JumpTest", nearMatches);
    }
}
