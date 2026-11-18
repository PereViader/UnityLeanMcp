using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityLeanMcp.Mcp;

/// <summary>
/// Reads the argument evidence exposed by the host operating system for an
/// already-running process. A failure is deliberately indistinguishable from
/// missing evidence: command-line inspection is an ownership proof only when
/// it succeeds and explicitly targets the requested project.
/// </summary>
internal static class UnityProcessCommandLineReader
{
    private const int MacCtlKernel = 1;
    private const int MacKernProcArgs2 = 49;
    private const int WindowsProcessCommandLineInformation = 60;
    private const int WindowsStatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint WindowsProcessQueryInformation = 0x0400;
    private const uint WindowsProcessQueryLimitedInformation = 0x1000;
    private const uint WindowsProcessVirtualMemoryRead = 0x0010;

    internal static bool TryRead(int processId, out string commandLine)
    {
        commandLine = string.Empty;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TryReadWindows(processId, out commandLine);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return TryReadMacOs(processId, out commandLine);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return TryReadLinux(processId, out commandLine);
            }
        }
        catch
        {
            // Process metadata is best-effort and commonly unavailable for
            // processes owned by another user or protected by the OS.
        }

        return false;
    }

    internal static bool CommandLineTargetsProject(
        string commandLine,
        string projectRoot,
        int? processId = null,
        Func<int, string?>? processWorkingDirectoryProvider = null)
    {
        if (!TryExtractProjectPathArgument(commandLine, out string? projectPathArg) ||
            string.IsNullOrWhiteSpace(projectPathArg))
        {
            return false;
        }

        // Only an explicit project argument proves intent. Relative arguments
        // belong to the Editor's working directory, never this host's directory.
        if (Path.IsPathFullyQualified(projectPathArg))
        {
            return FileUnityProcessIdentityStore.PathsEqual(projectPathArg, projectRoot);
        }

        if (processId.HasValue && (processWorkingDirectoryProvider != null || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)))
        {
            string? procCwd = TryGetProcessWorkingDirectory(processId.Value, processWorkingDirectoryProvider);
            if (!string.IsNullOrWhiteSpace(procCwd) && Path.IsPathFullyQualified(procCwd))
            {
                try
                {
                    string combined = Path.GetFullPath(Path.Combine(procCwd, projectPathArg));
                    return FileUnityProcessIdentityStore.PathsEqual(combined, projectRoot);
                }
                catch { }
            }
        }

        return false;
    }

    internal static bool TryExtractProjectPathArgument(string commandLine, out string? projectPath)
    {
        projectPath = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        List<string> tokens = ParseCommandLineTokens(commandLine);

        for (int i = 0; i < tokens.Count; i++)
        {
            string part = tokens[i].Trim('"', '\'');
            if (part.Equals("-projectPath", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count)
                {
                    projectPath = tokens[i + 1].Trim('"', '\'');
                    return !string.IsNullOrWhiteSpace(projectPath);
                }
            }
            else if (part.StartsWith("-projectPath=", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = part.Substring("-projectPath=".Length).Trim('"', '\'');
                return !string.IsNullOrWhiteSpace(projectPath);
            }
        }

        return false;
    }

    internal static List<string> ParseCommandLineTokens(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return tokens;
        }

        if (commandLine.Contains('\0'))
        {
            string[] rawParts = commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rawParts.Length > 1)
            {
                tokens.AddRange(rawParts);
                return tokens;
            }

            if (rawParts.Length == 1)
            {
                return TokenizeSpaceSeparated(rawParts[0]);
            }

            return tokens;
        }

        return TokenizeSpaceSeparated(commandLine);
    }

    private static List<string> TokenizeSpaceSeparated(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == '"' || c == '\'') && !inQuotes)
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (inQuotes && c == quoteChar)
            {
                inQuotes = false;
                quoteChar = '\0';
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    internal static string? TryGetProcessWorkingDirectory(
        int pid,
        Func<int, string?>? processWorkingDirectoryProvider = null)
    {
        if (processWorkingDirectoryProvider != null)
        {
            try
            {
                string? cwd = processWorkingDirectoryProvider(pid);
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    return FileUnityProcessIdentityStore.ResolveRealPath(cwd);
                }
            }
            catch { }

            return null;
        }

        try
        {
            string cwdLink = $"/proc/{pid}/cwd";
            if (Directory.Exists(cwdLink))
            {
                return FileUnityProcessIdentityStore.ResolveRealPath(cwdLink);
            }
        }
        catch { }

        return null;
    }

    internal static bool TryReadLinux(int processId, out string commandLine)
    {
        commandLine = string.Empty;
        string path = $"/proc/{processId}/cmdline";
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return false;
            }

            commandLine = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrEmpty(commandLine);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadMacOs(int processId, out string commandLine)
    {
        commandLine = string.Empty;
        int[] mib = { MacCtlKernel, MacKernProcArgs2, processId };
        nuint length = 0;

        if (Sysctl(mib, (uint)mib.Length, IntPtr.Zero, ref length, IntPtr.Zero, 0) != 0 ||
            length == 0 || length > int.MaxValue)
        {
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            nuint actualLength = length;
            if (Sysctl(mib, (uint)mib.Length, buffer, ref actualLength, IntPtr.Zero, 0) != 0 ||
                actualLength < sizeof(int) || actualLength > length || actualLength > (nuint)int.MaxValue)
            {
                return false;
            }

            int argc = Marshal.ReadInt32(buffer);
            if (argc <= 0)
            {
                return false;
            }

            byte[] bytes = new byte[(int)actualLength - sizeof(int)];
            Marshal.Copy(IntPtr.Add(buffer, sizeof(int)), bytes, 0, bytes.Length);
            return TryBuildMacOsCommandLine(bytes, argc, out commandLine);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool TryBuildMacOsCommandLine(
        byte[] bytes,
        int argc,
        out string commandLine)
    {
        commandLine = string.Empty;
        if (argc <= 0)
        {
            return false;
        }

        int offset = 0;
        // KERN_PROCARGS2 layout:
        // [argc (int)] [exec_path\0] [null-pad] [argv[0]\0] ... [argv[argc-1]\0] [envp[0]\0] ...
        string? executable = ReadNullTerminatedUtf8(bytes, ref offset);
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        // Skip null padding between exec_path and argv[0]
        while (offset < bytes.Length && bytes[offset] == 0)
        {
            offset++;
        }

        var arguments = new StringBuilder();
        int parsedArgs = 0;

        // Read all argc arguments from argv[0] through argv[argc-1]
        for (int index = 0; index < argc; index++)
        {
            while (offset < bytes.Length && bytes[offset] == 0)
            {
                offset++;
            }

            string? argument = ReadNullTerminatedUtf8(bytes, ref offset);
            if (argument == null)
            {
                break;
            }

            if (arguments.Length > 0)
            {
                arguments.Append('\0');
            }

            arguments.Append(argument);
            parsedArgs++;
        }

        // Fallback: if no argv entries were available (e.g. truncated buffer),
        // use the executable path from the header.
        if (parsedArgs == 0)
        {
            commandLine = executable;
            return true;
        }

        commandLine = arguments.ToString();
        return true;
    }

    private static string? ReadNullTerminatedUtf8(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
        {
            return null;
        }

        int start = offset;
        while (offset < bytes.Length && bytes[offset] != 0)
        {
            offset++;
        }

        return Encoding.UTF8.GetString(bytes, start, offset - start);
    }

    private static bool TryReadWindows(int processId, out string commandLine)
    {
        commandLine = string.Empty;
        IntPtr processHandle = OpenProcess(
            WindowsProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int bufferSize = 4096;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    int status = NtQueryInformationProcess(
                        processHandle,
                        WindowsProcessCommandLineInformation,
                        buffer,
                        bufferSize,
                        out int requiredLength);
                    if (status == WindowsStatusInfoLengthMismatch && requiredLength > bufferSize)
                    {
                        bufferSize = requiredLength;
                        continue;
                    }

                    if (status < 0)
                    {
                        return false;
                    }

                    var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                    if (value.Length == 0 || value.Buffer == IntPtr.Zero)
                    {
                        return false;
                    }

                    byte[] bytes = new byte[value.Length];
                    IntPtr bufferStart = buffer;
                    long bufferAddress = bufferStart.ToInt64();
                    long bufferEnd = bufferAddress + bufferSize;
                    long stringAddress = value.Buffer.ToInt64();
                    if (stringAddress >= bufferAddress && stringAddress <= bufferEnd - value.Length)
                    {
                        Marshal.Copy(value.Buffer, bytes, 0, bytes.Length);
                    }
                    else if (!TryReadProcessMemory(processId, value.Buffer, bytes))
                    {
                        return false;
                    }

                    commandLine = Encoding.Unicode.GetString(bytes);
                    return !string.IsNullOrEmpty(commandLine);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }

        return false;
    }

    private static bool TryReadProcessMemory(int processId, IntPtr remoteAddress, byte[] bytes)
    {
        IntPtr memoryHandle = OpenProcess(
            WindowsProcessQueryInformation | WindowsProcessVirtualMemoryRead,
            false,
            processId);
        if (memoryHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return ReadProcessMemory(
                       memoryHandle,
                       remoteAddress,
                       bytes,
                       bytes.Length,
                       out IntPtr bytesRead) &&
                   bytesRead.ToInt64() == bytes.Length;
        }
        finally
        {
            CloseHandle(memoryHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static extern int Sysctl(
        int[] name,
        uint nameLength,
        IntPtr oldValue,
        ref nuint oldValueLength,
        IntPtr newValue,
        nuint newValueLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
