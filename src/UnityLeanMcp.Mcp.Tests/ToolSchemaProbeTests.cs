using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
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

    [Fact]
    public void UnityTestMode_DeserializationBehavior()
    {
        var edit1 = JsonSerializer.Deserialize<UnityTestMode>("\"editmode\"");
        Assert.Equal(UnityTestMode.EditMode, edit1);
        var edit2 = JsonSerializer.Deserialize<UnityTestMode>("\"EditMode\"");
        Assert.Equal(UnityTestMode.EditMode, edit2);
        var play1 = JsonSerializer.Deserialize<UnityTestMode>("\"playmode\"");
        Assert.Equal(UnityTestMode.PlayMode, play1);
        var play2 = JsonSerializer.Deserialize<UnityTestMode>("\"PlayMode\"");
        Assert.Equal(UnityTestMode.PlayMode, play2);
    }

    [Fact]
    public void NormalizeToolArguments_NormalizesJsonElementStringToArray()
    {
        using var doc = JsonDocument.Parse("{\"testNames\":\"MySuite.MyTest\",\"paths\":\"Assets/Foo.cs\"}");
        var args = new Dictionary<string, JsonElement>
        {
            ["testNames"] = doc.RootElement.GetProperty("testNames"),
            ["paths"] = doc.RootElement.GetProperty("paths")
        };
        var requestParams = new CallToolRequestParams
        {
            Name = "unity_test",
            Arguments = args
        };

        UnityTools.NormalizeToolArguments(requestParams);

        Assert.Equal(JsonValueKind.Array, args["testNames"].ValueKind);
        Assert.Equal("MySuite.MyTest", args["testNames"][0].GetString());
        Assert.Equal(JsonValueKind.Array, args["paths"].ValueKind);
        Assert.Equal("Assets/Foo.cs", args["paths"][0].GetString());
    }

    [Fact]
    public void NormalizeToolArguments_LeavesExistingArraysUnchanged()
    {
        using var doc = JsonDocument.Parse("{\"testNames\":[\"A\",\"B\"],\"paths\":[\"X.cs\",\"Y.cs\"]}");
        var args = new Dictionary<string, JsonElement>
        {
            ["testNames"] = doc.RootElement.GetProperty("testNames"),
            ["paths"] = doc.RootElement.GetProperty("paths")
        };
        var requestParams = new CallToolRequestParams
        {
            Name = "unity_test",
            Arguments = args
        };

        UnityTools.NormalizeToolArguments(requestParams);

        Assert.Equal(JsonValueKind.Array, args["testNames"].ValueKind);
        Assert.Equal(2, args["testNames"].GetArrayLength());
        Assert.Equal(JsonValueKind.Array, args["paths"].ValueKind);
        Assert.Equal(2, args["paths"].GetArrayLength());
    }
}





















