using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class ProtocolCodecTests
{
    [Fact]
    public void EscapeToken_NullAndEmpty_ReturnsExpected()
    {
        Assert.Null(ProtocolCodec.EscapeToken(null));
        Assert.Equal(string.Empty, ProtocolCodec.EscapeToken(string.Empty));

        Assert.Null(ProtocolCodec.UnescapeToken(null));
        Assert.Equal(string.Empty, ProtocolCodec.UnescapeToken(string.Empty));
    }

    [Fact]
    public void EscapeLine_NullAndEmpty_ReturnsExpected()
    {
        Assert.Null(ProtocolCodec.EscapeLine(null));
        Assert.Equal(string.Empty, ProtocolCodec.EscapeLine(string.Empty));

        Assert.Null(ProtocolCodec.UnescapeLine(null));
        Assert.Equal(string.Empty, ProtocolCodec.UnescapeLine(string.Empty));
    }

    [Fact]
    public void EscapeParam_IsIdenticalTo_EscapeToken()
    {
        const string input = "path\\with\"quotes\"\r\nand\ttabs";
        Assert.Equal(ProtocolCodec.EscapeToken(input), ProtocolCodec.EscapeParam(input));
    }

    [Fact]
    public void EscapeCode_IsIdenticalTo_EscapeLine()
    {
        const string input = "Debug.Log(\"test\");\r\nreturn 42;";
        Assert.Equal(ProtocolCodec.EscapeLine(input), ProtocolCodec.EscapeCode(input));
    }

    [Theory]
    [InlineData("Hello World")]
    [InlineData("Single'Quotes'AreNotEscaped")]
    [InlineData("Double \"Quotes\" Are Escaped")]
    [InlineData("\"Leading and trailing quotes\"")]
    [InlineData("\"\"")]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Carriage\rReturn")]
    [InlineData("Tab\tSeparated\tValues")]
    [InlineData(@"C:\Program Files\Unity\Editor\Data")]
    [InlineData(@"C:\Folder\With\Trailing\Backslash\")]
    [InlineData(@"\\NetworkShare\Folder\Subfolder")]
    [InlineData(@"Literal \n is not a newline")]
    [InlineData(@"Literal \r and \t are not carriage returns or tabs")]
    [InlineData("Mixed: literal \\n and actual \n newline together")]
    [InlineData("Multiple \\\\ backslashes and \"\" quotes \"\"")]
    [InlineData("C# Code: var s = \"Hello \\\"World\\\"\";\nConsole.WriteLine(s);")]
    public void TokenCodec_Roundtrip_RestoresOriginalString(string input)
    {
        string escaped = ProtocolCodec.EscapeToken(input);
        string unescaped = ProtocolCodec.UnescapeToken(escaped);

        Assert.Equal(input, unescaped);
    }

    [Theory]
    [InlineData("Hello World")]
    [InlineData("Quotes \"remain\" unescaped in whole-line payloads")]
    [InlineData("\"\"")]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Carriage\rReturn")]
    [InlineData("Tab\tSeparated\tValues")]
    [InlineData(@"C:\Program Files\Unity\Editor\Data")]
    [InlineData(@"C:\Folder\With\Trailing\Backslash\")]
    [InlineData(@"\\NetworkShare\Folder\Subfolder")]
    [InlineData(@"Literal \n is not a newline")]
    [InlineData(@"Literal \r and \t are not control chars")]
    [InlineData("Mixed: literal \\n and actual \n newline together")]
    [InlineData(@"Regex: \d+\s*\w+")]
    [InlineData("SUCCESS {\"status\":\"ok\",\"path\":\"C:\\\\Unity\"}")]
    public void LineCodec_Roundtrip_RestoresOriginalString(string input)
    {
        string escaped = ProtocolCodec.EscapeLine(input);
        string unescaped = ProtocolCodec.UnescapeLine(escaped);

        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void LineCodec_DoesNotEscapeDoubleQuotes()
    {
        const string input = "SUCCESS {\"key\": \"value\"}";
        string escaped = ProtocolCodec.EscapeLine(input);

        // Quotes must remain as literal double-quotes in line-based responses
        Assert.Contains("\"key\"", escaped);
        Assert.DoesNotContain("\\\"", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void TokenCodec_EscapesDoubleQuotes()
    {
        const string input = "param \"with\" quotes";
        string escaped = ProtocolCodec.EscapeToken(input);

        Assert.Contains("\\\"with\\\"", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(input, unescaped);
    }

    [Fact]
    public void LineCodec_EscapesBackslashes_PreventingPathCorruption()
    {
        // When a path like C:\new\test or C:\read\table is sent,
        // \n and \r must not be mistakenly treated as control characters.
        const string windowsPath = @"C:\new\read\test\table";
        string escaped = ProtocolCodec.EscapeLine(windowsPath);

        // All backslashes must be escaped
        Assert.Equal(@"C:\\new\\read\\test\\table", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(windowsPath, unescaped);
    }

    [Fact]
    public void TokenCodec_EscapesBackslashes_PreventingPathCorruption()
    {
        const string windowsPath = @"C:\new\read\test\table";
        string escaped = ProtocolCodec.EscapeToken(windowsPath);

        Assert.Equal(@"C:\\new\\read\\test\\table", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(windowsPath, unescaped);
    }

    [Fact]
    public void TokenCodec_HandlesLoneAndTrailingBackslashes()
    {
        const string trailingSlash = @"test\";
        string escaped = ProtocolCodec.EscapeToken(trailingSlash);
        Assert.Equal(@"test\\", escaped);

        string unescaped = ProtocolCodec.UnescapeToken(escaped);
        Assert.Equal(trailingSlash, unescaped);

        // Unescaped trailing lone backslash without escaping
        Assert.Equal(@"test\", ProtocolCodec.UnescapeToken(@"test\"));
    }

    [Fact]
    public void LineCodec_HandlesLoneAndTrailingBackslashes()
    {
        const string trailingSlash = @"test\";
        string escaped = ProtocolCodec.EscapeLine(trailingSlash);
        Assert.Equal(@"test\\", escaped);

        string unescaped = ProtocolCodec.UnescapeLine(escaped);
        Assert.Equal(trailingSlash, unescaped);

        Assert.Equal(@"test\", ProtocolCodec.UnescapeLine(@"test\"));
    }

}
