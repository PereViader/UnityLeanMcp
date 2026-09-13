using System.Collections.Generic;

namespace UnityLeanMcp.Mcp;

public interface IUnityExecutableLocator
{
    string? FindUnityExecutable();
    string? FindInPath();
    string? GetProjectEditorVersion();
    List<string> GetStandardHubCandidatePaths(string version);
}
