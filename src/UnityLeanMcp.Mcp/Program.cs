using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UnityLeanMcp.Mcp;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("UnityLeanMcp MCP Server");
    Console.WriteLine("Exposes Unity Editor control and testing tools via Model Context Protocol (MCP).");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  UnityLeanMcp.Mcp [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -p, --project <path>    Path to Unity project root directory.");
    Console.WriteLine("  -h, --help              Show command line help.");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  UNITY_LEAN_MCP_PROJECT_ROOT  Path to Unity project root directory.");
    Console.WriteLine("  UNITY_PATH                   Path to Unity Editor executable.");
    Console.WriteLine("  UNITY_EDITOR                 Path to Unity Editor executable.");
    return 0;
}

string? projectPath = null;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--project" || args[i] == "-p") && i + 1 < args.Length)
    {
        projectPath = args[++i];
    }
}

if (string.IsNullOrWhiteSpace(projectPath))
{
    projectPath = Environment.GetEnvironmentVariable("UNITY_LEAN_MCP_PROJECT_ROOT");
}

if (string.IsNullOrWhiteSpace(projectPath))
{
    projectPath = ResolveProjectRoot();
}

string resolvedProjectRoot = Path.GetFullPath(projectPath);

var builder = Host.CreateApplicationBuilder(args);

// Direct all console logging to stderr to prevent log pollution on stdout (used by MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IUnityPathResolver>(new UnityPathResolver(resolvedProjectRoot));
builder.Services.AddSingleton<IUnityExecutableLocator, UnityExecutableLocator>();
builder.Services.AddSingleton<IUnityLogScanner, UnityLogScanner>();
builder.Services.AddSingleton<IUnitySocketTransport, UnitySocketTransport>();
builder.Services.AddSingleton<IUnityProcessManager, UnityProcessManager>();
builder.Services.AddSingleton<IOperationPoller, OperationPoller>();
builder.Services.AddSingleton<IUnityClient, UnityClient>();
builder.Services.AddSingleton<IDiagnosticFormatter, DiagnosticFormatter>();
builder.Services.AddSingleton<UnityTools>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = "unity_refresh verifies compilation diagnostics after editing scripts. unity_run_tests and unity_eval automatically compile and refresh pending changes before executing, so do not call unity_refresh immediately before evaluating code or running tests.";
    })
    .WithStdioServerTransport()
    .WithTools<UnityTools>();

var app = builder.Build();
await app.RunAsync();
return 0;

static string ResolveProjectRoot()
{
    string current = Directory.GetCurrentDirectory();
    var dir = new DirectoryInfo(current);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) &&
            Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
        {
            return dir.FullName;
        }

        string candidate = Path.Combine(dir.FullName, "src", "UnityLeanMcp.Unity3d");
        if (Directory.Exists(Path.Combine(candidate, "Assets")) &&
            Directory.Exists(Path.Combine(candidate, "ProjectSettings")))
        {
            return candidate;
        }

        dir = dir.Parent;
    }
    return current;
}
