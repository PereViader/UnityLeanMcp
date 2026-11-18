using System;
using System.IO;
using System.Text;

namespace UnityLeanMcp.Mcp;

public static class FileUriBuilder
{
    public static bool IsPathRootedCrossPlatform(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (Path.IsPathRooted(path))
            return true;

        if (path.StartsWith('/') || path.StartsWith('\\'))
            return true;

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            if (path.Length == 2 || path[2] == '/' || path[2] == '\\')
                return true;
        }

        return false;
    }

    public static bool IsEvalSynthetic(string? file) =>
        string.Equals(file, "eval", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file, "snippet", StringComparison.OrdinalIgnoreCase) ||
        (file != null && file.StartsWith('<'));

    public static bool IsPackageCache(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return false;

        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/PackageCache/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("PackageCache/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Library/PackageCache/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Library/PackageCache/", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildFileUri(string rawFile, int? lineNumber, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(rawFile) || IsEvalSynthetic(rawFile))
            return string.Empty;

        if (rawFile.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return BuildExistingFileUri(rawFile, lineNumber);

        string resolvedPath = rawFile;
        string normalizedRaw = resolvedPath.Replace('\\', '/');

        // Normalize package and project-relative paths that may have leading slashes
        if (normalizedRaw.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase) ||
            normalizedRaw.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            resolvedPath = normalizedRaw[1..];
        }

        if (!IsPathRootedCrossPlatform(resolvedPath) && !string.IsNullOrWhiteSpace(projectRoot))
        {
            string root = projectRoot.Replace('\\', '/').TrimEnd('/');
            string rel = resolvedPath.Replace('\\', '/').TrimStart('/');
            while (rel.StartsWith("./", StringComparison.Ordinal))
            {
                rel = rel[2..];
            }
            resolvedPath = $"{root}/{rel}";
        }

        string normalizedPath = resolvedPath.Replace('\\', '/');
        while (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        string anchor = lineNumber.HasValue ? $"#L{lineNumber.Value}" : "";

        // A Windows UNC path is represented by the URI authority (the server),
        // not by an extra pair of leading slashes in the URI path.
        if (normalizedPath.StartsWith("//", StringComparison.Ordinal) &&
            !normalizedPath.StartsWith("///", StringComparison.Ordinal))
        {
            string uncPath = normalizedPath[2..];
            int separatorIndex = uncPath.IndexOf('/');
            if (separatorIndex > 0)
            {
                string authority = EncodeUriAuthority(uncPath[..separatorIndex], preserveEscapes: false);
                string path = EncodeUriPath(uncPath[separatorIndex..], preserveEscapes: false);
                return $"file://{authority}{path}{anchor}";
            }
            else if (separatorIndex < 0 && uncPath.Length > 0)
            {
                string authority = EncodeUriAuthority(uncPath, preserveEscapes: false);
                return $"file://{authority}/{anchor}";
            }
        }

        string encodedPath = EncodeUriPath(normalizedPath, preserveEscapes: false);
        return normalizedPath.StartsWith('/')
            ? $"file://{encodedPath}{anchor}"
            : $"file:///{encodedPath}{anchor}";
    }

    private static string BuildExistingFileUri(string rawUri, int? lineNumber)
    {
        int uriStart = "file://".Length;
        string remainder = rawUri[uriStart..];

        int fragmentIndex = remainder.IndexOf('#');
        string fragment = fragmentIndex >= 0 ? remainder[fragmentIndex..] : string.Empty;
        string withoutFragment = fragmentIndex >= 0 ? remainder[..fragmentIndex] : remainder;

        int queryIndex = withoutFragment.IndexOf('?');
        string query = queryIndex >= 0 ? withoutFragment[queryIndex..] : string.Empty;
        string withoutQuery = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;

        string authority;
        string path;
        if (withoutQuery.StartsWith("/", StringComparison.Ordinal))
        {
            authority = string.Empty;
            path = withoutQuery;
        }
        else
        {
            int separatorIndex = withoutQuery.IndexOf('/');
            if (separatorIndex < 0)
            {
                authority = withoutQuery;
                path = string.Empty;
            }
            else
            {
                authority = withoutQuery[..separatorIndex];
                path = withoutQuery[separatorIndex..];
            }
        }

        string encodedAuthority = EncodeUriAuthority(authority, preserveEscapes: true);
        string encodedPath = EncodeUriPath(path.Replace('\\', '/'), preserveEscapes: true);
        string lineAnchor = fragment.Length == 0 && lineNumber.HasValue ? $"#L{lineNumber.Value}" : string.Empty;
        return $"file://{encodedAuthority}{encodedPath}{query}{fragment}{lineAnchor}";
    }

    private static string EncodeUriAuthority(string authority, bool preserveEscapes) =>
        PercentEncode(authority, IsUriAuthorityCharacter, preserveEscapes, preservePathSeparators: false);

    private static string EncodeUriPath(string path, bool preserveEscapes) =>
        PercentEncode(path, IsUriPathCharacter, preserveEscapes, preservePathSeparators: true);

    private static string PercentEncode(
        string value,
        Func<char, bool> isAllowed,
        bool preserveEscapes,
        bool preservePathSeparators)
    {
        var builder = new StringBuilder(value.Length);
        Span<byte> utf8Bytes = stackalloc byte[4];
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (preservePathSeparators && character == '/')
            {
                builder.Append('/');
                continue;
            }

            if (isAllowed(character))
            {
                builder.Append(character);
                continue;
            }

            if (preserveEscapes && character == '%' && i + 2 < value.Length &&
                IsHexDigit(value[i + 1]) && IsHexDigit(value[i + 2]))
            {
                builder.Append('%');
                builder.Append(char.ToUpperInvariant(value[i + 1]));
                builder.Append(char.ToUpperInvariant(value[i + 2]));
                i += 2;
                continue;
            }

            int characterLength = char.IsHighSurrogate(character) && i + 1 < value.Length &&
                                  char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            int written = Encoding.UTF8.GetBytes(value.AsSpan(i, characterLength), utf8Bytes);
            foreach (byte utf8Byte in utf8Bytes[..written])
            {
                builder.Append('%');
                builder.Append(GetHexDigit(utf8Byte >> 4));
                builder.Append(GetHexDigit(utf8Byte & 0x0F));
            }
            i += characterLength - 1;
        }

        return builder.ToString();
    }

    private static bool IsUriAuthorityCharacter(char character) =>
        IsUnreserved(character) || "!$&'()*+,;=".Contains(character, StringComparison.Ordinal);

    private static bool IsUriPathCharacter(char character) =>
        IsUnreserved(character) || "!$&'()*+,;=:@".Contains(character, StringComparison.Ordinal);

    private static bool IsUnreserved(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~';

    private static bool IsHexDigit(char character) =>
        char.IsAsciiDigit(character) || character is >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static char GetHexDigit(int value) =>
        (char)(value < 10 ? '0' + value : 'A' + value - 10);
}
