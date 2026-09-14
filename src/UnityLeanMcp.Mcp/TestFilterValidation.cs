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

    internal static bool TryValidate(SingleOrArray? values, string parameterName, out string error)
    {
        if (values == null || !values.HasBlankItems)
        {
            error = "";
            return true;
        }

        error = CreateError(parameterName, values.BlankItemCount);
        return false;
    }

    private static bool TryValidate(string parameterName, string[]? values, out string error)
    {
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                // Null entries have historically represented omitted values and
                // remain harmless. Empty and whitespace-only strings are invalid.
                if (values[i] != null && string.IsNullOrWhiteSpace(values[i]))
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
