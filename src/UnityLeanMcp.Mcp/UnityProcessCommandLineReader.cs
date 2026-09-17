using System;
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

    private static bool TryReadLinux(int processId, out string commandLine)
    {
        string path = $"/proc/{processId}/cmdline";
        if (!File.Exists(path))
        {
            commandLine = string.Empty;
            return false;
        }

        commandLine = File.ReadAllText(path);
        return !string.IsNullOrEmpty(commandLine);
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
            WindowsProcessQueryInformation |
            WindowsProcessQueryLimitedInformation |
            WindowsProcessVirtualMemoryRead,
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
                    else if (!ReadProcessMemory(
                                 processHandle,
                                 value.Buffer,
                                 bytes,
                                 bytes.Length,
                                 out IntPtr bytesRead) ||
                             bytesRead.ToInt64() != bytes.Length)
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
