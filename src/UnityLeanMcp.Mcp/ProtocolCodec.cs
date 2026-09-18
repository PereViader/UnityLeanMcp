using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// Centralized protocol codec for escaping and unescaping tokens, parameters, and lines
/// across the UnityLeanMcp socket and file protocol.
/// </summary>
public static class ProtocolCodec
{
    /// <summary>
    /// Escapes a single token or parameter value by escaping backslashes, quotes, carriage returns, newlines, and tabs.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? EscapeToken(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append(@"\"""); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Alias for <see cref="EscapeToken(string?)"/> for command parameter values.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? EscapeParam(string? text) => EscapeToken(text);

    /// <summary>
    /// Unescapes a single token or parameter value by unescaping \\, \", \r, \n, \t.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? UnescapeToken(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case '\\': sb.Append('\\'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    default:
                        sb.Append('\\');
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a full line payload by escaping backslashes, carriage returns, newlines, and tabs.
    /// Does not escape quotes, preserving them as literal characters.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? EscapeLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Alias for <see cref="EscapeLine(string?)"/> for code snippets sent on a single line.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? EscapeCode(string? text) => EscapeLine(text);

    /// <summary>
    /// Unescapes a full line payload by unescaping \\, \r, \n, \t.
    /// Does not unescape quotes.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? UnescapeLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case '\\': sb.Append('\\'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    default:
                        sb.Append('\\');
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strips protocol status prefixes (ERROR:, FAILURE:, SUCCESS:, or word followed by space)
    /// from response strings.
    /// </summary>
    public static string StripStatusPrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[6..].Trim();
        }

        if (trimmed.StartsWith("FAILURE:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[8..].Trim();
        }

        if (trimmed.StartsWith("SUCCESS:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[8..].Trim();
        }

        int spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0)
        {
            return trimmed[(spaceIdx + 1)..].Trim();
        }

        if (trimmed.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("FAILURE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    /// <summary>
    /// Attempts to parse an immediate terminal result (INTERRUPTION, ERROR, FAILURE) from a socket response.
    /// </summary>
    public static TResult? TryCreateImmediateTerminalResult<TResult>(string? response, string operationId) where TResult : class, IOperationResult, new()
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        string trimmed = response.Trim();
        if (trimmed.StartsWith("INTERRUPTION", StringComparison.OrdinalIgnoreCase))
        {
            string message = trimmed.Length > 12 ? trimmed[12..].Trim() : "Operation interrupted.";
            return new TResult
            {
                OperationId = operationId,
                Success = false,
                Interrupted = true,
                Message = UnescapeLine(message)
            };
        }

        if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FAILURE", StringComparison.OrdinalIgnoreCase))
        {
            return new TResult
            {
                OperationId = operationId,
                Success = false,
                Message = UnescapeLine(StripStatusPrefix(trimmed))
            };
        }

        return null;
    }
}
