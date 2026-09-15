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
        return TryValidate("testNames", testNames, out error) &&
            TryValidate("groupNames", groupNames, out error) &&
            TryValidate("categoryNames", categoryNames, out error) &&
            TryValidate("assemblyNames", assemblyNames, out error);
    }

    internal static bool TryValidate(string[]? values, string parameterName, out string error)
    {
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    error = CreateError(parameterName, 1);
                    return false;
                }
            }
        }

        error = "";
        return true;
    }

    private static bool TryValidate(string parameterName, string[]? values, out string error)
    {
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    error = CreateError(parameterName, 1, i);
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
