using System.Text.Json.Serialization;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// The execution modes supported by the Unity Test Framework integration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnityTestMode
{
    [JsonStringEnumMemberName("all")]
    All,

    [JsonStringEnumMemberName("editmode")]
    EditMode,

    [JsonStringEnumMemberName("playmode")]
    PlayMode
}
