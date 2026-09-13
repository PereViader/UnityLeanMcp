using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// A collection of strings that can be deserialized from either a single JSON string
/// or a JSON array of strings, and implicitly converts to and from string and string[].
/// </summary>
[JsonConverter(typeof(SingleOrArrayJsonConverter))]
public class SingleOrArray : List<string>, IEquatable<SingleOrArray>
{
    public SingleOrArray() { }

    public SingleOrArray(string single) : base(1)
    {
        if (!string.IsNullOrWhiteSpace(single))
        {
            Add(single);
        }
    }

    public SingleOrArray(params string[] items)
        : this((IEnumerable<string>)items)
    {
    }

    public SingleOrArray(IEnumerable<string> collection)
        : base(collection.Where(s => !string.IsNullOrWhiteSpace(s)))
    {
    }

    public static implicit operator SingleOrArray?(string? single) =>
        string.IsNullOrWhiteSpace(single) ? null : new SingleOrArray(single);

    public static implicit operator SingleOrArray?(string[]? array) =>
        array == null ? null : new SingleOrArray(array);

    public static implicit operator string[]?(SingleOrArray? val) =>
        val == null ? null : val.ToArray();

    public bool Equals(SingleOrArray? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (Count != other.Count) return false;
        for (int i = 0; i < Count; i++)
        {
            if (this[i] != other[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) =>
        obj is SingleOrArray other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in this)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    public override string ToString() =>
        string.Join(", ", (IEnumerable<string>)this);
}

/// <summary>
/// Deserializes JSON string to SingleOrArray with one element, or JSON array to SingleOrArray.
/// </summary>
public class SingleOrArrayJsonConverter : JsonConverter<SingleOrArray>
{
    public override SingleOrArray? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? s = reader.GetString();
            return !string.IsNullOrWhiteSpace(s) ? new SingleOrArray(s) : new SingleOrArray();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var result = new SingleOrArray();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? s = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result.Add(s);
                    }
                }
                else if (reader.TokenType == JsonTokenType.Null)
                {
                    // Ignore null items within array
                }
                else
                {
                    throw new JsonException($"Unexpected token type '{reader.TokenType}' inside array for {nameof(SingleOrArray)}.");
                }
            }
            return result;
        }

        throw new JsonException($"Unexpected token type '{reader.TokenType}' for {nameof(SingleOrArray)}. Expected string or array of strings.");
    }

    public override void Write(Utf8JsonWriter writer, SingleOrArray value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
