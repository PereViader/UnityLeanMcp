using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityLeanMcp;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
public sealed class UnityResultFormatterUnitTests : IDisposable
{
    private readonly UnityInspectTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void LimitOutput_TruncatesWhenExceedingCharacterCap()
    {
        string hugeText = new string('A', 1000);
        string capped = Inspect.LimitOutput(hugeText, maxCharacters: 100);

        Assert.True(capped.Length <= 100);
        Assert.Contains("... (truncated: maximum output size reached)", capped);
    }

    [Fact]
    public void LimitOutput_PreservesTextWithinBudget()
    {
        string normalText = "Hello World";
        string result = Inspect.LimitOutput(normalText, maxCharacters: 100);

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void LimitOutput_TinyBudget_StrictlyEnforcesCapsWithoutOverflow()
    {
        string text = new string('X', 500);

        // Cap at 10 chars
        string capped10 = Inspect.LimitOutput(text, maxCharacters: 10);
        Assert.True(capped10.Length <= 10, $"Expected length <= 10 but was {capped10.Length}");

        // Cap at 5 chars
        string capped5 = Inspect.LimitOutput(text, maxCharacters: 5);
        Assert.True(capped5.Length <= 5, $"Expected length <= 5 but was {capped5.Length}");
    }

    [Fact]
    public void LimitOutput_65KSurrogatePairs_TruncatesFastAndPreservesValidUtf16()
    {
        // 32,768 emoji surrogate pairs + 1 char (65,537 UTF-16 chars)
        var emojiBuilder = new StringBuilder(65537);
        for (int i = 0; i < 32768; i++)
        {
            emojiBuilder.Append("\uD83D\uDE00");
        }
        emojiBuilder.Append('x');
        string emojiText = emojiBuilder.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string limitedInspect = Inspect.LimitOutput(emojiText, maxCharacters: 65536);
        string limitedFormatter = UnityResultFormatter.LimitOutput(emojiText, 65536);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected fast truncation, took {sw.ElapsedMilliseconds}ms");
        Assert.True(limitedInspect.Length <= 65536);
        int markerIndex = limitedInspect.IndexOf("... (truncated: maximum output size reached)", StringComparison.Ordinal);
        Assert.True(markerIndex > 0);
        Assert.False(char.IsHighSurrogate(limitedInspect[markerIndex - 1]));
        Assert.Equal(limitedInspect, limitedFormatter);
    }

    private class BaseSerializableNode
    {
        [SerializeField] private BaseSerializableNode? _serializedPrivateCycle;
        internal BaseSerializableNode? InternalNonSerializedCycle;
        protected BaseSerializableNode? ProtectedNonSerializedCycle;

        public void SetPrivateCycle(BaseSerializableNode target) => _serializedPrivateCycle = target;
        public void SetProtectedCycle(BaseSerializableNode target) => ProtectedNonSerializedCycle = target;
    }

    private sealed class DerivedSerializableNode : BaseSerializableNode
    {
        public string Label = "Node";
    }

    [Fact]
    public void FormattingContext_InspectChildren_IgnoresInternalAndProtectedFieldsAndVisitsInheritedPrivateSerializeField()
    {
        var nodeWithNonPublicCycles = new DerivedSerializableNode();
        nodeWithNonPublicCycles.InternalNonSerializedCycle = nodeWithNonPublicCycles;
        nodeWithNonPublicCycles.SetProtectedCycle(nodeWithNonPublicCycles);

        var ctx1 = new UnityResultFormatter.FormattingContext(
            UnityResultFormatter.DefaultMaxDepth,
            UnityResultFormatter.DefaultMaxItems,
            UnityResultFormatter.DefaultMaxOutputCharacters);
        Assert.Equal(
            UnityResultFormatter.GraphGuardResult.Safe,
            ctx1.InspectChildren(nodeWithNonPublicCycles, depth: 0, prettyPrint: false));

        // Setting the inherited [SerializeField] private field to a self-cycle must be detected
        var nodeWithInheritedSerializedCycle = new DerivedSerializableNode();
        nodeWithInheritedSerializedCycle.SetPrivateCycle(nodeWithInheritedSerializedCycle);

        var ctx2 = new UnityResultFormatter.FormattingContext(
            UnityResultFormatter.DefaultMaxDepth,
            UnityResultFormatter.DefaultMaxItems,
            UnityResultFormatter.DefaultMaxOutputCharacters);
        Assert.Equal(
            UnityResultFormatter.GraphGuardResult.Cycle,
            ctx2.InspectChildren(nodeWithInheritedSerializedCycle, depth: 0, prettyPrint: false));
    }

    private sealed class FallbackArrayWrapper
    {
        public int[] Items = new[] { 1, 2, 3 };
    }

    [Fact]
    public void FormattingContext_SharesUnifiedItemBudgetAcrossCustomFormattersAndFallbackInspection()
    {
        int originalMaxItems = UnityResultFormatter.MaxItems;
        try
        {
            UnityResultFormatter.MaxItems = 3;
            // Outer list takes 1 item for the wrapper; wrapper's Items array has 3 elements (total 4 > MaxItems=3)
            var composite = new List<object> { new FallbackArrayWrapper() };
            string formatted = UnityResultFormatter.FormatResult(composite, prettyPrint: false);
            Assert.Contains("... (truncated: maximum item count reached)", formatted);
        }
        finally
        {
            UnityResultFormatter.MaxItems = originalMaxItems;
        }
    }
}
