using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class DiagnosticFormatterTests
{
    [Theory]
    [InlineData("  at MySuite.Test () [0x00000] in C:/Code/Assets/Tests/Test.cs:55", "C:/Code/Assets/Tests/Test.cs", 55)]
    [InlineData("   at MySuite.Test() in C:\\Code\\Assets\\Tests\\Test.cs:line 102", "C:\\Code\\Assets\\Tests\\Test.cs", 102)]
    [InlineData("MySuite.Test () (at Assets/Tests/Test.cs:23)", "Assets/Tests/Test.cs", 23)]
    [InlineData("  at NUnit.Framework.Assert.Fail() in <filename unknown>:0\n  at MySuite.Run() in Assets/Tests/Run.cs:99", "Assets/Tests/Run.cs", 99)]
    public void ExtractSourceLocation_ExtractsExpectedFileAndLine(string stackTrace, string expectedFile, int expectedLine)
    {
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/ProjectRoot");

        Assert.Equal(expectedFile, file);
        Assert.Equal(expectedLine, line);
        Assert.NotNull(uri);
        Assert.StartsWith("file:///", uri);
        Assert.EndsWith($"#L{expectedLine}", uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  at MySuite.TestMethod() line 42")]
    [InlineData("  at NUnit.Framework.Assert.AreEqual() in <filename unknown>:0")]
    public void ExtractSourceLocation_WhenNoSourceLocation_ReturnsNulls(string? stackTrace)
    {
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/ProjectRoot");

        Assert.Null(file);
        Assert.Null(line);
        Assert.Null(uri);
    }

    [Fact]
    public void ParseCompilerDiagnostics_ParsesErrorsAndWarningsCorrectly()
    {
        string text = @"Assets/Scripts/Player.cs(10,15): error CS0103: The name 'foo' does not exist in the current context
Assets/Scripts/Enemy.cs(42,5): warning CS0219: The variable 'bar' is assigned but its value is never used";

        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diagnostics.Count);

        Assert.Equal("Assets/Scripts/Player.cs", diagnostics[0].File);
        Assert.Equal(10, diagnostics[0].Line);
        Assert.Equal(15, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("CS0103", diagnostics[0].Code);
        Assert.Equal("The name 'foo' does not exist in the current context", diagnostics[0].Message);

        Assert.Equal("Assets/Scripts/Enemy.cs", diagnostics[1].File);
        Assert.Equal(42, diagnostics[1].Line);
        Assert.Equal(5, diagnostics[1].Column);
        Assert.Equal("warning", diagnostics[1].Severity);
        Assert.Equal("CS0219", diagnostics[1].Code);
        Assert.Equal("The variable 'bar' is assigned but its value is never used", diagnostics[1].Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Compilation succeeded with no diagnostics.")]
    public void ParseCompilerDiagnostics_WhenNoDiagnostics_ReturnsEmpty(string? text)
    {
        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ParseCompilerDiagnostics_NonCsErrors_ShaderAndMissingScript_ParsesSuccessfully()
    {
        string text = "Assets/Shaders/Water.shader(45): error undeclared identifier 'foo'\n" +
                      "Assets/Prefabs/Player.prefab: error The referenced script on this Behaviour is missing!";

        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diagnostics.Count);

        Assert.Equal("Assets/Shaders/Water.shader", diagnostics[0].File);
        Assert.Equal(45, diagnostics[0].Line);
        Assert.Equal(0, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("", diagnostics[0].Code);
        Assert.Equal("undeclared identifier 'foo'", diagnostics[0].Message);

        Assert.Equal("Assets/Prefabs/Player.prefab", diagnostics[1].File);
        Assert.Equal(0, diagnostics[1].Line);
        Assert.Equal(0, diagnostics[1].Column);
        Assert.Equal("error", diagnostics[1].Severity);
        Assert.Equal("", diagnostics[1].Code);
        Assert.Equal("The referenced script on this Behaviour is missing!", diagnostics[1].Message);
    }

    [Fact]
    public void FormatCompilerDiagnostics_NonCsError_GeneratesClickableFileUri()
    {
        string text = "Assets/Shaders/Water.shader(45): error undeclared identifier 'foo'";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("file:///C:/Code/MyProject/Assets/Shaders/Water.shader#L45: error: undeclared identifier 'foo'", formatted);
        Assert.Contains("Refresh failed.", formatted);
    }

    [Fact]
    public void ParseCompilerDiagnostics_ColonDelimitedLocations_ParsesSuccessfully()
    {
        string text = "Assets/Shaders/Water.shader:45: error undeclared identifier 'foo'\n" +
                      "Assets/Shaders/Surface.shader:12:8: warning division by zero";

        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diagnostics.Count);

        Assert.Equal("Assets/Shaders/Water.shader", diagnostics[0].File);
        Assert.Equal(45, diagnostics[0].Line);
        Assert.Equal(0, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("undeclared identifier 'foo'", diagnostics[0].Message);

        Assert.Equal("Assets/Shaders/Surface.shader", diagnostics[1].File);
        Assert.Equal(12, diagnostics[1].Line);
        Assert.Equal(8, diagnostics[1].Column);
        Assert.Equal("warning", diagnostics[1].Severity);
        Assert.Equal("division by zero", diagnostics[1].Message);
    }

    [Fact]
    public void FormatCompilerDiagnostics_ColonDelimitedLocation_GeneratesClickableFileUri()
    {
        string text = "Assets/Shaders/Water.shader:45: error undeclared identifier 'foo'";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("file:///C:/Code/MyProject/Assets/Shaders/Water.shader#L45: error: undeclared identifier 'foo'", formatted);
        Assert.Contains("Refresh failed.", formatted);
    }

    [Fact]
    public void ParseCompilerDiagnostics_StandaloneDiagnosticWithoutFile_ParsesCorrectly()
    {
        string text = "error: The referenced script on this Behaviour (Game Object 'Player') is missing!";
        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Single(diagnostics);
        Assert.Equal("", diagnostics[0].File);
        Assert.Equal(0, diagnostics[0].Line);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("The referenced script on this Behaviour (Game Object 'Player') is missing!", diagnostics[0].Message);
    }

    [Fact]
    public void FormatCompilerDiagnostics_StandaloneError_FormatsWithUnknownLocationAndNoDoubleColon()
    {
        string text = "error: The referenced script on this Behaviour is missing!";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("Unknown: error: The referenced script on this Behaviour is missing!", formatted);
        Assert.DoesNotContain("error::", formatted);
        Assert.Contains("Refresh failed.", formatted);
    }

    [Fact]
    public void FormatCompilerDiagnostics_MixedDiagnostics_AllParsedWithoutDroppingAny()
    {
        string text = "Assets/Scripts/Player.cs(12,34): error CS0103: The name 'foo' does not exist in the current context\n" +
                      "Assets/Shaders/Water.shader(45): error undeclared identifier 'bar'\n" +
                      "Assets/Prefabs/Player.prefab: error The referenced script on this Behaviour is missing!\n" +
                      "Assets/Textures/bad.png: error Asset import failed: \"Assets/Textures/bad.png\"\n" +
                      "Assets/Editor/MyAssetPostprocessor.cs(15): error System.NullReferenceException: Object reference not set to an instance of an object\n" +
                      "error: Standalone error without file path\n" +
                      "Assets/Scripts/Other.cs(5,1): warning CS0168: The variable 'unused' is declared but never used";

        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);
        Assert.Equal(7, diagnostics.Count);

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("file:///C:/Code/MyProject/Assets/Scripts/Player.cs#L12: error CS0103: The name 'foo' does not exist", formatted);
        Assert.Contains("file:///C:/Code/MyProject/Assets/Shaders/Water.shader#L45: error: undeclared identifier 'bar'", formatted);
        Assert.Contains("file:///C:/Code/MyProject/Assets/Prefabs/Player.prefab: error: The referenced script on this Behaviour is missing!", formatted);
        Assert.Contains("file:///C:/Code/MyProject/Assets/Textures/bad.png: error: Asset import failed: \"Assets/Textures/bad.png\"", formatted);
        Assert.Contains("file:///C:/Code/MyProject/Assets/Editor/MyAssetPostprocessor.cs#L15: error: System.NullReferenceException: Object reference not set to an instance of an object", formatted);
        Assert.Contains("Unknown: error: Standalone error without file path", formatted);
        Assert.Contains("file:///C:/Code/MyProject/Assets/Scripts/Other.cs#L5: warning CS0168: The variable 'unused' is declared but never used", formatted);
        Assert.Contains("Refresh failed.", formatted);
        Assert.DoesNotContain("::", formatted);
    }

    [Fact]
    public void ParseCompilerDiagnostics_WindowsAbsolutePathColonDelimited_ParsesSuccessfully()
    {
        string text = @"C:\Code\MyProject\Assets\Shaders\Water.shader:45:12: error undeclared identifier 'foo'" + "\n" +
                      "C:/Code/MyProject/Assets/Shaders/Surface.shader:12:8: warning division by zero\n" +
                      @"C:\Code\MyProject\Assets\Prefabs\Player.prefab: error Missing script";

        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(3, diagnostics.Count);

        Assert.Equal(@"C:\Code\MyProject\Assets\Shaders\Water.shader", diagnostics[0].File);
        Assert.Equal(45, diagnostics[0].Line);
        Assert.Equal(12, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("undeclared identifier 'foo'", diagnostics[0].Message);

        Assert.Equal("C:/Code/MyProject/Assets/Shaders/Surface.shader", diagnostics[1].File);
        Assert.Equal(12, diagnostics[1].Line);
        Assert.Equal(8, diagnostics[1].Column);
        Assert.Equal("warning", diagnostics[1].Severity);
        Assert.Equal("division by zero", diagnostics[1].Message);

        Assert.Equal(@"C:\Code\MyProject\Assets\Prefabs\Player.prefab", diagnostics[2].File);
        Assert.Equal(0, diagnostics[2].Line);
        Assert.Equal("error", diagnostics[2].Severity);
        Assert.Equal("Missing script", diagnostics[2].Message);
    }

    [Fact]
    public void ParseCompilerDiagnostics_SpaceAfterCommaInLocation_ParsesLineAndColumn()
    {
        string text = "Assets/Scripts/Player.cs(10, 5): error CS0103: The name 'foo' does not exist";
        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Single(diagnostics);
        Assert.Equal("Assets/Scripts/Player.cs", diagnostics[0].File);
        Assert.Equal(10, diagnostics[0].Line);
        Assert.Equal(5, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("CS0103", diagnostics[0].Code);
    }

    [Fact]
    public void FormatCompilerDiagnostics_WindowsAbsolutePathColonDelimited_GeneratesClickableFileUri()
    {
        string text = @"C:\Code\MyProject\Assets\Shaders\Water.shader:45:12: error undeclared identifier 'foo'";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("file:///C:/Code/MyProject/Assets/Shaders/Water.shader#L45: error: undeclared identifier 'foo'", formatted);
        Assert.Contains("Refresh failed.", formatted);
    }

    [Fact]
    public void ExtractSourceLocation_StackTraceWithPackageVersionAndHash_ExtractsCorrectLocation()
    {
        string stackTrace = "  at PackageNamespace.Class.Method () [0x00001] in Packages/com.company.pkg@1.0.0+hash123/Runtime/Script.cs:42";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Code/MyProject");

        Assert.Equal("Packages/com.company.pkg@1.0.0+hash123/Runtime/Script.cs", file);
        Assert.Equal(42, line);
        Assert.Contains("file:///C:/Code/MyProject/Packages/com.company.pkg@1.0.0+hash123/Runtime/Script.cs#L42", uri);
    }

    [Fact]
    public void ExtractSourceLocation_StackTraceWithParenthesesAndSpaces_ExtractsCorrectLocation()
    {
        string stackTrace = @"  at MyTest.Run () in C:\Build (x64)\Assets\Tests\MyTest.cs:line 25";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Build (x64)");

        Assert.Equal(@"C:\Build (x64)\Assets\Tests\MyTest.cs", file);
        Assert.Equal(25, line);
        Assert.Equal("file:///C:/Build%20(x64)/Assets/Tests/MyTest.cs#L25", uri);
    }

    [Fact]
    public void ExtractSourceLocation_RelativePathWithParenthesesAndSpaces_ExtractsCorrectLocation()
    {
        string stackTrace = @"  at MyTest.Run () in Plugins (x86)/Tests/MyTest.cs:line 25";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("Plugins (x86)/Tests/MyTest.cs", file);
        Assert.Equal(25, line);
        Assert.Equal("file:///C:/Project/Plugins%20(x86)/Tests/MyTest.cs#L25", uri);
    }

    [Fact]
    public void ExtractSourceLocation_RelativePathWithSpaces_ExtractsCorrectLocation()
    {
        string stackTrace = @"  at MyTest.Run () in My Folder/Tests/MyTest.cs:line 25";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("My Folder/Tests/MyTest.cs", file);
        Assert.Equal(25, line);
        Assert.Equal("file:///C:/Project/My%20Folder/Tests/MyTest.cs#L25", uri);
    }

    [Fact]
    public void ExtractSourceLocation_UnityFormatWithParenthesesAndSpaces_ExtractsCorrectLocation()
    {
        string stackTrace = @"MySuite.Test () (at Plugins (x86)/Test.cs:23)";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("Plugins (x86)/Test.cs", file);
        Assert.Equal(23, line);
        Assert.Equal("file:///C:/Project/Plugins%20(x86)/Test.cs#L23", uri);
    }

    [Fact]
    public void ExtractSourceLocation_StrippedTraceBareAtWithSpaces_ExtractsCorrectLocation()
    {
        string stackTrace = @"  at My Folder/Test.cs:42";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("My Folder/Test.cs", file);
        Assert.Equal(42, line);
        Assert.Equal("file:///C:/Project/My%20Folder/Test.cs#L42", uri);
    }

    [Fact]
    public void ExtractSourceLocation_MethodWithInParametersAndParentheses_ExtractsCorrectLocation()
    {
        string stackTrace = @"  at MyTest.Run (in int value, in string other) in Plugins (x86)/Tests/MyTest.cs:line 25";
        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("Plugins (x86)/Tests/MyTest.cs", file);
        Assert.Equal(25, line);
        Assert.Equal("file:///C:/Project/Plugins%20(x86)/Tests/MyTest.cs#L25", uri);
    }

    [Fact]
    public void ParseCompilerDiagnostics_PathWithParenthesesAndSpaces_ParsesSuccessfully()
    {
        string text = "Assets/Plugins (x86)/Plugin.cs(10, 5): error CS0103: The name 'foo' does not exist";
        var diagnostics = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Single(diagnostics);
        Assert.Equal("Assets/Plugins (x86)/Plugin.cs", diagnostics[0].File);
        Assert.Equal(10, diagnostics[0].Line);
        Assert.Equal(5, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("CS0103", diagnostics[0].Code);
    }

    [Fact]
    public void FormatCompilerDiagnostics_UnquotedAssetImportFailure_GeneratesClickableFileUri()
    {
        string text = "Assets/Textures/bad.png: error Asset import failed: Assets/Textures/bad.png";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Refresh failed.",
            isSuccess: false);

        Assert.Contains("file:///C:/Code/MyProject/Assets/Textures/bad.png: error: Asset import failed: Assets/Textures/bad.png", formatted);
        Assert.Contains("Refresh failed.", formatted);
    }

    [Fact]
    public void FormatCompilerDiagnostics_WhenRawTextIsOversized_PreservesSuccessTrailerWithinLimit()
    {
        string diagnosticText = new string('r', McpOutputLimits.MaxFormattedOutputCharacters * 2);

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            diagnosticText,
            projectRoot: null,
            successTrailer: "Refresh succeeded.",
            isSuccess: true);

        Assert.True(formatted.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.EndsWith("Refresh succeeded.", formatted);
        Assert.Contains(McpOutputLimits.AggregateOutputTruncationMarker, formatted);
    }

    [Fact]
    public void FormatCompilerDiagnostics_WhenManyStructuredErrors_PreservesFailureTrailerWithinLimit()
    {
        string diagnosticText = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 10_000).Select(i =>
                $"Assets/Scripts/Error{i}.cs(1,1): error CS1000: Failure {i}"));

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            diagnosticText,
            projectRoot: "/project",
            failureTrailer: "Compilation failed.");

        Assert.True(formatted.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.True(Encoding.UTF8.GetByteCount(formatted) <= McpOutputLimits.MaxFormattedOutputBytes);
        Assert.EndsWith("Compilation failed.", formatted);
        Assert.Contains(McpOutputLimits.AggregateOutputTruncationMarker, formatted);
    }

    [Fact]
    public void FormatCompilerDiagnostics_WhenDiagnosticMessageIsOversized_PreservesFailureTrailerWithinLimit()
    {
        string diagnosticText =
            $"Assets/Scripts/Error.cs(1,1): error CS1000: {new string('m', McpOutputLimits.MaxFormattedOutputCharacters * 2)}";

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            diagnosticText,
            projectRoot: "/project",
            failureTrailer: "Compilation failed.");

        Assert.True(formatted.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.True(Encoding.UTF8.GetByteCount(formatted) <= McpOutputLimits.MaxFormattedOutputBytes);
        Assert.EndsWith("Compilation failed.", formatted);
        Assert.Contains(McpOutputLimits.AggregateOutputTruncationMarker, formatted);
    }

    [Fact]
    public async Task DiagnosticFormatter_CustomImplementationCanBeInjectedIntoUnityTools()
    {
        var customFormatter = new TestCustomDiagnosticFormatter();
        using var ctx = new ToolFormattingTestContext();
        var customTools = new UnityTools(ctx.Client, ctx.PathResolver, customFormatter);

        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = true,
            Message = "Dummy message"
        };

        var refreshResult = await customTools.UnityRefreshAsync();
        Assert.True(customFormatter.FormatCompilerDiagnosticsCalled);

        ctx.Client.TestRunResultToReturn = new UnityTestRunResult
        {
            Success = false,
            FailedTests =
            {
                new FailedTestInfo { Name = "Test1", StackTrace = "dummy stack trace" }
            }
        };

        var testResult = await customTools.UnityTestAsync(mode: UnityTestMode.EditMode);
        Assert.True(customFormatter.ExtractSourceLocationCalled);
    }

    [Fact]
    public void DiagnosticFormatter_ParseCompilerDiagnostics_WhenPipeSeparated_ParsesMultipleDiagnostics()
    {
        string text = "eval(1,4): error CS1002: ; expected | eval(1,1): error CS0103: The name 'xyz' does not exist in the current context";
        var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diags.Count);
        Assert.Equal("eval", diags[0].File);
        Assert.Equal(1, diags[0].Line);
        Assert.Equal(4, diags[0].Column);
        Assert.Equal("CS1002", diags[0].Code);
        Assert.Equal("; expected", diags[0].Message);

        Assert.Equal("eval", diags[1].File);
        Assert.Equal(1, diags[1].Line);
        Assert.Equal(1, diags[1].Column);
        Assert.Equal("CS0103", diags[1].Code);
        Assert.Equal("The name 'xyz' does not exist in the current context", diags[1].Message);
    }

    [Fact]
    public void DiagnosticFormatter_ParseCompilerDiagnostics_PreservesPipeInsideSingleDiagnosticMessage()
    {
        string text = "Assets/Scripts/Flags.cs(12,9): error CS0019: Operator '|' cannot be applied to operands a | b";
        var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Single(diags);
        Assert.Equal("Assets/Scripts/Flags.cs", diags[0].File);
        Assert.Equal(12, diags[0].Line);
        Assert.Equal(9, diags[0].Column);
        Assert.Equal("CS0019", diags[0].Code);
        Assert.Equal("Operator '|' cannot be applied to operands a | b", diags[0].Message);
    }

    [Fact]
    public void DiagnosticFormatter_ParseCompilerDiagnostics_SplitsJoinedDiagnosticsWhilePreservingMessagePipes()
    {
        string text = "Assets/Scripts/Flags.cs(12,9): error CS0019: Bad expression a | b | Assets/Scripts/Other.cs(4,1): error CS1002: ; expected";
        var diags = DiagnosticFormatter.Default.ParseCompilerDiagnostics(text);

        Assert.Equal(2, diags.Count);
        Assert.Equal("Assets/Scripts/Flags.cs", diags[0].File);
        Assert.Equal("CS0019", diags[0].Code);
        Assert.Equal("Bad expression a | b", diags[0].Message);

        Assert.Equal("Assets/Scripts/Other.cs", diags[1].File);
        Assert.Equal("CS1002", diags[1].Code);
        Assert.Equal("; expected", diags[1].Message);
    }

    [Theory]
    [InlineData("eval", true)]
    [InlineData("EVAL", true)]
    [InlineData("<eval>", true)]
    [InlineData("<stdin>", true)]
    [InlineData("snippet", true)]
    [InlineData("SNIPPET", true)]
    [InlineData("Assets/Scripts/Player.cs", false)]
    [InlineData("eval.cs", false)]
    [InlineData("snippet.cs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DiagnosticFormatter_IsEvalSynthetic_IdentifiesSyntheticFileNames(string? file, bool expected)
    {
        Assert.Equal(expected, DiagnosticFormatter.IsEvalSynthetic(file));
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("EVAL")]
    [InlineData("<eval>")]
    [InlineData("snippet")]
    public void DiagnosticFormatter_BuildFileUri_WhenSyntheticFile_ReturnsEmpty(string file)
    {
        string uri = DiagnosticFormatter.BuildFileUri(file, 1, "C:/Repo");
        Assert.Equal(string.Empty, uri);
    }

    [Theory]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:\\Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "C:\\Repo\\", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("Assets/Scripts/Foo.cs", 10, "/home/user/repo/", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("./Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData(".\\Assets\\Scripts\\Foo.cs", 10, "C:\\Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:/Repo/Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:\\Repo\\Assets\\Scripts\\Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("C:/Repo/Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///C:/Repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("/home/user/repo/Assets/Scripts/Foo.cs", 10, "/home/user/repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    [InlineData("/home/user/repo/Assets/Scripts/Foo.cs", 10, "C:/Repo", "file:///home/user/repo/Assets/Scripts/Foo.cs#L10")]
    public void BuildFileUri_WindowsAndPosixPaths_FormatsCorrectFileUris(string file, int line, string projectRoot, string expectedUri)
    {
        string uri = DiagnosticFormatter.BuildFileUri(file, line, projectRoot);
        Assert.Equal(expectedUri, uri);
    }

    [Theory]
    [InlineData(
        "Assets/My Scripts/50% complete/#draft?.cs",
        7,
        "/Users/tester/My Project",
        "file:///Users/tester/My%20Project/Assets/My%20Scripts/50%25%20complete/%23draft%3F.cs#L7")]
    [InlineData(
        "C:\\Work Folder\\foo#bar%?.cs",
        42,
        "C:\\Project",
        "file:///C:/Work%20Folder/foo%23bar%25%3F.cs#L42")]
    [InlineData(
        "/home/tester/Проект/naïve file.cs",
        3,
        "/home/tester",
        "file:///home/tester/%D0%9F%D1%80%D0%BE%D0%B5%D0%BA%D1%82/na%C3%AFve%20file.cs#L3")]
    [InlineData(
        "\\\\build-server\\Shared Folder\\foo#bar.cs",
        5,
        "C:/Project",
        "file://build-server/Shared%20Folder/foo%23bar.cs#L5")]
    public void BuildFileUri_EncodesRawPathComponentsAcrossPlatformStyles(
        string file,
        int line,
        string projectRoot,
        string expectedUri)
    {
        Assert.Equal(expectedUri, DiagnosticFormatter.BuildFileUri(file, line, projectRoot));
    }

    [Fact]
    public void BuildFileUri_EncodesExtractedSpecialCharactersAndPreservesLineAnchor()
    {
        const string stackTrace = "at Suite.Test() in C:\\Project Folder\\Assets\\file#name%.cs:line 12";

        var (file, line, uri) = DiagnosticFormatter.Default.ExtractSourceLocation(stackTrace, "C:/Project");

        Assert.Equal("C:\\Project Folder\\Assets\\file#name%.cs", file);
        Assert.Equal(12, line);
        Assert.Equal("file:///C:/Project%20Folder/Assets/file%23name%25.cs#L12", uri);
    }

    [Theory]
    [InlineData(
        "file:///C:/Project%20Folder/Source%23.cs",
        19,
        "file:///C:/Project%20Folder/Source%23.cs#L19")]
    [InlineData(
        "file://build-server/Shared Folder/naïve%20file.cs#L8",
        19,
        "file://build-server/Shared%20Folder/na%C3%AFve%20file.cs#L8")]
    public void BuildFileUri_EncodesExistingFileUriWithoutDoubleEncodingOrReplacingAnchor(
        string fileUri,
        int line,
        string expectedUri)
    {
        Assert.Equal(expectedUri, DiagnosticFormatter.BuildFileUri(fileUri, line, "C:/Project"));
    }

    [Theory]
    [InlineData("Packages/com.unity.test-framework/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("Packages\\com.unity.test-framework\\Foo.cs", 10, "C:\\Repo", "file:///C:/Repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("Packages/com.unity.test-framework/Foo.cs", 10, "/home/user/repo", "file:///home/user/repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("/Packages/com.unity.test-framework/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("\\Packages\\com.unity.test-framework\\Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("./Packages/com.unity.test-framework/Foo.cs", 10, "C:/Repo", "file:///C:/Repo/Packages/com.unity.test-framework/Foo.cs#L10")]
    [InlineData("Packages/com.unity.test-framework/Foo.cs", 10, null, "file:///Packages/com.unity.test-framework/Foo.cs#L10")]
    public void BuildFileUri_PackageDependencies_ResolvesAgainstProjectRootWithLineAnchors(
        string file,
        int line,
        string? projectRoot,
        string expectedUri)
    {
        Assert.Equal(expectedUri, DiagnosticFormatter.BuildFileUri(file, line, projectRoot));
    }

    [Theory]
    [InlineData("/mnt/c/Repo/Assets/Foo.cs", 15, "C:/Repo", "file:///mnt/c/Repo/Assets/Foo.cs#L15")]
    [InlineData("/mnt/c/Repo/Assets/Foo.cs", 15, null, "file:///mnt/c/Repo/Assets/Foo.cs#L15")]
    [InlineData("/mnt/d/Project/Packages/com.foo/Bar.cs", 42, "C:/Repo", "file:///mnt/d/Project/Packages/com.foo/Bar.cs#L42")]
    public void BuildFileUri_WslMountPaths_GeneratesValidFileUrisAcrossHosts(
        string file,
        int line,
        string? projectRoot,
        string expectedUri)
    {
        Assert.Equal(expectedUri, DiagnosticFormatter.BuildFileUri(file, line, projectRoot));
    }

    [Theory]
    [InlineData("\\\\server\\share\\Assets\\Foo.cs", 20, "file://server/share/Assets/Foo.cs#L20")]
    [InlineData("//server/share/Assets/Foo.cs", 20, "file://server/share/Assets/Foo.cs#L20")]
    [InlineData("\\\\build-server\\Shared Folder\\foo#bar.cs", 5, "file://build-server/Shared%20Folder/foo%23bar.cs#L5")]
    [InlineData("//build-server/Shared Folder/naïve file.cs", 8, "file://build-server/Shared%20Folder/na%C3%AFve%20file.cs#L8")]
    public void BuildFileUri_UncPaths_PreservesAuthorityAndLineAnchors(
        string file,
        int line,
        string expectedUri)
    {
        Assert.Equal(expectedUri, DiagnosticFormatter.BuildFileUri(file, line, "C:/Project"));
    }

    [Fact]
    public void BuildFileUri_CrossDriveAndUnanchoredUnc_ProducesValidUris()
    {
        Assert.Equal("file://server/share/file.cs", DiagnosticFormatter.BuildFileUri(@"\\server\share\file.cs", null, "C:/Project"));
        Assert.Equal("file://server/share/file.cs", DiagnosticFormatter.BuildFileUri("//server/share/file.cs", null, "C:/Project"));
        Assert.Equal("file:///D:/OtherDrive/Assets/Script.cs#L99", DiagnosticFormatter.BuildFileUri("D:/OtherDrive/Assets/Script.cs", 99, "C:/Project"));
        Assert.Equal("file:///D:/OtherDrive/Assets/Script.cs#L99", DiagnosticFormatter.BuildFileUri(@"D:\OtherDrive\Assets\Script.cs", 99, "C:/Project"));
    }

    [Fact]
    public void FormatCompilerDiagnostics_WithPackageDependencyPath_ResolvesAgainstProjectRoot()
    {
        string diagText = "Packages/com.unity.test-framework/Runner.cs(42,10): error CS0103: The name 'Runner' does not exist in the current context";
        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(diagText, "C:/UnityProject", isSuccess: false);

        Assert.Contains("file:///C:/UnityProject/Packages/com.unity.test-framework/Runner.cs#L42", formatted);
        Assert.Contains("error CS0103", formatted);
    }

    [Fact]
    public async Task UnityTools_UsesInjectedDiagnosticFormatter()
    {
        using var ctx = new ToolFormattingTestContext();
        var customFormatter = new CustomMockDiagnosticFormatter();
        var tools = new UnityTools(ctx.Client, ctx.PathResolver, diagnosticFormatter: customFormatter);

        ctx.Client.RefreshResultToReturn = new UnityRefreshResult
        {
            Success = false,
            Message = "Assets/Scripts/Foo.cs(1,1): error CS0001: Error"
        };

        var result = await tools.UnityRefreshAsync();
        string text = ToolFormattingTestContext.GetResultText(result);
        Assert.Contains("CUSTOM_FORMATTED_DIAGNOSTIC", text);
    }

    [Fact]
    public void DiagnosticFormatter_WhenProjectErrorsExist_SuppressesPackageCacheWarnings()
    {
        string text = "Assets/Scripts/Player.cs(12,4): error CS0103: The name 'player' does not exist\n" +
                      "Library/PackageCache/com.unity.ugui@2.0.0/Runtime/UI.cs(50,1): warning CS0219: Variable 'foo' is unused\n" +
                      "Assets/Scripts/Game.cs(8,2): warning CS0168: Variable 'bar' is unused";

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            failureTrailer: "Compilation failed.",
            isSuccess: false);

        Assert.Contains("Assets/Scripts/Player.cs", formatted);
        Assert.Contains("Assets/Scripts/Game.cs", formatted);
        Assert.DoesNotContain("com.unity.ugui", formatted);
        Assert.DoesNotContain("Library/PackageCache", formatted);
    }

    [Fact]
    public void DiagnosticFormatter_WhenNoProjectErrorsExist_PreservesPackageCacheWarnings()
    {
        string text = "Library/PackageCache/com.unity.ugui@2.0.0/Runtime/UI.cs(50,1): warning CS0219: Variable 'foo' is unused\n" +
                      "Assets/Scripts/Game.cs(8,2): warning CS0168: Variable 'bar' is unused";

        string formatted = DiagnosticFormatter.Default.FormatCompilerDiagnostics(
            text,
            projectRoot: "C:/Code/MyProject",
            successTrailer: "Compilation succeeded.",
            isSuccess: true);

        Assert.Contains("com.unity.ugui", formatted);
        Assert.Contains("Assets/Scripts/Game.cs", formatted);
    }

    [Fact]
    public void DiagnosticFormatter_CompactTestStackTrace_SelectsTopActionableProjectLines()
    {
        var formatter = new DiagnosticFormatter();
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            "  at UnityEngine.Assertions.Assert.AreEqual[T] (T expected, T actual) [0x00000] in <...>:0",
            "  at MyProject.Engine.ComputeInternal () [0x00005] in Assets/Scripts/Engine.cs:10",
            "  at MyProject.Engine.Compute () [0x00010] in Assets/Scripts/Engine.cs:25",
            "  at MyProject.Tests.PlayerTests.MoveTest () [0x00025] in Assets/Tests/PlayerTests.cs:42",
            "  at MyProject.Tests.PlayerTests.BaseRunner () [0x00001] in Assets/Tests/BaseRunner.cs:99",
            "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(System.Reflection.RuntimeMethodInfo,object,object[],System.Exception&)",
            "  at NUnit.Framework.Internal.Commands.TestMethodCommand.Execute (NUnit.Framework.Internal.TestExecutionContext context) [0x0001c] in <...>:0"
        });

        string compacted = formatter.CompactTestStackTrace(rawTrace, maxLines: 3);

        Assert.Contains("ComputeInternal", compacted);
        Assert.Contains("Compute ()", compacted);
        Assert.Contains("MoveTest", compacted);
        Assert.DoesNotContain("BaseRunner", compacted);
        Assert.DoesNotContain("TestMethodCommand", compacted);
        Assert.DoesNotContain("InternalInvoke", compacted);
        Assert.DoesNotContain("Assert.AreEqual", compacted);
    }

    [Fact]
    public void DiagnosticFormatter_CompactTestStackTrace_PreservesProjectFramesPastMaxLines_InOriginalOrder()
    {
        var formatter = new DiagnosticFormatter();
        string nonProject0 = "  at UnityEngine.Assertions.Assert.AreEqual[T] (T expected, T actual) [0x00000] in <...>:0";
        string nonProject1 = "  at UnityEngine.Assertions.Assert.Fail (System.String message) [0x00000] in <...>:0";
        string nonProject2 = "  at UnityEngine.Debug.LogError (System.Object message) [0x00000] in <...>:0";
        string project3 = "  at MyProject.Engine.Compute () [0x00010] in Assets/Scripts/Engine.cs:25";
        string project4 = "  at MyProject.Tests.PlayerTests.MoveTest () [0x00025] in Assets/Tests/PlayerTests.cs:42";
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            nonProject0,
            nonProject1,
            nonProject2,
            project3,
            project4,
        });

        string compacted = formatter.CompactTestStackTrace(rawTrace, maxLines: 3);

        string expected = string.Join(Environment.NewLine, new[]
        {
            nonProject0,
            project3,
            project4,
        });
        Assert.Equal(expected, compacted);
    }

    [Fact]
    public void DiagnosticFormatter_CompactTestStackTrace_PreservesDuplicateRecursiveFrames()
    {
        var formatter = new DiagnosticFormatter();
        string nonProjectDup = "  at System.Collections.Generic.List`1[T].ForEach (System.Action`1[T] action) [0x00000] in <...>:0";
        string nonProjectOther = "  at UnityEngine.Assertions.Assert.IsTrue (System.Boolean condition) [0x00000] in <...>:0";
        string projectDup = "  at MyProject.TreeNode.Traverse (System.Int32 depth) [0x00015] in Assets/Scripts/TreeNode.cs:30";
        string rawTrace = string.Join(Environment.NewLine, new[]
        {
            nonProjectDup,
            nonProjectDup,
            nonProjectOther,
            projectDup,
            projectDup,
        });

        string compacted = formatter.CompactTestStackTrace(rawTrace, maxLines: 4);

        string expected = string.Join(Environment.NewLine, new[]
        {
            nonProjectDup,
            nonProjectDup,
            projectDup,
            projectDup,
        });
        Assert.Equal(expected, compacted);
    }

    [Fact]
    public void FormatEvalException_StandardExceptionWithReflectionAndHarnessTail_StripsTailAndFormatsEvalLine()
    {
        string message = string.Join("\n", new[]
        {
            "System.NullReferenceException: Object reference not set to an instance of an object",
            "  at GameService.Process () [0x00012] in Assets/Scripts/GameService.cs:42",
            "  at __UnityLeanMcpEvalRunner.Run () [0x00005] in eval:line 3",
            "  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke()",
            "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj) [0x0006a] in <000>:0",
            "  at UnityLeanMcp.EvalHandler.Execute (System.String code) [0x00050] in Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs:120"
        });

        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Contains("System.NullReferenceException: Object reference not set to an instance of an object", formatted);
        Assert.Contains("at GameService.Process () [0x00012] in Assets/Scripts/GameService.cs:42", formatted);
        Assert.Contains("at __UnityLeanMcpEvalRunner.Run () [0x00005] in snippet line 3", formatted);
        Assert.DoesNotContain("InternalInvoke", formatted);
        Assert.DoesNotContain("RuntimeMethodInfo.Invoke", formatted);
        Assert.DoesNotContain("UnityLeanMcp.EvalHandler", formatted);
    }

    [Fact]
    public void FormatEvalException_AsyncExceptionWithDispatchInfoAndTaskAwaiter_StripsOrphanedBoundaryAndTail()
    {
        string message = string.Join("\n", new[]
        {
            "System.InvalidOperationException: Async boom",
            "  at MyAsyncHelper.FailAsync () [0x00010] in Assets/Scripts/MyAsyncHelper.cs:18",
            "  at __UnityLeanMcpEvalRunner+<RunAsync>d__0.MoveNext () [0x00020] in eval:line 5",
            "--- End of stack trace from previous location ---",
            "  at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw () [0x0000c] in <000>:0",
            "  at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess (System.Threading.Tasks.Task task) [0x0003e] in <000>:0",
            "  at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification (System.Threading.Tasks.Task task) [0x00028] in <000>:0",
            "  at UnityLeanMcp.OperationExecutionEngine.PumpTasks () [0x00030] in Packages/com.pereviader.unityleanmcp/Editor/OperationExecutionEngine.cs:88"
        });

        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Contains("System.InvalidOperationException: Async boom", formatted);
        Assert.Contains("at MyAsyncHelper.FailAsync () [0x00010] in Assets/Scripts/MyAsyncHelper.cs:18", formatted);
        Assert.Contains("at __UnityLeanMcpEvalRunner+<RunAsync>d__0.MoveNext () [0x00020] in snippet line 5", formatted);
        Assert.DoesNotContain("End of stack trace from previous location", formatted);
        Assert.DoesNotContain("ExceptionDispatchInfo", formatted);
        Assert.DoesNotContain("TaskAwaiter", formatted);
        Assert.DoesNotContain("OperationExecutionEngine", formatted);
    }

    [Fact]
    public void FormatEvalException_MultilineMessage_PreservesAllMessageLines()
    {
        string message = string.Join("\n", new[]
        {
            "System.InvalidOperationException: First line of message",
            "Second line of message (UnityLeanMcp.EvalHandler mentioned in text)",
            "Third line of message",
            "  at Foo.Bar () in Assets/Scripts/Foo.cs:10",
            "  at __UnityLeanMcpEvalRunner.Run () in eval:line 1",
            "  at UnityLeanMcp.EvalHandler.Execute () in Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs:100"
        });

        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Contains("First line of message", formatted);
        Assert.Contains("Second line of message (UnityLeanMcp.EvalHandler mentioned in text)", formatted);
        Assert.Contains("Third line of message", formatted);
        Assert.Contains("at Foo.Bar () in Assets/Scripts/Foo.cs:10", formatted);
        Assert.Contains("at __UnityLeanMcpEvalRunner.Run () in snippet line 1", formatted);
        Assert.DoesNotContain("EvalHandler.Execute", formatted);
    }

    [Fact]
    public void FormatEvalException_InnerExceptionChain_PreservesHeadersAndStripsHarnessTail()
    {
        string message = string.Join("\n", new[]
        {
            "System.AggregateException: One or more errors occurred. (Inner boom)",
            " ---> System.InvalidOperationException: Inner boom",
            "  at InnerService.DoWork () [0x00010] in Assets/Scripts/InnerService.cs:12",
            "  at OuterService.Run () [0x00020] in Assets/Scripts/OuterService.cs:25",
            "   --- End of inner exception stack trace ---",
            "  at OuterService.Call () [0x00030] in Assets/Scripts/OuterService.cs:40",
            "  at __UnityLeanMcpEvalRunner.Run () [0x00005] in eval:line 2",
            "  at System.Reflection.RuntimeMethodInfo.Invoke (System.Object obj) [0x0006a] in <000>:0",
            "  at UnityLeanMcp.EvalHandler.Execute (System.String code) [0x00050] in Packages/com.pereviader.unityleanmcp/Editor/EvalHandler.cs:120"
        });

        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Contains("System.AggregateException: One or more errors occurred. (Inner boom)", formatted);
        Assert.Contains("---> System.InvalidOperationException: Inner boom", formatted);
        Assert.Contains("at InnerService.DoWork ()", formatted);
        Assert.Contains("--- End of inner exception stack trace ---", formatted);
        Assert.Contains("at OuterService.Call ()", formatted);
        Assert.Contains("at __UnityLeanMcpEvalRunner.Run () [0x00005] in snippet line 2", formatted);
        Assert.DoesNotContain("RuntimeMethodInfo.Invoke", formatted);
        Assert.DoesNotContain("UnityLeanMcp.EvalHandler", formatted);
    }

    [Theory]
    [InlineData("Unity version 2020.3 is not supported. Minimum supported version is 2021.3.")]
    [InlineData("Unity is busy with another operation: BUSY test")]
    [InlineData("Evaluation timed out or failed without stack frames.")]
    public void FormatEvalException_NonExceptionMessageWithoutStackFrames_ReturnsUnchanged(string message)
    {
        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Equal(message, formatted);
    }

    [Theory]
    [InlineData(@"  at GameService.Process () in C:\Projects\Game\Assets\Scripts\GameService.cs:line 42")]
    [InlineData(@"  at GameService.Process () in /Users/dev/Game/Assets/Scripts/GameService.cs:42")]
    public void FormatEvalException_CrossPlatformPaths_PreservesProjectFrames(string frame)
    {
        string message = $"System.Exception: Fail\n{frame}\n  at UnityLeanMcp.EvalHandler.Execute ()";

        string formatted = DiagnosticFormatter.Default.FormatEvalException(message);

        Assert.Contains(frame, formatted);
        Assert.DoesNotContain("UnityLeanMcp.EvalHandler", formatted);
    }

    private sealed class TestCustomDiagnosticFormatter : IDiagnosticFormatter
    {
        public bool ExtractSourceLocationCalled { get; private set; }
        public bool ParseCompilerDiagnosticsCalled { get; private set; }
        public bool FormatCompilerDiagnosticsCalled { get; private set; }
        public bool FormatDiagnosticCalled { get; private set; }

        public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
        {
            ExtractSourceLocationCalled = true;
            return ("CustomFile.cs", 1, "file:///CustomFile.cs#L1");
        }

        public string SanitizeTestStackTrace(string? stackTrace) => stackTrace ?? string.Empty;
        public string CompactTestStackTrace(string? stackTrace, int maxLines = 3) => stackTrace ?? string.Empty;

        public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText)
        {
            ParseCompilerDiagnosticsCalled = true;
            return new List<StructuredCompilerDiagnostic>
            {
                new StructuredCompilerDiagnostic
                {
                    File = "CustomFile.cs",
                    Line = 1,
                    Column = 1,
                    Severity = "warning",
                    Code = "CS9999",
                    Message = "Custom diagnostic"
                }
            };
        }

        public string FormatCompilerDiagnostics(
            string? diagnosticText,
            string? projectRoot,
            string? successTrailer = null,
            string? failureTrailer = null,
            bool isSuccess = false,
            int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
            bool isEval = false)
        {
            FormatCompilerDiagnosticsCalled = true;
            return DiagnosticFormatter.Default.FormatCompilerDiagnostics(
                diagnosticText, projectRoot, successTrailer, failureTrailer, isSuccess, maxWarnings, isEval);
        }

        public string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false)
        {
            FormatDiagnosticCalled = true;
            return DiagnosticFormatter.Default.FormatDiagnostic(diagnostic, projectRoot, isEval);
        }
    }

    private sealed class CustomMockDiagnosticFormatter : IDiagnosticFormatter
    {
        public (string? filePath, int? lineNumber, string? fileUri) ExtractSourceLocation(string? stackTrace, string? projectRoot)
            => (null, null, null);
        public string SanitizeTestStackTrace(string? stackTrace) => stackTrace ?? string.Empty;
        public string CompactTestStackTrace(string? stackTrace, int maxLines = 3) => stackTrace ?? string.Empty;
        public List<StructuredCompilerDiagnostic> ParseCompilerDiagnostics(string? diagnosticText) => [];
        public string FormatCompilerDiagnostics(
            string? diagnosticText,
            string? projectRoot,
            string? successTrailer = null,
            string? failureTrailer = null,
            bool isSuccess = false,
            int maxWarnings = DiagnosticFormatter.DefaultMaxWarnings,
            bool isEval = false) => "CUSTOM_FORMATTED_DIAGNOSTIC";
        public string FormatDiagnostic(StructuredCompilerDiagnostic diagnostic, string? projectRoot, bool isEval = false)
            => "CUSTOM_DIAGNOSTIC";
    }
}
