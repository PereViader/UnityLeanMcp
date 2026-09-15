using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public sealed class ToolSchemaTests
{
    [Fact]
    public void UnityRunTests_ExposesTypedFilterArraysAndModeEnum()
    {
        var tools = new UnityTools(null!, null!, new UnityPathResolver("/tmp"));
        MethodInfo method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityRunTestsAsync))!;
        var tool = McpServerTool.Create(method, tools, new McpServerToolCreateOptions());
        using JsonDocument schema = JsonDocument.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        JsonElement properties = schema.RootElement.GetProperty("properties");

        foreach (string parameterName in new[] { "testNames", "groupNames", "categoryNames", "assemblyNames" })
        {
            JsonElement parameter = properties.GetProperty(parameterName);
            Assert.Contains("array", GetSchemaTypes(parameter));
            Assert.Contains("string", GetSchemaTypes(parameter.GetProperty("items")));
        }

        JsonElement mode = properties.GetProperty("mode");
        Assert.Equal("string", mode.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "all", "editmode", "playmode" },
            mode.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("all", mode.GetProperty("default").GetString());
    }

    private static IEnumerable<string> GetSchemaTypes(JsonElement schema)
    {
        JsonElement type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()!)
            : new[] { type.GetString()! };
    }
}
