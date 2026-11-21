using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AssembliesType = UnityEditor.Compilation.AssembliesType;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
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
    [InlineData("unity_refresh", "Refreshes AssetDatabase and returns compiler and asset import diagnostics. Do not call immediately before unity_test or unity_eval, which automatically compile and refresh pending changes. C# compiler errors persist across no-op refreshes, while non-C# diagnostics (shaders, import failures) are captured only during active imports and not re-emitted if assets are unchanged. Normal refresh is fast when unchanged; set clean to true only when a full script recompilation is needed to recover from a stale or corrupted compiler cache; clean refreshes are more expensive.")]
    [InlineData("unity_eval", "Evaluates C# top-level statements in the active Unity Editor with top-level await. Pre-imports System, System.Collections.Generic, System.Linq, UnityEngine, UnityEditor, and UnityLeanMcp. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. Use 'return <value>;' to return results. Includes a built-in Inspect utility to speed up scene, object, asset, UI, and capture analysis—read the unity://inspect/help resource for its docs.")]
    [InlineData("unity_test", "Runs Unity tests in 'editmode' or 'playmode'. Automatically ensures compilation and refresh are settled prior to execution, so manual pre-refresh is unnecessary. The mode parameter is required; callers must determine whether target tests are EditMode or PlayMode before calling. Use 'editmode' for fast unit tests, editor utilities, and tests without player/runtime lifecycle. Use 'playmode' for integration tests, tests that load scenes, use MonoBehaviour lifecycle, or yield frames via UnityTest/IEnumerator. Use testNames for exact fully qualified name filters and groupNames for .NET regex filters; categoryNames and assemblyNames are also supported as string arrays. To discover available tests or verify exact test names before running, evaluate Inspect.Tests(\"<filter>\") via unity_eval. Set failedOnly to re-run only failed tests (recommended for slow suites). Set coverage to true to capture in-memory code coverage.")]
    [InlineData("unity_coverage", "Queries in-memory code coverage for specified files or directories from the latest test run. Does not run tests. Accepts individual C# files and directory paths (which include all scripts in that folder and its subdirectories). Returns line coverage percentages sorted from lowest to highest coverage and lists uncovered line spans for each file. Set uncoveredOnly to true to omit 100%-covered files and files with 0 executable points.")]
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

    private sealed class MockTestTreeNode : TestDiscoveryHelper.ITestTreeNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsSuite { get; set; }
        public bool HasChildren { get; set; }
        public bool IsFixtureSuite { get; set; }
        public Type? FixtureType { get; set; }
        public MethodInfo? MethodInfo { get; set; }
        public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();
        public IEnumerable<TestDiscoveryHelper.ITestTreeNode> Children { get; set; } = Array.Empty<TestDiscoveryHelper.ITestTreeNode>();
    }

    private sealed class NamedStubAssembly : Assembly
    {
        private readonly AssemblyName _name;

        public NamedStubAssembly(string name)
        {
            _name = new AssemblyName(name);
        }

        public override AssemblyName GetName() => _name;
        public override AssemblyName[] GetReferencedAssemblies() => Array.Empty<AssemblyName>();
        public override Type[] GetTypes() => Array.Empty<Type>();
    }

    private static void SampleCustomNamedMethod() { }

    private static MockTestTreeNode BuildRichDomainTree()
    {
        var customMethodInfo = typeof(TestFilterAndDiscoveryTests).GetMethod(
            nameof(SampleCustomNamedMethod),
            BindingFlags.NonPublic | BindingFlags.Static);

        return new MockTestTreeNode
        {
            Name = "PureCSharp.Domain.Tests",
            FullName = "PureCSharp.Domain.Tests",
            IsSuite = true,
            HasChildren = true,
            Children = new TestDiscoveryHelper.ITestTreeNode[]
            {
                new MockTestTreeNode
                {
                    Name = "EmptySuite",
                    FullName = "MyGame.Domain.EmptySuite",
                    IsSuite = true,
                    IsFixtureSuite = true,
                    HasChildren = false
                },
                new MockTestTreeNode
                {
                    Name = "MathTests",
                    FullName = "MyGame.Domain.MathTests",
                    IsSuite = true,
                    IsFixtureSuite = true,
                    HasChildren = true,
                    Categories = new[] { "Unit" },
                    Children = new TestDiscoveryHelper.ITestTreeNode[]
                    {
                        new MockTestTreeNode
                        {
                            Name = "Add",
                            FullName = "MyGame.Domain.MathTests.Add",
                            IsSuite = true,
                            IsFixtureSuite = false,
                            HasChildren = true,
                            Categories = new[] { "Fast" },
                            Children = new[]
                            {
                                new MockTestTreeNode
                                {
                                    Name = "Add(1,2)",
                                    FullName = "MyGame.Domain.MathTests.Add(1,2)",
                                    MethodInfo = customMethodInfo,
                                    Categories = new[] { "Fast" }
                                },
                                new MockTestTreeNode
                                {
                                    Name = "CustomAddName",
                                    FullName = "MyGame.Domain.MathTests.CustomAddName",
                                    MethodInfo = customMethodInfo,
                                    Categories = new[] { "Boundary" }
                                }
                            }
                        },
                        new MockTestTreeNode
                        {
                            Name = "Subtract",
                            FullName = "MyGame.Domain.MathTests.Subtract",
                            Categories = new[] { "Uncategorized" }
                        }
                    }
                },
                new MockTestTreeNode
                {
                    Name = "ParamFixture(1)",
                    FullName = "MyGame.Domain.ParamFixture(1)",
                    IsSuite = true,
                    IsFixtureSuite = true,
                    HasChildren = true,
                    Children = new[]
                    {
                        new MockTestTreeNode
                        {
                            Name = "Works",
                            FullName = "MyGame.Domain.ParamFixture(1).Works",
                            Categories = new[] { "Uncategorized" }
                        }
                    }
                },
                new MockTestTreeNode
                {
                    Name = "GenericFixture`1[System.Int32]",
                    FullName = "MyGame.Domain.GenericFixture`1[System.Int32]",
                    IsSuite = true,
                    IsFixtureSuite = true,
                    HasChildren = true,
                    Children = new[]
                    {
                        new MockTestTreeNode
                        {
                            Name = "GenericCheck",
                            FullName = "MyGame.Domain.GenericFixture`1[System.Int32].GenericCheck"
                        }
                    }
                },
                new MockTestTreeNode
                {
                    Name = "Outer",
                    FullName = "MyGame.Domain.Outer",
                    IsSuite = true,
                    IsFixtureSuite = true,
                    HasChildren = true,
                    Children = new[]
                    {
                        new MockTestTreeNode
                        {
                            Name = "Outer+Inner",
                            FullName = "MyGame.Domain.Outer+Inner",
                            IsSuite = true,
                            IsFixtureSuite = true,
                            HasChildren = true,
                            Children = new[]
                            {
                                new MockTestTreeNode
                                {
                                    Name = "NestedTest",
                                    FullName = "MyGame.Domain.Outer+Inner.NestedTest"
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    [Fact]
    public void TestDiscoveryHelper_CompilationPipeline_ClassifiesPureCSharpEditModeAssembly()
    {
        var thisAssemblyName = typeof(TestFilterAndDiscoveryTests).Assembly.GetName().Name!;
        CompilationPipeline.GetAssembliesByTypeFunc = assembliesType => assembliesType switch
        {
            AssembliesType.Editor => [new UnityEditor.Compilation.Assembly { name = thisAssemblyName }],
            AssembliesType.Player => [],
            _ => []
        };

        try
        {
            var items = TestDiscoveryHelper.DiscoverAllTests();
            var selfItem = items.First(i => i.FullName.Contains(nameof(TestDiscoveryHelper_CompilationPipeline_ClassifiesPureCSharpEditModeAssembly), StringComparison.Ordinal));
            Assert.Equal("editmode", selfItem.Mode);

            string output = Inspect.Tests(nameof(TestDiscoveryHelper_CompilationPipeline_ClassifiesPureCSharpEditModeAssembly));
            Assert.Contains($"• {selfItem.FullName} [editmode]", output);
        }
        finally
        {
            CompilationPipeline.GetAssembliesByTypeFunc = null;
        }
    }

    [Fact]
    public void TestDiscoveryHelper_NUnitTree_ExtractsParameterizedCustomNameGenericNestedAndStripsUncategorized()
    {
        var editAsm = new NamedStubAssembly("PureCSharp.Domain.Tests");
        var playAsm = new NamedStubAssembly("MyGame.Runtime.Tests");
        var modeOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PureCSharp.Domain.Tests"] = "editmode",
            ["MyGame.Runtime.Tests"] = "playmode"
        };

        TestDiscoveryHelper.NUnitTreeBuilderOverride = asm =>
        {
            if (ReferenceEquals(asm, editAsm)) return BuildRichDomainTree();
            return new MockTestTreeNode
            {
                Name = "MyGame.Runtime.Tests",
                FullName = "MyGame.Runtime.Tests",
                IsSuite = true,
                HasChildren = true,
                Children = new[]
                {
                    new MockTestTreeNode
                    {
                        Name = "PlayerTests",
                        FullName = "MyGame.Runtime.PlayerTests",
                        IsSuite = true,
                        IsFixtureSuite = true,
                        HasChildren = true,
                        Categories = new[] { "Integration" },
                        Children = new[]
                        {
                            new MockTestTreeNode
                            {
                                Name = "SpawnsInScene",
                                FullName = "MyGame.Runtime.PlayerTests.SpawnsInScene"
                            }
                        }
                    }
                }
            };
        };

        try
        {
            var discovered = TestDiscoveryHelper.DiscoverAllTests(new Assembly[] { editAsm, playAsm }, modeOverrides);

            Assert.Equal(7, discovered.Count);
            Assert.DoesNotContain(discovered, d => d.FullName.Contains("EmptySuite", StringComparison.Ordinal));

            var add12 = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.MathTests.Add(1,2)");
            Assert.Equal("editmode", add12.Mode);
            Assert.Equal("PureCSharp.Domain.Tests", add12.AssemblyName);
            Assert.Equal("MyGame.Domain.MathTests", add12.FixtureFullName);
            Assert.Equal(new[] { "Unit", "Fast" }, add12.Categories);

            var custom = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.MathTests.CustomAddName");
            Assert.Equal("MyGame.Domain.MathTests", custom.FixtureFullName);
            Assert.Equal("MyGame.Domain.MathTests." + nameof(SampleCustomNamedMethod), custom.MethodFullName);
            Assert.Equal(new[] { "Unit", "Fast", "Boundary" }, custom.Categories);

            var subtract = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.MathTests.Subtract");
            Assert.Equal(new[] { "Unit" }, subtract.Categories);

            var paramFixture = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.ParamFixture(1).Works");
            Assert.Equal("MyGame.Domain.ParamFixture(1)", paramFixture.FixtureFullName);
            Assert.Empty(paramFixture.Categories);

            var generic = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.GenericFixture`1[System.Int32].GenericCheck");
            Assert.Equal("MyGame.Domain.GenericFixture`1[System.Int32]", generic.FixtureFullName);

            var nested = Assert.Single(discovered, d => d.FullName == "MyGame.Domain.Outer+Inner.NestedTest");
            Assert.Equal("MyGame.Domain.Outer+Inner", nested.FixtureFullName);

            var spawn = Assert.Single(discovered, d => d.FullName == "MyGame.Runtime.PlayerTests.SpawnsInScene");
            Assert.Equal("playmode", spawn.Mode);
            Assert.Equal("MyGame.Runtime.Tests", spawn.AssemblyName);
        }
        finally
        {
            TestDiscoveryHelper.NUnitTreeBuilderOverride = null;
        }
    }

    [Fact]
    public void InspectTests_FormatsModeCategoriesAssembly_PreservesDuplicateNamesAcrossAssemblies_AndSupportsFilters()
    {
        var asmA = new NamedStubAssembly("AssemblyA.Tests");
        var asmB = new NamedStubAssembly("AssemblyB.Tests");
        var targetAsm = typeof(TestFilterAndDiscoveryTests).Assembly;

        CompilationPipeline.GetAssembliesByTypeFunc = assembliesType => assembliesType switch
        {
            AssembliesType.Editor => [new UnityEditor.Compilation.Assembly { name = targetAsm.GetName().Name! }],
            AssembliesType.Player => [],
            _ => []
        };

        TestDiscoveryHelper.NUnitTreeBuilderOverride = asm =>
        {
            if (!ReferenceEquals(asm, targetAsm)) return null!;
            return new MockTestTreeNode
            {
                Name = "Root",
                FullName = "Root",
                IsSuite = true,
                HasChildren = true,
                Children = new[]
                {
                    new MockTestTreeNode
                    {
                        Name = "MathTests",
                        FullName = "MyGame.Domain.MathTests",
                        IsSuite = true,
                        IsFixtureSuite = true,
                        HasChildren = true,
                        Categories = new[] { "Unit" },
                        Children = new[]
                        {
                            new MockTestTreeNode
                            {
                                Name = "Add(1,2)",
                                FullName = "MyGame.Domain.MathTests.Add(1,2)",
                                Categories = new[] { "Fast" }
                            },
                            new MockTestTreeNode
                            {
                                Name = "Add(3,4)",
                                FullName = "MyGame.Domain.MathTests.Add(3,4)",
                                Categories = new[] { "Boundary" }
                            },
                            new MockTestTreeNode
                            {
                                Name = "Plain",
                                FullName = "MyGame.Domain.MathTests.Plain",
                                Categories = new[] { "Uncategorized" }
                            }
                        }
                    }
                }
            };
        };

        try
        {
            string all = Inspect.Tests(filter: "MyGame.Domain.MathTests");
            Assert.Contains("Available Tests (3):", all);
            Assert.Contains("  • MyGame.Domain.MathTests.Add(1,2) [editmode] [Unit, Fast] (UnityLeanMcp.Mcp.Tests)", all);
            Assert.Contains("  • MyGame.Domain.MathTests.Add(3,4) [editmode] [Unit, Boundary] (UnityLeanMcp.Mcp.Tests)", all);
            Assert.Contains("  • MyGame.Domain.MathTests.Plain [editmode] [Unit] (UnityLeanMcp.Mcp.Tests)", all);
            Assert.DoesNotContain("Uncategorized", all);

            string editOnly = Inspect.Tests(filter: "MyGame.Domain.MathTests", mode: "editmode");
            Assert.Contains("Available Tests (3):", editOnly);

            string playOnly = Inspect.Tests(filter: "MyGame.Domain.MathTests", mode: "PlayMode");
            Assert.Equal("(No tests found matching filter 'MyGame.Domain.MathTests', mode 'playmode')", playOnly);

            string byCategory = Inspect.Tests(category: "Boundary");
            Assert.Contains("Available Tests (1):", byCategory);
            Assert.Contains("MyGame.Domain.MathTests.Add(3,4)", byCategory);

            string byWildcardCategory = Inspect.Tests(category: "*Fast*");
            Assert.Contains("Available Tests (1):", byWildcardCategory);
            Assert.Contains("MyGame.Domain.MathTests.Add(1,2)", byWildcardCategory);

            string invalidMode = Inspect.Tests(mode: "invalid");
            Assert.Equal("(Invalid mode 'invalid'. Expected 'editmode' or 'playmode')", invalidMode);

            string twoArgPositional = Inspect.Tests("MyGame.Domain.MathTests.Add", 1);
            Assert.Contains("Available Tests (2):", twoArgPositional);
            Assert.Contains("MyGame.Domain.MathTests.Add(1,2)", twoArgPositional);
            Assert.DoesNotContain("MyGame.Domain.MathTests.Add(3,4)", twoArgPositional);
            Assert.Contains(Inspect.ItemTruncationMarker, twoArgPositional);

            string twoArgNamed = Inspect.Tests("MyGame.Domain.MathTests.Add", maxItems: 1);
            Assert.Equal(twoArgPositional, twoArgNamed);

            // Verify two distinct assemblies defining the same test FullName are both kept
            TestDiscoveryHelper.NUnitTreeBuilderOverride = asm => new MockTestTreeNode
            {
                Name = "SharedTests",
                FullName = "SharedTests",
                IsSuite = true,
                IsFixtureSuite = true,
                HasChildren = true,
                Children = new[]
                {
                    new MockTestTreeNode
                    {
                        Name = "SameName",
                        FullName = "SharedTests.SameName"
                    }
                }
            };

            var twoAssemblyItems = TestDiscoveryHelper.DiscoverAllTests(new Assembly[] { asmA, asmB });
            Assert.Equal(2, twoAssemblyItems.Count);
            Assert.NotEqual(twoAssemblyItems[0].AssemblyName, twoAssemblyItems[1].AssemblyName);
        }
        finally
        {
            TestDiscoveryHelper.NUnitTreeBuilderOverride = null;
            CompilationPipeline.GetAssembliesByTypeFunc = null;
        }
    }

    [Fact]
    public void TestDiscoveryHelper_DiscoverHints_MatchesAnchoredGroupNamesCategoryOrRegexAndStrictAssemblyName()
    {
        var items = new List<TestDiscoveryHelper.DiscoveredItem>
        {
            new()
            {
                FullName = "MyGame.Domain.MathTests.CustomAddName",
                FixtureFullName = "MyGame.Domain.MathTests",
                MethodFullName = "MyGame.Domain.MathTests.Add",
                Name = "CustomAddName",
                AssemblyName = "PureCSharp.Domain.Tests",
                Categories = ["Unit", "Fast"],
                Mode = "editmode"
            },
            new()
            {
                FullName = "MyGame.Domain.MathTests.NoCategories",
                FixtureFullName = "MyGame.Domain.MathTests",
                MethodFullName = "MyGame.Domain.MathTests.NoCategories",
                Name = "NoCategories",
                AssemblyName = "PureCSharp.Domain.Tests",
                Categories = null!,
                Mode = "editmode"
            }
        };

        // Anchored groupNames matching FixtureFullName and MethodFullName in opposite mode
        TestDiscoveryHelper.DiscoverHints(
            currentMode: "playmode",
            testNames: null,
            groupNames: ["^MyGame\\.Domain\\.MathTests$"],
            categoryNames: ["^Fas.*$", "OtherOrCategory", "!Slow"],
            assemblyNames: ["PureCSharp.Domain.Tests"],
            out var otherModeMatches,
            out var nearMatches,
            preloadedItems: items);

        Assert.Single(otherModeMatches);
        Assert.Equal("MyGame.Domain.MathTests.CustomAddName", otherModeMatches[0]);
        Assert.Empty(nearMatches);

        TestDiscoveryHelper.DiscoverHints(
            currentMode: "playmode",
            testNames: null,
            groupNames: ["^MyGame\\.Domain\\.MathTests\\.Add$"],
            categoryNames: null,
            assemblyNames: null,
            out otherModeMatches,
            out nearMatches,
            preloadedItems: items);

        Assert.Single(otherModeMatches);
        Assert.Equal("MyGame.Domain.MathTests.CustomAddName", otherModeMatches[0]);

        // UTF AssemblyNameFilter rejects ".dll" suffix, so DiscoverHints should not suggest switching modes
        TestDiscoveryHelper.DiscoverHints(
            currentMode: "playmode",
            testNames: ["MyGame.Domain.MathTests.CustomAddName"],
            groupNames: null,
            categoryNames: null,
            assemblyNames: ["PureCSharp.Domain.Tests.dll"],
            out otherModeMatches,
            out nearMatches,
            preloadedItems: items);

        Assert.Empty(otherModeMatches);
    }

    [Fact]
    public void TestDiscoveryHelper_FallsBackToReflectionWhenTreeBuilderOrCompilationPipelineThrows()
    {
        CompilationPipeline.GetAssembliesByTypeFunc = _ => throw new InvalidOperationException("Off main thread");
        TestDiscoveryHelper.NUnitTreeBuilderOverride = _ => throw new InvalidOperationException("Builder unavailable");
        try
        {
            var items = TestDiscoveryHelper.DiscoverAllTests();
            Assert.Contains(items, t => t.FullName.Contains(nameof(TestFilterAndDiscoveryTests), StringComparison.Ordinal));
        }
        finally
        {
            TestDiscoveryHelper.NUnitTreeBuilderOverride = null;
            CompilationPipeline.GetAssembliesByTypeFunc = null;
        }
    }
}
