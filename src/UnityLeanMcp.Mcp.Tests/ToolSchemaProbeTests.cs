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
    public void UnityTest_ExposesTypedFilterArraysModeEnumAndCoverage()
    {
        var tools = new UnityTools(null!, null!, new UnityPathResolver("/tmp"));
        MethodInfo method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityTestAsync))!;
        var tool = McpServerTool.Create(method, tools, new McpServerToolCreateOptions());
        Assert.Equal("unity_test", tool.ProtocolTool.Name);
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
            new[] { "editmode", "playmode" },
            mode.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.False(mode.TryGetProperty("default", out _));

        JsonElement coverage = properties.GetProperty("coverage");
        Assert.Contains("boolean", GetSchemaTypes(coverage));

        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("mode", required);
    }

    [Fact]
    public void UnityCoverage_ExposesRequiredPathsArray()
    {
        var tools = new UnityTools(null!, null!, new UnityPathResolver("/tmp"));
        MethodInfo method = typeof(UnityTools).GetMethod(nameof(UnityTools.UnityCoverageAsync))!;
        var tool = McpServerTool.Create(method, tools, new McpServerToolCreateOptions());
        Assert.Equal("unity_coverage", tool.ProtocolTool.Name);
        using JsonDocument schema = JsonDocument.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        JsonElement properties = schema.RootElement.GetProperty("properties");

        JsonElement paths = properties.GetProperty("paths");
        Assert.Contains("array", GetSchemaTypes(paths));
        Assert.Contains("string", GetSchemaTypes(paths.GetProperty("items")));

        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("paths", required);
    }

    private static IEnumerable<string> GetSchemaTypes(JsonElement schema)
    {
        JsonElement type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()!)
            : new[] { type.GetString()! };
    }
}
