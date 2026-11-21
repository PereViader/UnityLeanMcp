using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityLeanMcp.Mcp;

public interface IUnityLogScanner
{
    bool HasCompilationErrors(string logText);
    List<string> ExtractUniqueCompilationLines(string logText);
    string GetLogSnippet(string logFilePath, long initialOffset = 0);
    string? ExtractLatestStartupMilestone(string logText);
}

public class UnityLogScanner : IUnityLogScanner
{
    private static readonly Regex s_CompileErrorRegex = new(
        @"^.+?\([0-9]+,[0-9]+\):\s*error\s+[a-zA-Z0-9]+:",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_CompileDiagRegex = new(
        @"^.+?\([0-9]+,[0-9]+\):\s*(error|warning)\s+[a-zA-Z0-9]+:.*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static Regex CompileErrorRegex => s_CompileErrorRegex;
    public static Regex CompileDiagRegex => s_CompileDiagRegex;

    public virtual bool HasCompilationErrors(string logText)
    {
        if (string.IsNullOrEmpty(logText)) return false;
        return s_CompileErrorRegex.IsMatch(logText);
    }

    public virtual List<string> ExtractUniqueCompilationLines(string logText)
    {
        if (string.IsNullOrEmpty(logText)) return new List<string>();

        var matches = s_CompileDiagRegex.Matches(logText);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in matches)
        {
            string line = m.Value.Trim();
            if (!string.IsNullOrEmpty(line) && seen.Add(line))
            {
                result.Add(line);
            }
        }
        return result;
    }

    public virtual string GetLogSnippet(string logFilePath, long initialOffset = 0)
    {
        if (!File.Exists(logFilePath)) return "No Unity log file found.";
        try
        {
            string text = FileIoRetryHelper.ReadFileWithRetry(logFilePath, fromOffset: initialOffset);
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int start = Math.Max(0, lines.Length - 25);
            return "Last log lines:" + Environment.NewLine + string.Join(Environment.NewLine, lines[start..]);
        }
        catch (Exception ex)
        {
            return $"Error reading log file: {ex.Message}";
        }
    }

    public virtual string? ExtractLatestStartupMilestone(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return null;
        }

        string? latest = null;
        var lines = logText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (ContainsAny(line, "ReloadAssembly", "Domain Reload", "Reloading assemblies", "Reloading domain"))
            {
                latest = "Starting Unity Editor: Reloading domain...";
            }
            else if (ContainsAny(line, "[ScriptCompilation]", "Compilation started", "Compiling scripts", "Starting C# compiler", "Compiling "))
            {
                latest = "Starting Unity Editor: Compiling scripts...";
            }
            else if (ContainsAny(line, "Start importing", "Rebuilding", "Updating Assets", "AssetDatabase", "detecting if any assets need to be imported"))
            {
                latest = "Starting Unity Editor: Importing assets...";
            }
            else if (ContainsAny(line, "[Package Manager]", "Resolving packages", "Restoring packages"))
            {
                latest = "Starting Unity Editor: Resolving packages...";
            }
        }

        return latest;
    }

    private static bool ContainsAny(string line, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
