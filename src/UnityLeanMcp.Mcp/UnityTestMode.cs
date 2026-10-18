using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityLeanMcp.Mcp;

public sealed class UnityTestModeJsonConverter : JsonConverter<UnityTestMode>
{
    public override UnityTestMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            if (TestModeParser.TryParseMode(str, out var mode))
            {
                return mode;
            }
            throw new JsonException($"Invalid test mode: '{str}'. Expected 'editmode' or 'playmode'.");
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int intVal))
        {
            if (Enum.IsDefined(typeof(UnityTestMode), intVal))
            {
                return (UnityTestMode)intVal;
            }
        }
        throw new JsonException($"Expected string or valid integer for UnityTestMode, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, UnityTestMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == UnityTestMode.PlayMode ? "playmode" : "editmode");
    }
}

/// <summary>
/// The execution modes supported by the Unity Test Framework integration.
/// </summary>
[JsonConverter(typeof(UnityTestModeJsonConverter))]
public enum UnityTestMode
{
    [JsonStringEnumMemberName("editmode")]
    EditMode,

    [JsonStringEnumMemberName("playmode")]
    PlayMode
}

