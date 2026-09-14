using System;
using System.Text;

namespace UnityLeanMcp.Mcp;

internal static class McpOutputLimits
{
    public const int MaxFormattedOutputCharacters = 64 * 1024;
    // Public MCP text responses use the same 64 KiB ceiling in UTF-8 bytes as in
    // UTF-16 characters, keeping multibyte output from expanding the AI payload.
    public const int MaxFormattedOutputBytes = 64 * 1024;
    public const int MaxLogEntryCharacters = 4 * 1024;
    public const int MaxLogOutputCharacters = 32 * 1024;
    public const int MaxFailureIdentifierCharacters = 512;
    public const int MaxFailureMessageCharacters = 4 * 1024;
    public const int MaxFailureStackTraceCharacters = 12 * 1024;
    public const int MaxFailureSummaryMessageCharacters = 200;
    public const int MaxFailureSummaryCharacters = 16 * 1024;

    public const string LogEntryTruncationMarker = "... (truncated: log entry exceeded 4,096 characters)";
    public const string LogOutputTruncationMarker = "... (truncated: log output exceeded 32,768 characters)";
    public const string FailureIdentifierTruncationMarker = "... (truncated: failure identifier exceeded 512 characters)";
    public const string FailureMessageTruncationMarker = "... (truncated: failure message exceeded 4,096 characters)";
    public const string FailureStackTraceTruncationMarker = "... (truncated: test stack trace exceeded 12,288 characters)";
    public const string DetailedFailureOutputTruncationMarker = "... (truncated: detailed failure output reserved space for compact summaries)";
    public const string FailureSummaryOutputTruncationMarker = "... (truncated: compact failure summaries exceeded 16,384 characters)";
    public const string AggregateOutputTruncationMarker = "... (truncated: MCP output exceeded 65,536 characters)";

    public static string Truncate(string? value, int maxCharacters, string marker)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
        {
            return value ?? string.Empty;
        }

        if (maxCharacters <= 0)
        {
            return string.Empty;
        }

        if (marker.Length >= maxCharacters)
        {
            return marker.Substring(0, GetSafePrefixLength(marker, maxCharacters));
        }

        return value.Substring(0, GetSafePrefixLength(value, maxCharacters - marker.Length)) + marker;
    }

    internal static int GetSafePrefixLength(string value, int maxCharacters)
    {
        int prefixLength = Math.Min(Math.Max(0, maxCharacters), value.Length);
        if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
        {
            prefixLength--;
        }

        return prefixLength;
    }
}

/// <summary>
/// Appends output in one pass while reserving space for a deterministic aggregate marker.
/// </summary>
internal sealed class BoundedTextBuilder
{
    private readonly StringBuilder m_Builder;
    private readonly int m_MaxCharacters;
    private readonly int m_MaxBytes;
    private readonly string m_AggregateMarker;
    private int m_Utf8ByteLength;
    private bool m_Truncated;

    public BoundedTextBuilder(int maxCharacters, string aggregateMarker)
        : this(maxCharacters, McpOutputLimits.MaxFormattedOutputBytes, aggregateMarker)
    {
    }

    internal BoundedTextBuilder(int maxCharacters, int maxBytes, string aggregateMarker)
    {
        if (maxCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        m_Builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        m_MaxCharacters = maxCharacters;
        m_MaxBytes = maxBytes;
        m_AggregateMarker = aggregateMarker ?? string.Empty;
    }

    public void Append(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            AppendCore(value, 0, value.Length);
        }
    }

    public void AppendLine(string? value = null)
    {
        Append(value);
        Append(Environment.NewLine);
    }

    public void AppendBounded(string? value, int maxCharacters, string marker)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (maxCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        if (value.Length <= maxCharacters)
        {
            Append(value);
            return;
        }

        int prefixLength = McpOutputLimits.GetSafePrefixLength(value, maxCharacters - marker.Length);
        AppendCore(value, 0, prefixLength);
        AppendCore(marker, 0, marker.Length);
    }

    public void AppendTrimmedBounded(string? value, int maxCharacters, string marker)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        int length = value.Length;
        while (length > 0 && char.IsWhiteSpace(value[length - 1]))
        {
            length--;
        }

        if (length == 0)
        {
            return;
        }

        if (length <= maxCharacters)
        {
            AppendCore(value, 0, length);
            return;
        }

        int prefixLength = McpOutputLimits.GetSafePrefixLength(value, maxCharacters - marker.Length);
        AppendCore(value, 0, prefixLength);
        AppendCore(marker, 0, marker.Length);
    }

    public override string ToString() => m_Builder.ToString();

    public int Length => m_Builder.Length;

    internal int Utf8ByteLength => m_Utf8ByteLength;

    private void AppendCore(string value, int startIndex, int count)
    {
        if (m_Truncated || count <= 0)
        {
            return;
        }

        int remainingCharacters = m_MaxCharacters - m_Builder.Length;
        int remainingBytes = m_MaxBytes - m_Utf8ByteLength;
        if (remainingCharacters <= 0 || remainingBytes <= 0)
        {
            AppendAggregateMarker();
            return;
        }

        int prefixLength = GetFittingPrefixLength(
            value,
            startIndex,
            count,
            remainingCharacters,
            remainingBytes,
            out int prefixBytes);

        if (prefixLength == count)
        {
            AppendRange(value, startIndex, prefixLength, prefixBytes);
            return;
        }

        if (prefixLength > 0)
        {
            AppendRange(value, startIndex, prefixLength, prefixBytes);
        }

        AppendAggregateMarker();
    }

    private void AppendRange(string value, int startIndex, int count, int byteCount)
    {
        m_Builder.Append(value, startIndex, count);
        m_Utf8ByteLength += byteCount;
    }

    private void AppendAggregateMarker()
    {
        if (m_Truncated || string.IsNullOrEmpty(m_AggregateMarker))
        {
            m_Truncated = true;
            return;
        }

        int markerLength = GetFittingPrefixLength(
            m_AggregateMarker,
            0,
            m_AggregateMarker.Length,
            m_MaxCharacters,
            m_MaxBytes,
            out int markerBytes);
        if (markerLength <= 0)
        {
            m_Truncated = true;
            return;
        }

        while (m_Builder.Length + markerLength > m_MaxCharacters ||
               m_Utf8ByteLength + markerBytes > m_MaxBytes)
        {
            if (!RemoveLastTextElement())
            {
                break;
            }
        }

        int availableCharacters = m_MaxCharacters - m_Builder.Length;
        int availableBytes = m_MaxBytes - m_Utf8ByteLength;
        if (availableCharacters >= markerLength && availableBytes >= markerBytes)
        {
            AppendRange(m_AggregateMarker, 0, markerLength, markerBytes);
        }

        m_Truncated = true;
    }

    private bool RemoveLastTextElement()
    {
        int length = m_Builder.Length;
        if (length == 0)
        {
            return false;
        }

        int removeLength = 1;
        if (length >= 2 &&
            char.IsLowSurrogate(m_Builder[length - 1]) &&
            char.IsHighSurrogate(m_Builder[length - 2]))
        {
            removeLength = 2;
        }

        int byteCount = GetUtf8ByteCount(m_Builder, length - removeLength, removeLength);
        m_Builder.Remove(length - removeLength, removeLength);
        m_Utf8ByteLength -= byteCount;
        return true;
    }

    private static int GetFittingPrefixLength(
        string value,
        int startIndex,
        int count,
        int maxCharacters,
        int maxBytes,
        out int byteCount)
    {
        int endIndex = startIndex + count;
        int index = startIndex;
        int characters = 0;
        byteCount = 0;

        while (index < endIndex)
        {
            int elementLength = 1;
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= endIndex || !char.IsLowSurrogate(value[index + 1]))
                {
                    break;
                }

                elementLength = 2;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                break;
            }

            if (characters + elementLength > maxCharacters)
            {
                break;
            }

            int elementBytes = GetUtf8ByteCount(value, index, elementLength);
            if (byteCount + elementBytes > maxBytes)
            {
                break;
            }

            characters += elementLength;
            byteCount += elementBytes;
            index += elementLength;
        }

        return characters;
    }

    private static int GetUtf8ByteCount(StringBuilder value, int startIndex, int count)
    {
        int byteCount = 0;
        int endIndex = startIndex + count;
        for (int index = startIndex; index < endIndex; index++)
        {
            byteCount += GetUtf8ByteCount(value[index], index + 1 < endIndex ? value[index + 1] : null, out bool consumedPair);
            if (consumedPair)
            {
                index++;
            }
        }

        return byteCount;
    }

    private static int GetUtf8ByteCount(string value, int startIndex, int count)
    {
        int byteCount = 0;
        int endIndex = startIndex + count;
        for (int index = startIndex; index < endIndex; index++)
        {
            byteCount += GetUtf8ByteCount(value[index], index + 1 < endIndex ? value[index + 1] : null, out bool consumedPair);
            if (consumedPair)
            {
                index++;
            }
        }

        return byteCount;
    }

    private static int GetUtf8ByteCount(char value, char? nextValue, out bool consumedPair)
    {
        if (char.IsHighSurrogate(value) && nextValue.HasValue && char.IsLowSurrogate(nextValue.Value))
        {
            consumedPair = true;
            return 4;
        }

        consumedPair = false;
        if (value <= 0x7F)
        {
            return 1;
        }

        if (value <= 0x7FF)
        {
            return 2;
        }

        return 3;
    }
}
