namespace UnityLeanMcp.Mcp;

internal static class TestModeParser
{
    internal const string InvalidModeMessage =
        "Invalid test mode. Expected one of: all, editmode, or playmode. The mode must not be blank.";

    internal static bool TryNormalize(string? mode, out string normalizedMode)
    {
        normalizedMode = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalizedMode is "all" or "editmode" or "playmode";
    }

    internal static bool TryNormalize(UnityTestMode mode, out string normalizedMode)
    {
        normalizedMode = mode switch
        {
            UnityTestMode.All => "all",
            UnityTestMode.EditMode => "editmode",
            UnityTestMode.PlayMode => "playmode",
            _ => string.Empty
        };

        return normalizedMode.Length > 0;
    }
}
