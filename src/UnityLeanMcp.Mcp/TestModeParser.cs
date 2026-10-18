namespace UnityLeanMcp.Mcp;

internal static class TestModeParser
{
    internal const string InvalidModeMessage =
        "Invalid test mode. Expected one of: editmode or playmode. The mode must not be blank.";

    internal const string RemovedAllModeMessage =
        "The 'all' test mode has been removed. Tests must explicitly specify 'editmode' (for unit tests and editor utilities) or 'playmode' (for integration tests and tests requiring scene loading or MonoBehaviour lifecycle).";

    internal static bool TryNormalize(string? mode, out string normalizedMode)
    {
        return TryNormalize(mode, out normalizedMode, out _);
    }

    internal static bool TryNormalize(string? mode, out string normalizedMode, out string? errorMessage)
    {
        normalizedMode = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedMode == "all")
        {
            errorMessage = RemovedAllModeMessage;
            return false;
        }

        if (normalizedMode is "editmode" or "playmode")
        {
            errorMessage = null;
            return true;
        }

        errorMessage = InvalidModeMessage;
        return false;
    }

    internal static bool TryNormalize(UnityTestMode mode, out string normalizedMode)
    {
        return TryNormalize(mode, out normalizedMode, out _);
    }

    internal static bool TryNormalize(UnityTestMode mode, out string normalizedMode, out string? errorMessage)
    {
        normalizedMode = mode switch
        {
            UnityTestMode.EditMode => "editmode",
            UnityTestMode.PlayMode => "playmode",
            _ => string.Empty
        };

        if (normalizedMode.Length > 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = InvalidModeMessage;
        return false;
    }

    internal static bool TryParseMode(string? mode, out UnityTestMode testMode)
    {
        string trimmed = mode?.Trim() ?? string.Empty;
        if (string.Equals(trimmed, "editmode", StringComparison.OrdinalIgnoreCase))
        {
            testMode = UnityTestMode.EditMode;
            return true;
        }

        if (string.Equals(trimmed, "playmode", StringComparison.OrdinalIgnoreCase))
        {
            testMode = UnityTestMode.PlayMode;
            return true;
        }

        testMode = default;
        return false;
    }
}

