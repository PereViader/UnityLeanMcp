using System.Text;

namespace UnityLeanMcp
{
    /// <summary>
    /// Centralized protocol codec for escaping and unescaping lines
    /// across the UnityLeanMcp socket and file protocol.
    /// </summary>
    internal static class ProtocolCodec
    {
        public static string EscapeLine(string text)
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

        public static string UnescapeLine(string text)
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
    }
}
