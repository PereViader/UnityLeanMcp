using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Subsystem")]
public class McpProtocolTests
{
    [Fact]
    public async Task StdioHandshake_And_ToolsList_ReturnsAllExpectedTools()
    {
        string dllPath = McpTestClient.GetSubsystemServerDllPath();
        string projectRoot = McpTestClient.GetUnityProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{projectRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        // Constantly drain stderr to avoid process deadlock on full pipe buffer
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();

        var writer = proc.StandardInput;
        var reader = proc.StandardOutput;

        // 1. Initialize
        string initMsg = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"xunit-test\",\"version\":\"1.0\"}}}";
        await writer.WriteLineAsync(initMsg);
        await writer.FlushAsync();

        string? initResponse = await reader.ReadLineAsync();
        Assert.NotNull(initResponse);
        using var initDoc = JsonDocument.Parse(initResponse);
        Assert.True(initDoc.RootElement.TryGetProperty("result", out var initResult));
        Assert.True(initResult.TryGetProperty("serverInfo", out var serverInfo));
        Assert.Equal("UnityLeanMcp.Mcp", serverInfo.GetProperty("name").GetString());
        Assert.True(initResult.TryGetProperty("capabilities", out var capabilities));
        Assert.True(capabilities.TryGetProperty("resources", out _));
        Assert.False(initResult.TryGetProperty("instructions", out _));

        // 2. Initialized notification
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await writer.FlushAsync();

        // 3. tools/list
        string listMsg = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}";
        await writer.WriteLineAsync(listMsg);
        await writer.FlushAsync();

        string? listResponse = await reader.ReadLineAsync();
        Assert.NotNull(listResponse);
        using var listDoc = JsonDocument.Parse(listResponse);
        Assert.True(listDoc.RootElement.TryGetProperty("result", out var listResult));
        Assert.True(listResult.TryGetProperty("tools", out var toolsElem));

        var toolNames = new HashSet<string>();
        foreach (var tool in toolsElem.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString()!);
        }

        Assert.DoesNotContain("unity_status", toolNames);
        Assert.Contains("unity_refresh", toolNames);
        Assert.DoesNotContain("unity_recompile", toolNames);
        Assert.Contains("unity_eval", toolNames);
        Assert.DoesNotContain("unity_execute_method", toolNames);
        Assert.DoesNotContain("unity_run_tests", toolNames);
        Assert.Contains("unity_test", toolNames);
        Assert.Contains("unity_coverage", toolNames);
        Assert.DoesNotContain("unity_stop", toolNames);
        Assert.DoesNotContain("unity_start", toolNames);
        Assert.Equal(4, toolNames.Count);

        var runTestsTool = toolsElem.EnumerateArray().First(t => t.GetProperty("name").GetString() == "unity_test");
        var inputSchema = runTestsTool.GetProperty("inputSchema");
        var properties = inputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("testNames", out _));
        Assert.True(properties.TryGetProperty("groupNames", out var groupNamesProp));
        Assert.True(properties.TryGetProperty("categoryNames", out _));
        Assert.True(properties.TryGetProperty("assemblyNames", out _));
        Assert.True(properties.TryGetProperty("mode", out _));
        Assert.True(properties.TryGetProperty("failedOnly", out _));
        Assert.True(properties.TryGetProperty("coverage", out _));

        var coverageTool = toolsElem.EnumerateArray().First(t => t.GetProperty("name").GetString() == "unity_coverage");
        var covSchema = coverageTool.GetProperty("inputSchema");
        var covProps = covSchema.GetProperty("properties");
        Assert.True(covProps.TryGetProperty("paths", out _));
        Assert.True(covProps.TryGetProperty("uncoveredOnly", out var uncoveredOnlyProp));
        var uncoveredOnlyTypes = uncoveredOnlyProp.GetProperty("type").ValueKind == JsonValueKind.Array
            ? uncoveredOnlyProp.GetProperty("type").EnumerateArray().Select(value => value.GetString()).ToArray()
            : [uncoveredOnlyProp.GetProperty("type").GetString()];
        Assert.Contains("boolean", uncoveredOnlyTypes);
        Assert.False(covProps.TryGetProperty("sortBy", out _));
        var covRequired = covSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("paths", covRequired);
        Assert.DoesNotContain("uncoveredOnly", covRequired);

        Assert.False(properties.TryGetProperty("testName", out _));
        Assert.False(properties.TryGetProperty("group", out _));
        Assert.False(properties.TryGetProperty("filter", out _));
        Assert.False(properties.TryGetProperty("category", out _));
        Assert.False(properties.TryGetProperty("assembly", out _));

        Assert.Contains(".NET Regular Expression patterns", groupNamesProp.GetProperty("description").GetString());
        Assert.Contains("array", groupNamesProp.GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", groupNamesProp.GetProperty("items").GetProperty("type").EnumerateArray().First(value => value.GetString() == "string").GetString());

        var modeProp = properties.GetProperty("mode");
        Assert.Equal("string", modeProp.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "editmode", "playmode" },
            modeProp.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray());

        writer.Close();
        if (!proc.WaitForExit(3000))
        {
            proc.Kill(true);
        }
    }

    [Fact]
    public async Task StdioCall_UnknownTool_ReturnsJsonRpcError()
    {
        string dllPath = McpTestClient.GetSubsystemServerDllPath();
        string projectRoot = McpTestClient.GetUnityProjectRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --project \"{projectRoot}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();

        var writer = proc.StandardInput;
        var reader = proc.StandardOutput;

        // Initialize
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"xunit-test\",\"version\":\"1.0\"}}}");
        await writer.FlushAsync();
        await reader.ReadLineAsync();

        // Initialized notification
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await writer.FlushAsync();

        // Call non-existent tool
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"non_existent_tool\",\"arguments\":{}}}");
        await writer.FlushAsync();

        string? callResponse = await reader.ReadLineAsync();
        Assert.NotNull(callResponse);
        using var callDoc = JsonDocument.Parse(callResponse);
        Assert.True(callDoc.RootElement.TryGetProperty("error", out _));

        writer.Close();
        if (!proc.WaitForExit(3000))
        {
            proc.Kill(true);
        }
    }

    [Fact]
    public async Task StdioResources_ListAndRead_ReturnsInspectHelpResource()
    {
        string dllPath = McpTestClient.GetSubsystemServerDllPath();
        string tempDir = Path.Combine(Path.GetTempPath(), $"unity-lean-mcp-res-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dllPath}\" --project \"{tempDir}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            Assert.NotNull(proc);

            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();

            var writer = proc.StandardInput;
            var reader = proc.StandardOutput;

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"xunit-test\",\"version\":\"1.0\"}}}");
            await writer.FlushAsync();
            string? initResponse = await reader.ReadLineAsync();
            Assert.NotNull(initResponse);

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await writer.FlushAsync();

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/list\",\"params\":{}}");
            await writer.FlushAsync();

            string? listResponse = await reader.ReadLineAsync();
            Assert.NotNull(listResponse);
            using var listDoc = JsonDocument.Parse(listResponse);
            Assert.True(listDoc.RootElement.TryGetProperty("result", out var listResult));
            Assert.True(listResult.TryGetProperty("resources", out var resourcesElem));

            var resources = resourcesElem.EnumerateArray().ToArray();
            Assert.Single(resources);
            var helpResource = resources[0];
            Assert.Equal(InspectHelpCatalog.ResourceUri, helpResource.GetProperty("uri").GetString());
            Assert.Equal(InspectHelpCatalog.ResourceName, helpResource.GetProperty("name").GetString());
            Assert.Equal(InspectHelpCatalog.ResourceTitle, helpResource.GetProperty("title").GetString());
            Assert.Equal(InspectHelpCatalog.ResourceMimeType, helpResource.GetProperty("mimeType").GetString());
            Assert.Equal(InspectHelpCatalog.ResourceDescription, helpResource.GetProperty("description").GetString());

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{\"uri\":\"unity://inspect/help\"}}");
            await writer.FlushAsync();

            string? readResponse = await reader.ReadLineAsync();
            Assert.NotNull(readResponse);
            using var readDoc = JsonDocument.Parse(readResponse);
            Assert.True(readDoc.RootElement.TryGetProperty("result", out var readResult));
            Assert.True(readResult.TryGetProperty("contents", out var contentsElem));

            var contents = contentsElem.EnumerateArray().ToArray();
            Assert.Single(contents);
            Assert.Equal(InspectHelpCatalog.ResourceUri, contents[0].GetProperty("uri").GetString());
            Assert.Equal(InspectHelpCatalog.ResourceMimeType, contents[0].GetProperty("mimeType").GetString());
            Assert.Equal(InspectHelpCatalog.FullHelpText, contents[0].GetProperty("text").GetString());

            Assert.Empty(Directory.GetFileSystemEntries(tempDir));

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/read\",\"params\":{\"uri\":\"unity://unknown/resource\"}}");
            await writer.FlushAsync();

            string? unknownResponse = await reader.ReadLineAsync();
            Assert.NotNull(unknownResponse);
            using var unknownDoc = JsonDocument.Parse(unknownResponse);
            Assert.True(unknownDoc.RootElement.TryGetProperty("error", out _));

            writer.Close();
            if (!proc.WaitForExit(3000))
            {
                proc.Kill(true);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
