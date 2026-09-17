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
            string text = UnityProcessManager.ReadFileWithRetry(logFilePath, fromOffset: initialOffset);
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int start = Math.Max(0, lines.Length - 25);
            return "Last log lines:\n" + string.Join(Environment.NewLine, lines[start..]);
        }
        catch (Exception ex)
        {
            return $"Error reading log file: {ex.Message}";
        }
    }
}
