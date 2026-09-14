using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class McpTestClientDiagnosticsTests
{
    [Fact]
    public void StderrTail_RetainsRecentOutputWithinBound()
    {
        var tail = new BoundedStderrTail(8);

        tail.Append("1234567890");

        string output = tail.GetText();
        Assert.EndsWith("34567890", output, StringComparison.Ordinal);
        Assert.Contains("stderr truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void StderrTail_IsThreadSafeAndBoundedUnderConcurrentWrites()
    {
        const int maxCharacters = 1024;
        var tail = new BoundedStderrTail(maxCharacters);

        Parallel.For(0, 64, _ => tail.Append(new string('x', 512)));

        string output = tail.GetText();
        const string marker = "... (stderr truncated; showing the most recent output)\n";
        Assert.StartsWith(marker, output, StringComparison.Ordinal);
        Assert.Equal(maxCharacters, output.Length - marker.Length);
        Assert.EndsWith(new string('x', maxCharacters), output, StringComparison.Ordinal);
        Assert.Equal(maxCharacters, output[marker.Length..].Count(char.IsLetterOrDigit));
    }
}
