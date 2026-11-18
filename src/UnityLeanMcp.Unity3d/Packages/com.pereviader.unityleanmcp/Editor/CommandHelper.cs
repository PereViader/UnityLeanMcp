using System;
using System.IO;
using System.Threading;

namespace UnityLeanMcp
{
    internal static class CommandHelper
    {
        // Shared protocol helpers
        public static string FormatResult(object result, bool isVoidStatement = false, bool prettyPrint = true) =>
            UnityResultFormatter.FormatResult(result, isVoidStatement, prettyPrint);

        public static bool IsAssetImportWorkerProcess()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (IsAssetImportWorkerName(args[i]))
                {
                    return true;
                }

                if (string.Equals(args[i], "-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && IsAssetImportWorkerName(args[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssetImportWorkerName(string value)
        {
            return string.Equals(value, "AssetImport", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("AssetImportWorker", StringComparison.OrdinalIgnoreCase);
        }

        public static string ReadFileWithRetry(string path, int maxRetries = 5, int delayMs = 10)
        {
            return WorkerThreadSnapshots.ReadFileWithRetry(path, maxRetries, delayMs);
        }

        public static bool DeleteFileWithRetry(string path, int maxRetries = 5, int delayMs = 10)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return true;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Delete(path);
                    return true;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
