using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityLeanMcp
{
    /// <summary>
    /// Lightweight, zero-dependency JSON reader and writer.
    /// Operates on plain dictionaries and lists without mapping to concrete schemas,
    /// preserving ISO date strings and unrelated properties.
    /// Has no Unity engine dependencies, making it safe for worker threads.
    /// </summary>
    internal static class UnityLeanJson
    {
        public static Dictionary<string, object> DeserializeObject(string json, IEqualityComparer<string> keyComparer = null)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("JSON content is empty.");
            }

            var reader = new Reader(json, keyComparer);
            return reader.ReadRootObject();
        }

        public static object Deserialize(string json, IEqualityComparer<string> keyComparer = null)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("JSON content is empty.");
            }

            var reader = new Reader(json, keyComparer);
            return reader.ReadRootValue();
        }

        public static string Serialize(object value, bool prettyPrint = false)
        {
            var sb = new StringBuilder();
            SerializeValue(sb, value, prettyPrint, 0);
            if (prettyPrint)
            {
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void SerializeValue(StringBuilder sb, object value, bool prettyPrint, int indentLevel)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }

            if (value is IDictionary dict)
            {
                SerializeDictionary(sb, dict, prettyPrint, indentLevel);
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                SerializeArray(sb, enumerable, prettyPrint, indentLevel);
                return;
            }

            if (value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is float f)
            {
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal dec)
            {
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void SerializeDictionary(StringBuilder sb, IDictionary dict, bool prettyPrint, int indentLevel)
        {
            if (dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            if (prettyPrint) sb.Append('\n');

            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first)
                {
                    sb.Append(',');
                    if (prettyPrint) sb.Append('\n');
                }
                first = false;

                if (prettyPrint)
                {
                    sb.Append(' ', (indentLevel + 1) * 2);
                }

                WriteString(sb, entry.Key.ToString());
                sb.Append(prettyPrint ? ": " : ":");
                SerializeValue(sb, entry.Value, prettyPrint, indentLevel + 1);
            }

            if (prettyPrint)
            {
                sb.Append('\n');
                sb.Append(' ', indentLevel * 2);
            }
            sb.Append('}');
        }

        private static void SerializeArray(StringBuilder sb, IEnumerable enumerable, bool prettyPrint, int indentLevel)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(item);
            }

            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            if (prettyPrint) sb.Append('\n');

            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                    if (prettyPrint) sb.Append('\n');
                }

                if (prettyPrint)
                {
                    sb.Append(' ', (indentLevel + 1) * 2);
                }

                SerializeValue(sb, list[i], prettyPrint, indentLevel + 1);
            }

            if (prettyPrint)
            {
                sb.Append('\n');
                sb.Append(' ', indentLevel * 2);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _json;
            private readonly IEqualityComparer<string> _comparer;
            private int _index;

            public Reader(string json, IEqualityComparer<string> comparer)
            {
                _json = json;
                _comparer = comparer ?? StringComparer.Ordinal;
            }

            public Dictionary<string, object> ReadRootObject()
            {
                SkipWhitespaceAndComments();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                if (_json[_index] != '{')
                {
                    throw new FormatException("The JSON root must be an object.");
                }

                var result = ReadObject();
                SkipWhitespaceAndComments();
                if (_index != _json.Length)
                {
                    throw new FormatException("Unexpected content after JSON document.");
                }

                return result;
            }

            public object ReadRootValue()
            {
                SkipWhitespaceAndComments();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                var result = ReadValue();
                SkipWhitespaceAndComments();
                if (_index != _json.Length)
                {
                    throw new FormatException("Unexpected content after JSON document.");
                }

                return result;
            }

            private Dictionary<string, object> ReadObject()
            {
                Expect('{');
                var values = new Dictionary<string, object>(_comparer);
                SkipWhitespaceAndComments();
                if (TryConsume('}'))
                {
                    return values;
                }

                while (true)
                {
                    SkipWhitespaceAndComments();
                    string key = ReadString();
                    SkipWhitespaceAndComments();
                    Expect(':');
                    values[key] = ReadValue();
                    SkipWhitespaceAndComments();
                    if (TryConsume('}'))
                    {
                        return values;
                    }
                    Expect(',');
                }
            }

            private List<object> ReadArray()
            {
                Expect('[');
                var values = new List<object>();
                SkipWhitespaceAndComments();
                if (TryConsume(']'))
                {
                    return values;
                }

                while (true)
                {
                    values.Add(ReadValue());
                    SkipWhitespaceAndComments();
                    if (TryConsume(']'))
                    {
                        return values;
                    }
                    Expect(',');
                }
            }

            private object ReadValue()
            {
                SkipWhitespaceAndComments();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                char c = _json[_index];
                if (c == '"') return ReadString();
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (StartsWithKeyword("true")) { _index += 4; return true; }
                if (StartsWithKeyword("false")) { _index += 5; return false; }
                if (StartsWithKeyword("null")) { _index += 4; return null; }
                if (c == '-' || char.IsDigit(c)) return ReadNumber();

                throw new FormatException($"Unexpected character '{c}' in JSON at position {_index}.");
            }

            private string ReadString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (_index < _json.Length)
                {
                    char c = _json[_index++];
                    if (c == '"') return builder.ToString();
                    if (c == '\\')
                    {
                        if (_index >= _json.Length) throw new FormatException("Invalid JSON escape.");
                        char escape = _json[_index++];
                        switch (escape)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u': builder.Append(ReadUnicodeEscape()); break;
                            default: throw new FormatException("Invalid JSON escape.");
                        }
                    }
                    else if (c < 32)
                    {
                        throw new FormatException("Unescaped control character in JSON string.");
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _json.Length) throw new FormatException("Invalid JSON unicode escape.");
                string hex = _json.Substring(_index, 4);
                _index += 4;
                if (!ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
                {
                    throw new FormatException("Invalid JSON unicode escape.");
                }
                return (char)value;
            }

            private object ReadNumber()
            {
                int start = _index;
                if (_index < _json.Length && _json[_index] == '-')
                {
                    _index++;
                }

                if (_index >= _json.Length || !char.IsDigit(_json[_index]))
                {
                    throw new FormatException("Invalid JSON number.");
                }

                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                bool isFloatingPoint = false;
                if (_index < _json.Length && _json[_index] == '.')
                {
                    isFloatingPoint = true;
                    _index++;
                    if (_index >= _json.Length || !char.IsDigit(_json[_index]))
                    {
                        throw new FormatException("Invalid JSON number.");
                    }
                    while (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    isFloatingPoint = true;
                    _index++;
                    if (_index < _json.Length && (_json[_index] == '+' || _json[_index] == '-'))
                    {
                        _index++;
                    }
                    if (_index >= _json.Length || !char.IsDigit(_json[_index]))
                    {
                        throw new FormatException("Invalid JSON number.");
                    }
                    while (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                string numStr = _json.Substring(start, _index - start);
                if (!isFloatingPoint && long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal))
                {
                    return longVal;
                }

                if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleVal))
                {
                    return doubleVal;
                }

                throw new FormatException("Invalid JSON number.");
            }

            private bool StartsWithKeyword(string value)
            {
                if (_index + value.Length > _json.Length)
                {
                    return false;
                }

                if (string.CompareOrdinal(_json, _index, value, 0, value.Length) != 0)
                {
                    return false;
                }

                if (_index + value.Length < _json.Length)
                {
                    char next = _json[_index + value.Length];
                    if (char.IsLetterOrDigit(next) || next == '_')
                    {
                        return false;
                    }
                }

                return true;
            }

            private void SkipWhitespaceAndComments()
            {
                while (_index < _json.Length)
                {
                    char c = _json[_index];
                    if (char.IsWhiteSpace(c))
                    {
                        _index++;
                    }
                    else if (c == '/' && _index + 1 < _json.Length && _json[_index + 1] == '/')
                    {
                        _index += 2;
                        while (_index < _json.Length && _json[_index] != '\n' && _json[_index] != '\r')
                        {
                            _index++;
                        }
                    }
                    else if (c == '/' && _index + 1 < _json.Length && _json[_index + 1] == '*')
                    {
                        _index += 2;
                        while (_index + 1 < _json.Length && !(_json[_index] == '*' && _json[_index + 1] == '/'))
                        {
                            _index++;
                        }

                        if (_index + 1 < _json.Length)
                        {
                            _index += 2;
                        }
                        else
                        {
                            throw new FormatException("Unterminated block comment.");
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            private void Expect(char expected)
            {
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    throw new FormatException($"Invalid JSON structure. Expected '{expected}'.");
                }
                _index++;
            }

            private bool TryConsume(char value)
            {
                if (_index < _json.Length && _json[_index] == value)
                {
                    _index++;
                    return true;
                }
                return false;
            }
        }
    }
}
