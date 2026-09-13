using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// A collection of strings that can be deserialized from either a single JSON string
/// or a JSON array of strings, and implicitly converts to and from string and string[].
/// Encapsulates items as a read-only list protecting the non-empty, non-whitespace invariant.
/// </summary>
[JsonConverter(typeof(SingleOrArrayJsonConverter))]
[CollectionBuilder(typeof(SingleOrArray), nameof(Create))]
public sealed class SingleOrArray : IReadOnlyList<string>, IEquatable<SingleOrArray>
{
    private readonly List<string> _items;

    public static SingleOrArray Create(ReadOnlySpan<string?> items)
    {
        var list = new List<string>(items.Length);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                list.Add(item);
            }
        }
        return new SingleOrArray(list);
    }

    public SingleOrArray()
    {
        _items = new List<string>();
    }

    public SingleOrArray(string? single)
    {
        _items = new List<string>(1);
        if (!string.IsNullOrWhiteSpace(single))
        {
            _items.Add(single);
        }
    }

    public SingleOrArray(params string?[]? items)
        : this((IEnumerable<string?>?)items)
    {
    }

    public SingleOrArray(IEnumerable<string?>? collection)
    {
        if (collection == null)
        {
            _items = new List<string>();
        }
        else
        {
            _items = collection
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }
    }

    public int Count => _items.Count;

    public string this[int index] => _items[index];

    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public string[] ToArray() => _items.ToArray();

    public static implicit operator SingleOrArray?(string? single) =>
        string.IsNullOrWhiteSpace(single) ? null : new SingleOrArray(single);

    public static implicit operator SingleOrArray?(string[]? array) =>
        array == null ? null : new SingleOrArray(array);

    public static implicit operator string[]?(SingleOrArray? val) =>
        val == null ? null : val.ToArray();

    public static bool operator ==(SingleOrArray? left, SingleOrArray? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(SingleOrArray? left, SingleOrArray? right) =>
        !(left == right);

    public bool Equals(SingleOrArray? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (_items.Count != other._items.Count) return false;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i] != other._items[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) =>
        obj is SingleOrArray other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    public override string ToString() =>
        string.Join(", ", _items);
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
            return !string.IsNullOrWhiteSpace(s) ? new SingleOrArray(s) : null;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? s = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        items.Add(s);
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
            return new SingleOrArray(items);
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
