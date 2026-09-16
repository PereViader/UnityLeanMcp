using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityLeanMcp
{
    [InitializeOnLoad]
    internal static class CommandHelper
    {
        // Capture this once on Unity's main thread. User execute/eval code can
        // change Environment.CurrentDirectory, which must not redirect protocol
        // files to an arbitrary directory.
        private static string s_ProjectRoot;

        internal static string ProjectRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_ProjectRoot))
                {
                    EnsureInitialized();
                }
                return s_ProjectRoot;
            }
        }

        internal static void EnsureInitialized()
        {
            if (string.IsNullOrEmpty(s_ProjectRoot))
            {
                try
                {
                    s_ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"UnityLeanMcp: Failed to initialize ProjectRoot: {ex}");
                }
            }
        }

        public static void RunActionAfterStoppingPlaymode(Action action)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("UnityLeanMcp: Stopping PlayMode before executing command...");
                EditorApplication.isPlaying = false;

                EditorApplication.CallbackFunction checkPlaymode = null;
                checkPlaymode = () =>
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.update -= checkPlaymode;
                        Debug.Log("UnityLeanMcp: PlayMode stopped. Executing command...");
                        try
                        {
                            action();
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                };
                EditorApplication.update += checkPlaymode;
            }
            else
            {
                action();
            }
        }

        // Shared protocol helpers
        public static string FormatResult(object result, bool isVoidStatement = false, bool prettyPrint = true) =>
            UnityResultFormatter.FormatResult(result, isVoidStatement, prettyPrint);

        public static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

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
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
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

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
