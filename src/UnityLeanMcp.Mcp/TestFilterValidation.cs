using System;

namespace UnityLeanMcp.Mcp;

internal static class TestFilterValidation
{
    internal static bool TryValidate(
        string[]? testNames,
        string[]? groupNames,
        string[]? categoryNames,
        string[]? assemblyNames,
        out string error)
    {
        return TryValidate("testNames", testNames, out error, includeIndex: true) &&
            TryValidate("groupNames", groupNames, out error, includeIndex: true) &&
            TryValidate("categoryNames", categoryNames, out error, includeIndex: true) &&
            TryValidate("assemblyNames", assemblyNames, out error, includeIndex: true);
    }

    internal static bool TryValidate(string parameterName, string[]? values, out string error, bool includeIndex = false)
    {
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    error = CreateError(parameterName, 1, includeIndex ? i : null);
                    return false;
                }
            }
        }

        error = "";
        return true;
    }

    internal static string CreateError(string parameterName, int blankItemCount, int? index = null)
    {
        string location = index.HasValue ? $"[{index.Value}]" : "";
        string count = blankItemCount == 1 ? "one" : blankItemCount.ToString();
        return $"Invalid test filter '{parameterName}{location}': contains {count} empty or whitespace-only value(s).";
    }
}
