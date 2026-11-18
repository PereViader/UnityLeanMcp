#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityLeanMcp
{
    /// <summary>
    /// Computes deterministic SHA-256 fingerprints over Unity compilation inputs,
    /// assembly graph metadata, compiler options, and immutable package caches.
    /// </summary>
    internal static class CompilationInputIdentityCalculator
    {
        private struct CachedFileDigest
        {
            public long Length;
            public long LastWriteUtcTicks;
            public string Digest;
        }

        private static readonly Dictionary<string, CachedFileDigest> s_ImmutablePackageFileDigestCache =
            new Dictionary<string, CachedFileDigest>(GetCoverageHandler.PathComparer);

        internal static string ComputeCompilationInputIdentity() =>
            ComputeCompilationInputIdentity(out _);

        internal static string ComputeCompilationInputIdentity(out string diskFilesIdentity)
        {
            string projectRoot = Path.GetFullPath(UnityLeanMcpPaths.ProjectRoot);
            var manifest = new StringBuilder();
            var diskManifest = new StringBuilder();
            var diskPaths = CollectCompilationInputFiles(projectRoot);
            var contentPaths = new SortedSet<string>(diskPaths, GetCoverageHandler.PathComparer);

            AppendIdentity(manifest, "unity", Application.unityVersion);
            AppendIdentity(manifest, "optimization", CompilationPipeline.codeOptimization.ToString());

            var assemblies = CompilationPipeline.GetAssemblies() ?? new UnityEditor.Compilation.Assembly[0];
            foreach (var assembly in assemblies.OrderBy(a => a?.name, StringComparer.Ordinal))
            {
                if (assembly == null) continue;
                AppendIdentity(manifest, "assembly", assembly.name);
                AppendIdentity(manifest, "flags", assembly.flags);
                AppendIdentity(manifest, "defines", string.Join("\u001f", assembly.defines.OrderBy(d => d, StringComparer.Ordinal)));

                foreach (string source in assembly.sourceFiles.OrderBy(p => p, GetCoverageHandler.PathComparer))
                {
                    string path = ResolveCompilerPath(source, projectRoot);
                    AppendIdentity(manifest, "source", path);
                    contentPaths.Add(path);
                }

                foreach (var reference in assembly.assemblyReferences.OrderBy(r => r.name, StringComparer.Ordinal))
                {
                    AppendIdentity(manifest, "assembly-reference", reference.name);
                }

                foreach (string reference in assembly.compiledAssemblyReferences.OrderBy(p => p, GetCoverageHandler.PathComparer))
                {
                    AppendIdentity(manifest, "compiled-reference", NormalizeCompilerPath(reference));
                }

                var compilerOptions = assembly.compilerOptions;
                AppendIdentity(manifest, "compiler-allow-unsafe", compilerOptions.AllowUnsafeCode);
                AppendIdentity(manifest, "compiler-api-compatibility", compilerOptions.ApiCompatibilityLevel);
                AppendIdentity(manifest, "compiler-optimization", compilerOptions.CodeOptimization);
                AppendIdentity(manifest, "compiler-language-version", compilerOptions.LanguageVersion);
                AppendIdentity(manifest, "compiler-arguments", string.Join("\u001f", compilerOptions.AdditionalCompilerArguments ?? new string[0]));
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-response", compilerOptions.ResponseFiles, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-config", new[] { compilerOptions.AnalyzerConfigPath }, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-additional", compilerOptions.RoslynAdditionalFilePaths, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-dll", compilerOptions.RoslynAnalyzerDllPaths, projectRoot);
                AddCompilerOptionFiles(manifest, contentPaths, "compiler-analyzer-rules", new[] { compilerOptions.RoslynAnalyzerRulesetPath }, projectRoot);
            }

            string[] userPrecompiled = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly) ?? new string[0];
            foreach (string path in userPrecompiled.Select(p => ResolveCompilerPath(p, projectRoot)).OrderBy(p => p, GetCoverageHandler.PathComparer))
            {
                AppendIdentity(manifest, "user-binary", path);
                contentPaths.Add(path);
            }

            using (var sha = SHA256.Create())
            {
                foreach (string path in contentPaths)
                {
                    string digest = GetFileContentDigest(path, projectRoot, sha);
                    AppendIdentity(manifest, "input-path", path);
                    AppendIdentity(manifest, "input-content", digest);
                    if (diskPaths.Contains(path))
                    {
                        AppendIdentity(diskManifest, "input-path", path);
                        AppendIdentity(diskManifest, "input-content", digest);
                    }
                }

                diskFilesIdentity = ComputeHexDigest(sha, diskManifest.ToString());
                return ComputeHexDigest(sha, manifest.ToString());
            }
        }

        internal static string ComputeCompilationInputFilesIdentity()
        {
            string projectRoot = Path.GetFullPath(UnityLeanMcpPaths.ProjectRoot);
            var manifest = new StringBuilder();
            using (var sha = SHA256.Create())
            {
                foreach (string path in CollectCompilationInputFiles(projectRoot))
                {
                    AppendIdentity(manifest, "input-path", path);
                    AppendIdentity(manifest, "input-content", GetFileContentDigest(path, projectRoot, sha));
                }

                return ComputeHexDigest(sha, manifest.ToString());
            }
        }

        private static string GetFileContentDigest(string path, string projectRoot, SHA256 sha)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return "missing";

            bool cacheable = IsImmutablePackageOrEnginePath(path, projectRoot);
            long length = info.Length;
            long writeTicks = info.LastWriteTimeUtc.Ticks;
            if (cacheable &&
                s_ImmutablePackageFileDigestCache.TryGetValue(path, out var cached) &&
                cached.Length == length &&
                cached.LastWriteUtcTicks == writeTicks)
            {
                return cached.Digest;
            }

            using (var stream = info.OpenRead())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (cacheable)
                {
                    s_ImmutablePackageFileDigestCache[path] = new CachedFileDigest
                    {
                        Length = length,
                        LastWriteUtcTicks = writeTicks,
                        Digest = digest
                    };
                }
                return digest;
            }
        }

        private static string ComputeHexDigest(SHA256 sha, string text) =>
            BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");

        private static bool IsImmutablePackageOrEnginePath(string fullPath, string projectRoot)
        {
            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (IsUnderDirectory(fullPath, packageCacheRoot))
                return true;

            string editorContents = EditorApplication.applicationContentsPath;
            if (!string.IsNullOrWhiteSpace(editorContents))
            {
                try
                {
                    if (IsUnderDirectory(fullPath, Path.GetFullPath(editorContents)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static bool IsUnderDirectory(string candidatePath, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
                return false;
            string normCandidate = candidatePath.Replace('\\', '/').TrimEnd('/');
            string normDir = directoryPath.Replace('\\', '/').TrimEnd('/');
            return normCandidate.Equals(normDir, GetCoverageHandler.PathComparison) ||
                normCandidate.StartsWith(normDir + "/", GetCoverageHandler.PathComparison);
        }

        private static SortedSet<string> CollectCompilationInputFiles(string projectRoot)
        {
            var paths = new SortedSet<string>(GetCoverageHandler.PathComparer);
            var inputDirectories = new SortedSet<string>(GetCoverageHandler.PathComparer)
            {
                Path.Combine(projectRoot, "Assets"),
                Path.Combine(projectRoot, "Packages")
            };

            foreach (UnityPackageInfo package in UnityPackageInfo.GetAllRegisteredPackages() ?? new UnityPackageInfo[0])
            {
                if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath)) continue;
                string resolved = Path.GetFullPath(package.resolvedPath);
                if (IsImmutablePackageOrEnginePath(resolved, projectRoot))
                    continue;
                inputDirectories.Add(resolved);
                AddExistingInput(paths, Path.Combine(resolved, "package.json"));
            }

            foreach (string root in inputDirectories)
            {
                CollectDirectoryInputFiles(root, paths);
            }

            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "EditorSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "ProjectSettings", "PackageManagerSettings.asset"));
            AddExistingInput(paths, Path.Combine(projectRoot, "Packages", "manifest.json"));
            AddExistingInput(paths, Path.Combine(projectRoot, "Packages", "packages-lock.json"));

            var assemblies = CompilationPipeline.GetAssemblies() ?? new UnityEditor.Compilation.Assembly[0];
            foreach (var assembly in assemblies)
            {
                if (assembly == null) continue;
                foreach (string source in assembly.sourceFiles ?? new string[0])
                    paths.Add(ResolveCompilerPath(source, projectRoot));
                var options = assembly.compilerOptions;
                AddPaths(paths, options.ResponseFiles, projectRoot);
                AddPaths(paths, new[] { options.AnalyzerConfigPath }, projectRoot);
                AddPaths(paths, options.RoslynAdditionalFilePaths, projectRoot);
                AddPaths(paths, options.RoslynAnalyzerDllPaths, projectRoot);
                AddPaths(paths, new[] { options.RoslynAnalyzerRulesetPath }, projectRoot);
            }

            string[] userPrecompiled = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly) ?? new string[0];
            foreach (string path in userPrecompiled)
            {
                string resolved = ResolveCompilerPath(path, projectRoot);
                paths.Add(resolved);
                if (File.Exists(resolved + ".meta")) paths.Add(resolved + ".meta");
            }
            return paths;
        }

        private static void CollectDirectoryInputFiles(string root, SortedSet<string> paths)
        {
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (IsCompilationInputFile(file))
                        paths.Add(Path.GetFullPath(file));
                }

                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string childName = Path.GetFileName(child);
                    if (string.IsNullOrEmpty(childName) || childName.StartsWith(".", StringComparison.Ordinal) ||
                        childName.EndsWith("~", StringComparison.Ordinal))
                        continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    pending.Push(child);
                }
            }
        }

        private static bool IsCompilationInputFile(string file)
        {
            string extension = Path.GetExtension(file);
            string fileName = Path.GetFileName(file);
            return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rsp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".additionalfile", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".asmdef.meta", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".asmref.meta", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddPaths(SortedSet<string> paths, string[] values, string projectRoot)
        {
            foreach (string value in values ?? new string[0])
                if (!string.IsNullOrWhiteSpace(value)) paths.Add(ResolveCompilerPath(value, projectRoot));
        }

        private static void AddCompilerOptionFiles(StringBuilder manifest, SortedSet<string> contentPaths, string name, string[] paths, string projectRoot)
        {
            foreach (string path in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string resolved = ResolveCompilerPath(path, projectRoot);
                AppendIdentity(manifest, name, resolved);
                contentPaths.Add(resolved);
            }
        }

        private static string ResolveCompilerPath(string path, string projectRoot)
        {
            string normalized = NormalizeCompilerPath(path);
            // A Windows compiler path may be returned by a host running on
            // another OS in unit tests. Keep its identity without pretending it
            // is a local path.
            if (Path.DirectorySeparatorChar != '\\' && normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
                return normalized;
            if (Path.IsPathRooted(normalized)) return Path.GetFullPath(normalized);
            return Path.GetFullPath(Path.Combine(projectRoot, normalized));
        }

        private static string NormalizeCompilerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Replace('\\', '/');
        }

        private static void AddExistingInput(SortedSet<string> paths, string path)
        {
            if (File.Exists(path)) paths.Add(Path.GetFullPath(path));
        }

        private static void AppendIdentity(StringBuilder manifest, string name, object value)
        {
            manifest.Append(name ?? "").Append('\0').Append(value?.ToString() ?? "").Append('\n');
        }
    }
}
