using System.Collections.Generic;

namespace UnityLeanMcp.Mcp;

public readonly record struct UnityLocatorResult(string? ExecutablePath, string? Diagnostic)
{
    public bool Success => !string.IsNullOrWhiteSpace(ExecutablePath);

    public static UnityLocatorResult Found(string executablePath) => new(executablePath, null);

    public static UnityLocatorResult NotFound(string diagnostic) => new(null, diagnostic);
}

public interface IUnityExecutableLocator
{
    UnityLocatorResult FindUnityExecutable();
}
