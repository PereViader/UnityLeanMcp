using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "UnityIntegration")]
public class McpProtocolTests
{
    [Fact]
    public async Task StdioHandshake_And_ToolsList_ReturnsAllExpectedTools()
    {
        string dllPath = McpTestClient.GetMcpServerDllPath();
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
        Assert.True(initResult.TryGetProperty("instructions", out var instructions));
        Assert.Equal("unity_refresh verifies compilation diagnostics after editing scripts. unity_run_tests and unity_eval automatically compile and refresh pending changes before executing, so do not call unity_refresh immediately before evaluating code or running tests.", instructions.GetString());

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
        Assert.Contains("unity_run_tests", toolNames);
        Assert.Contains("unity_stop", toolNames);
        Assert.DoesNotContain("unity_start", toolNames);
        Assert.Equal(4, toolNames.Count);

        var runTestsTool = toolsElem.EnumerateArray().First(t => t.GetProperty("name").GetString() == "unity_run_tests");
        var inputSchema = runTestsTool.GetProperty("inputSchema");
        var properties = inputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("testNames", out _));
        Assert.True(properties.TryGetProperty("groupNames", out var groupNamesProp));
        Assert.True(properties.TryGetProperty("categoryNames", out _));
        Assert.True(properties.TryGetProperty("assemblyNames", out _));
        Assert.True(properties.TryGetProperty("mode", out _));
        Assert.True(properties.TryGetProperty("failedOnly", out _));

        Assert.False(properties.TryGetProperty("testName", out _));
        Assert.False(properties.TryGetProperty("group", out _));
        Assert.False(properties.TryGetProperty("filter", out _));
        Assert.False(properties.TryGetProperty("category", out _));
        Assert.False(properties.TryGetProperty("assembly", out _));

        Assert.Contains(".NET Regular Expression pattern(s)", groupNamesProp.GetProperty("description").GetString());

        // 4. tools/call unity_stop
        string callMsg = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"unity_stop\",\"arguments\":{}}}";
        await writer.WriteLineAsync(callMsg);
        await writer.FlushAsync();

        string? callResponse = await reader.ReadLineAsync();
        Assert.NotNull(callResponse);
        using var callDoc = JsonDocument.Parse(callResponse);
        Assert.True(callDoc.RootElement.TryGetProperty("result", out var callResult));
        Assert.False(callResult.TryGetProperty("isError", out var isErr) && isErr.GetBoolean());
        Assert.True(callResult.TryGetProperty("content", out var content));
        Assert.Equal(1, content.GetArrayLength());
        string stopText = content[0].GetProperty("text").GetString()!;
        Assert.True(stopText == "Unity background instance is not running." || stopText == "Stopped.", $"Unexpected stop text: {stopText}");

        writer.Close();
        if (!proc.WaitForExit(3000))
        {
            proc.Kill(true);
        }
    }

    [Fact]
    public async Task StdioCall_UnknownTool_ReturnsJsonRpcError()
    {
        string dllPath = McpTestClient.GetMcpServerDllPath();
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
}
