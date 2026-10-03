using System;
using System.Collections.Generic;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class UnityLeanJsonTests
{
    [Fact]
    public void Serialize_PrimitivesAndStructures_MatchesExpectedJson()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "test",
            ["count"] = 42,
            ["pi"] = 3.14,
            ["active"] = true,
            ["items"] = new List<object> { "a", 1, false },
            ["nested"] = new Dictionary<string, object?> { ["inner"] = null }
        };

        string compact = UnityLeanJson.Serialize(data, prettyPrint: false);
        Assert.Equal("{\"name\":\"test\",\"count\":42,\"pi\":3.14,\"active\":true,\"items\":[\"a\",1,false],\"nested\":{\"inner\":null}}", compact);

        string pretty = UnityLeanJson.Serialize(data, prettyPrint: true);
        Assert.Contains("  \"name\": \"test\",\n", pretty);
        Assert.Contains("  \"count\": 42,\n", pretty);
        Assert.Contains("  \"items\": [\n    \"a\",\n    1,\n    false\n  ],\n", pretty);
        Assert.EndsWith("}\n", pretty);
    }

    [Fact]
    public void Serialize_EscapesControlCharactersAndQuotes()
    {
        var data = new Dictionary<string, object>
        {
            ["text"] = "line 1\nline 2\r\ttab \"quote\" \\slash \u001fcontrol"
        };

        string json = UnityLeanJson.Serialize(data, prettyPrint: false);
        Assert.Equal("{\"text\":\"line 1\\nline 2\\r\\ttab \\\"quote\\\" \\\\slash \\u001fcontrol\"}", json);

        var roundtripped = UnityLeanJson.DeserializeObject(json);
        Assert.Equal("line 1\nline 2\r\ttab \"quote\" \\slash \u001fcontrol", roundtripped["text"]);
    }

    [Fact]
    public void Deserialize_HandlesCommentsGracefully()
    {
        string jsonWithComments = @"
        // Top-level comment
        {
            /* block comment before key */
            ""key"": ""value"", // line comment after value
            ""list"": [
                /* array item */ 1,
                2 // item 2
            ]
        }
        // Trailing comment
        /* Trailing block */
        ";

        var result = UnityLeanJson.DeserializeObject(jsonWithComments);
        Assert.Equal("value", result["key"]);
        var list = Assert.IsType<List<object>>(result["list"]);
        Assert.Equal(2, list.Count);
        Assert.Equal(1L, list[0]);
        Assert.Equal(2L, list[1]);
    }

    [Fact]
    public void Deserialize_PreservesIsoDateStringsExactly()
    {
        string isoDate = "2026-10-03T11:42:00.000Z";
        string json = $"{{\"timestamp\":\"{isoDate}\"}}";

        var result = UnityLeanJson.DeserializeObject(json);
        object val = result["timestamp"];
        Assert.IsType<string>(val);
        Assert.Equal(isoDate, val);
    }

    [Theory]
    [InlineData("{\"key\":")]
    [InlineData("{\"key\": \"val\"")]
    [InlineData("{\"key\": \"val\"} trailing")]
    [InlineData("{\"key\": \"val\"} { \"another\": 1 }")]
    [InlineData("/* unterminated comment { \"key\": 1 }")]
    [InlineData("{\"key\": \"unterminated string }")]
    [InlineData("[\"not an object root\"]")]
    [InlineData("")]
    [InlineData("   ")]
    public void DeserializeObject_InvalidJson_ThrowsFormatException(string invalidJson)
    {
        Assert.Throws<FormatException>(() => UnityLeanJson.DeserializeObject(invalidJson));
    }

    [Fact]
    public void Deserialize_UnicodeEscapesAndSurrogatePairs()
    {
        // Unicode snowman \u2603 and grinning face \uD83D\uDE00
        string json = "{\"symbols\":\"\\u2603 \\uD83D\\uDE00\"}";
        var result = UnityLeanJson.DeserializeObject(json);
        Assert.Equal("\u2603 \uD83D\uDE00", result["symbols"]);
    }

    [Fact]
    public void Deserialize_Numbers_DistinguishesLongAndDouble()
    {
        string json = "{\"intVal\": 1234567890123, \"floatVal\": 123.456, \"expVal\": 1e-4}";
        var result = UnityLeanJson.DeserializeObject(json);
        Assert.Equal(1234567890123L, result["intVal"]);
        Assert.Equal(123.456, (double)result["floatVal"], 4);
        Assert.Equal(0.0001, (double)result["expVal"], 6);
    }
}
