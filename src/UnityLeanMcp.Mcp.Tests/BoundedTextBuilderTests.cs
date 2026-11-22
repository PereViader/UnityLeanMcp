using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class BoundedTextBuilderTests
{
    [Fact]
    public async Task UnityEval_PublicOutput_EnforcesUtf8BudgetWithoutSplittingEmoji()
    {
        using var ctx = new ToolFormattingTestContext();
        ctx.Client.EvalResultToReturn = new UnityEvalResult
        {
            Success = true,
            Payload = string.Concat(Enumerable.Repeat("界😀", McpOutputLimits.MaxFormattedOutputCharacters / 2))
        };

        var result = await ctx.Tools.UnityEvalAsync("return value;");
        string text = ToolFormattingTestContext.GetResultText(result);

        Assert.False(result.IsError);
        Assert.True(text.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= McpOutputLimits.MaxFormattedOutputBytes);
        Assert.Contains(McpOutputLimits.AggregateOutputTruncationMarker, text);
        AssertValidUtf16(text);
    }

    [Fact]
    public void BoundedTextBuilder_ByteBudgetPreservesSurrogatePairs()
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 16,
            aggregateMarker: "[truncated]");

        builder.Append("😀😀😀😀😀");
        string text = builder.ToString();

        Assert.True(text.Length <= 64);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 16);
        Assert.Equal("😀[truncated]", text);
        AssertValidUtf16(text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedTextBuilder_BoundedEntryPointsDoNotSplitSurrogatePairs(bool trimTrailingWhitespace)
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 1024,
            aggregateMarker: "[aggregate]");
        const string marker = "[x]";
        string value = trimTrailingWhitespace ? "😀😀😀😀  \t" : "😀😀😀😀";

        if (trimTrailingWhitespace)
        {
            builder.AppendTrimmedBounded(value, maxCharacters: 6, marker: marker);
        }
        else
        {
            builder.AppendBounded(value, maxCharacters: 6, marker: marker);
        }

        string text = builder.ToString();

        Assert.Equal("😀[x]", text);
        AssertValidUtf16(text);
    }

    [Fact]
    public void McpOutputLimits_TruncateDoesNotSplitSurrogatePairs()
    {
        string text = McpOutputLimits.Truncate("😀😀😀😀", maxCharacters: 6, marker: "[x]");

        Assert.Equal("😀[x]", text);
        AssertValidUtf16(text);
    }

    [Theory]
    [InlineData(5, "😀[x]")]
    [InlineData(6, "😀[x]")]
    [InlineData(7, "😀😀[x]")]
    [InlineData(4, "[x]")]
    [InlineData(3, "[x]")]
    public void McpOutputLimits_Truncate_PreservesSurrogatePairIntegrityAtAllCutoffs(int maxCharacters, string expected)
    {
        string input = "😀😀😀😀";
        string result = McpOutputLimits.Truncate(input, maxCharacters, "[x]");
        Assert.Equal(expected, result);
        AssertValidUtf16(result);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void BoundedTextBuilder_UnpairedSurrogates_DoNotTriggerFalseTruncation(int surrogateCodeUnit)
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 128,
            aggregateMarker: "[truncated]");

        string surrogate = ((char)surrogateCodeUnit).ToString();
        string surrogateText = $"a{surrogate}b{surrogate}c";
        builder.Append(surrogateText);
        builder.Append("-tail");

        string expected = surrogateText + "-tail";
        Assert.Equal(expected, builder.ToString());
        Assert.Equal(3 + 6 + 5, builder.Utf8ByteLength);
    }

    [Fact]
    public void BoundedTextBuilder_UnpairedSurrogate_RespectsByteBudgetWhenFitting()
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 16,
            aggregateMarker: "[truncated]");

        // "[truncated]" is 11 bytes, leaving 5 bytes: "a" (1) + "\uDC00" (3) = 4 bytes fits,
        // next "\uDC00" (3) would exceed 5 bytes once the 17-byte input triggers truncation.
        builder.Append("a\uDC00\uDC00\uDC00\uDC00\uDC00b");

        Assert.Equal("a\uDC00[truncated]", builder.ToString());
        Assert.Equal(4 + 11, builder.Utf8ByteLength);
    }

    [Theory]
    [InlineData(false, 2, "[x", "[truncated]")]
    [InlineData(true, 2, "[x", "[truncated]")]
    [InlineData(false, 3, "[x]", "[truncated]")]
    [InlineData(true, 3, "[x]", "[truncated]")]
    [InlineData(false, 0, "", "[truncated]")]
    [InlineData(true, 0, "", "[truncated]")]
    public void BoundedTextBuilder_BoundedEntryPoints_TruncateMarkerWhenMaxCharactersIsNotGreaterThanMarker(
        bool trimTrailingWhitespace,
        int maxCharacters,
        string expected,
        string aggregateMarker)
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 1024,
            aggregateMarker: aggregateMarker);
        const string marker = "[x]";
        string value = trimTrailingWhitespace ? "abcdef  \t" : "abcdef";

        if (trimTrailingWhitespace)
        {
            builder.AppendTrimmedBounded(value, maxCharacters, marker);
        }
        else
        {
            builder.AppendBounded(value, maxCharacters, marker);
        }

        string text = builder.ToString();
        Assert.True(text.Length <= maxCharacters);
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedTextBuilder_BoundedEntryPoints_ThrowOnNegativeMaxCharacters(bool trimTrailingWhitespace)
    {
        var builder = new BoundedTextBuilder(
            maxCharacters: 64,
            maxBytes: 1024,
            aggregateMarker: "[truncated]");

        if (trimTrailingWhitespace)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                builder.AppendTrimmedBounded("abc", maxCharacters: -1, marker: "[x]"));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                builder.AppendBounded("abc", maxCharacters: -1, marker: "[x]"));
        }
    }

    private static void AssertValidUtf16(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]));
            }
        }
    }
}
