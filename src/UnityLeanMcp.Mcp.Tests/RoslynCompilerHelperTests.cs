using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
public sealed class RoslynCompilerHelperTests
{
    [Fact]
    public void RoslynCompilerHelper_GetOrCreateMetadataReference_CachesByPathAndTimestamp()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "roslyn_ref_cache_" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(tempFile, "v1");
        try
        {
            UnityLeanMcp.RoslynCompilerHelper.ResetState();
            int createCalls = 0;

            object? first = UnityLeanMcp.RoslynCompilerHelper.GetOrCreateMetadataReference(tempFile, _ =>
            {
                createCalls++;
                return new object();
            });
            object? second = UnityLeanMcp.RoslynCompilerHelper.GetOrCreateMetadataReference(tempFile, _ =>
            {
                createCalls++;
                return new object();
            });

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, createCalls);
            Assert.True(UnityLeanMcp.RoslynCompilerHelper.CachedMetadataReferenceCount >= 1);

            File.SetLastWriteTimeUtc(tempFile, File.GetLastWriteTimeUtc(tempFile).AddMinutes(1));
            object? third = UnityLeanMcp.RoslynCompilerHelper.GetOrCreateMetadataReference(tempFile, _ =>
            {
                createCalls++;
                return new object();
            });

            Assert.NotNull(third);
            Assert.NotSame(first, third);
            Assert.Equal(2, createCalls);

            UnityLeanMcp.RoslynCompilerHelper.ResetState();
            Assert.Equal(0, UnityLeanMcp.RoslynCompilerHelper.CachedMetadataReferenceCount);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void EvalHandler_ClearCache_ResetsCachedSnippetCount()
    {
        UnityLeanMcp.EvalHandler.ClearCache();
        Assert.Equal(0, UnityLeanMcp.EvalHandler.CachedSnippetCount);
    }

    [Fact]
    public async Task RoslynCompilerHelper_EnsureInitialized_RetriesAfterFailureAndSerializesConcurrentCallers()
    {
        string originalContentsPath = UnityEditor.EditorApplication.applicationContentsPath;
        string missingContentsPath = Path.Combine(Path.GetTempPath(), "unity_roslyn_missing_" + Guid.NewGuid().ToString("N"));
        string existingContentsPath = Path.Combine(Path.GetTempPath(), "unity_roslyn_existing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existingContentsPath);

        try
        {
            UnityEditor.EditorApplication.applicationContentsPath = missingContentsPath;
            UnityLeanMcp.RoslynCompilerHelper.EnsureInitialized();
            Assert.Contains("invalid or does not exist", UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason);

            UnityEditor.EditorApplication.applicationContentsPath = existingContentsPath;
            var callers = new Task[8];
            for (int i = 0; i < callers.Length; i++)
            {
                callers[i] = Task.Run(() => UnityLeanMcp.RoslynCompilerHelper.EnsureInitialized());
            }

            await Task.WhenAll(callers);

            Assert.False(UnityLeanMcp.RoslynCompilerHelper.IsSupported);
            Assert.Contains("could not be found or loaded", UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason);
        }
        finally
        {
            UnityEditor.EditorApplication.applicationContentsPath = originalContentsPath;
            try { Directory.Delete(existingContentsPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoslynCompilerHelper_GetSupportStatus_EvaluatesAtomically()
    {
        var status = UnityLeanMcp.RoslynCompilerHelper.GetSupportStatus();
        Assert.Equal(UnityLeanMcp.RoslynCompilerHelper.IsSupported, status.IsSupported);
        Assert.Equal(UnityLeanMcp.RoslynCompilerHelper.UnsupportedReason, status.UnsupportedReason ?? "");
    }

    [Fact]
    public void RoslynCompilerHelper_HasTopLevelValueReturn_WhenNullOrEmpty_ReturnsFalse()
    {
        Assert.False(UnityLeanMcp.RoslynCompilerHelper.HasTopLevelValueReturn(null!));
        Assert.False(UnityLeanMcp.RoslynCompilerHelper.HasTopLevelValueReturn(""));
        Assert.False(UnityLeanMcp.RoslynCompilerHelper.HasTopLevelValueReturn("   ", alreadyCleaned: true));
    }

    [Fact]
    public void RoslynCompilerHelper_ResetState_ClearsInitializedStateCleanly()
    {
        UnityLeanMcp.RoslynCompilerHelper.ResetState();
        var status = UnityLeanMcp.RoslynCompilerHelper.GetSupportStatus();
        Assert.NotNull(status.UnsupportedReason);
    }

    [Fact]
    public void RoslynCompilerHelper_GetCandidateDirectories_ReturnsLegacyAndUnity60003Paths()
    {
        string fakeDataPath = Path.Combine("fake", "Contents");
        var candidates = UnityLeanMcp.RoslynCompilerHelper.GetCandidateDirectories(fakeDataPath);

        Assert.NotNull(candidates);
        Assert.NotEmpty(candidates);

        // Legacy paths
        Assert.Contains(Path.Combine(fakeDataPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "DotNetSdkRoslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Tools", "Roslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "MonoBleedingEdge", "lib", "mono", "4.5"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Frameworks", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"), candidates);

        // Unity 6000.3+ paths under Resources/Scripting
        Assert.Contains(Path.Combine(fakeDataPath, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Resources", "Scripting", "DotNetSdkRoslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Resources", "Scripting", "Tools", "Roslyn"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "4.5"), candidates);
        Assert.Contains(Path.Combine(fakeDataPath, "Resources", "Scripting", "Frameworks", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn"), candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RoslynCompilerHelper_GetCandidateDirectories_WhenNullOrEmpty_ReturnsEmpty(string? dataPath)
    {
        var candidates = UnityLeanMcp.RoslynCompilerHelper.GetCandidateDirectories(dataPath!);
        Assert.NotNull(candidates);
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvalHandler_BuildSource_InjectsBaselineUsingsAndPreservesLineDirective(bool isVoid)
    {
        string body = isVoid ? "Debug.Log(1);" : "return 42;";
        string source = UnityLeanMcp.EvalHandler.BuildSource(body, isVoid);

        Assert.Contains("using System;", source);
        Assert.Contains("using System.Collections.Generic;", source);
        Assert.Contains("using System.Linq;", source);
        Assert.Contains("using UnityEngine;", source);
        Assert.Contains("using UnityEditor;", source);
        Assert.Contains("using UnityLeanMcp;", source);
        Assert.DoesNotContain("using UnityEngine.UI;", source);

        string expectedReturnType = isVoid
            ? "public static async System.Threading.Tasks.Task Execute(System.Threading.CancellationToken cancellationToken)"
            : "public static async System.Threading.Tasks.Task<object> Execute(System.Threading.CancellationToken cancellationToken)";
        Assert.Contains(expectedReturnType, source);

        string normalizedSource = source.Replace("\r\n", "\n");
        Assert.Contains("#line 1 \"eval\"\n" + body + "\n", normalizedSource);
    }

    [Fact]
    public void EvalHandler_BuildSource_DeduplicatesUserUsingsAndEmitsUserDirectivesFirst()
    {
        var userUsings = new System.Collections.Generic.List<string>
        {
            "global using System;",
            "using   System . Linq ;",
            "using global::UnityEngine;",
            "using /* inline */ UnityLeanMcp;",
            "using @System.@Collections.@Generic;",
            "using System.IO;",
            "using Object = UnityEngine.Object;",
            "using static UnityEngine.Mathf;"
        };

        string source = UnityLeanMcp.EvalHandler.BuildSource("return 1;", isVoid: false, userUsings);
        string normalizedSource = source.Replace("\r\n", "\n");

        Assert.Equal(1, CountOccurrences(normalizedSource, "System;"));
        Assert.Equal(1, CountOccurrences(normalizedSource, "Linq"));
        Assert.Equal(1, CountOccurrences(normalizedSource, "UnityEngine;"));
        Assert.Equal(1, CountOccurrences(normalizedSource, "UnityLeanMcp;"));
        Assert.Equal(1, CountOccurrences(normalizedSource, "Generic;"));
        Assert.Contains("using UnityEditor;\n", normalizedSource);
        Assert.Contains("using System.IO;\n", normalizedSource);
        Assert.Contains("using Object = UnityEngine.Object;\n", normalizedSource);
        Assert.Contains("using static UnityEngine.Mathf;\n", normalizedSource);

        int lastUserIdx = normalizedSource.IndexOf("using static UnityEngine.Mathf;", StringComparison.Ordinal);
        int defaultEditorIdx = normalizedSource.IndexOf("using UnityEditor;", StringComparison.Ordinal);
        Assert.True(lastUserIdx >= 0 && defaultEditorIdx > lastUserIdx);
    }

    [Theory]
    [InlineData("using System;", true, "System")]
    [InlineData("using   System . Collections . Generic ;", true, "System.Collections.Generic")]
    [InlineData("using\n// comment\nSystem.Linq;", true, "System.Linq")]
    [InlineData("using /* c */ global::UnityEngine;", true, "UnityEngine")]
    [InlineData("global using UnityEditor;", true, "UnityEditor")]
    [InlineData("using @System;", true, "System")]
    [InlineData("using staticNamespace.Foo;", true, "staticNamespace.Foo")]
    [InlineData("using globalNamespace.Bar;", true, "globalNamespace.Bar")]
    [InlineData("using static UnityEngine.Mathf;", false, null)]
    [InlineData("global using static System.Math;", false, null)]
    [InlineData("using Object = UnityEngine.Object;", false, null)]
    [InlineData("using U = UnityEngine;", false, null)]
    [InlineData("using", false, null)]
    [InlineData("", false, null)]
    public void EvalHandler_TryExtractImportedNamespace_ParsesDirectivesAccurately(string directive, bool expectedSuccess, string? expectedNamespace)
    {
        bool actualSuccess = UnityLeanMcp.EvalHandler.TryExtractImportedNamespace(directive, out string? actualNamespace);

        Assert.Equal(expectedSuccess, actualSuccess);
        Assert.Equal(expectedNamespace, actualNamespace);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
