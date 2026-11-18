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
