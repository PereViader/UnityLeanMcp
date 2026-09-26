using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class CoverageFormattingTests
{
    [Fact]
    public void FormatUncoveredSpans_EmptyOrNull_ReturnsEmptyList()
    {
        Assert.Empty(UnityTools.FormatUncoveredSpans(null!));
        Assert.Empty(UnityTools.FormatUncoveredSpans([]));
    }

    [Fact]
    public void FormatUncoveredSpans_SingleLine_ReturnsSingleSpan()
    {
        var spans = UnityTools.FormatUncoveredSpans([42]);
        Assert.Single(spans);
        Assert.Equal(42, spans[0].StartLine);
        Assert.Equal(42, spans[0].EndLine);
    }

    [Fact]
    public void FormatUncoveredSpans_ConsecutiveLines_CollapsedIntoSingleSpan()
    {
        var spans = UnityTools.FormatUncoveredSpans([10, 11, 12, 13]);
        Assert.Single(spans);
        Assert.Equal(10, spans[0].StartLine);
        Assert.Equal(13, spans[0].EndLine);
    }

    [Fact]
    public void FormatUncoveredSpans_MultipleGapsAndDuplicates_ReturnsSortedMergedSpans()
    {
        var spans = UnityTools.FormatUncoveredSpans([5, 1, 2, 3, 10, 10, 11, 20]);
        Assert.Equal(4, spans.Count);

        Assert.Equal(1, spans[0].StartLine);
        Assert.Equal(3, spans[0].EndLine);

        Assert.Equal(5, spans[1].StartLine);
        Assert.Equal(5, spans[1].EndLine);

        Assert.Equal(10, spans[2].StartLine);
        Assert.Equal(11, spans[2].EndLine);

        Assert.Equal(20, spans[3].StartLine);
        Assert.Equal(20, spans[3].EndLine);
    }

    [Fact]
    public async Task UnityCoverage_WhenPathsNull_ReturnsError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var result = await tools.UnityCoverageAsync(null!);
            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("paths", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_WhenPathsEmpty_ReturnsError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var result = await tools.UnityCoverageAsync([]);
            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("paths", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_SingleFileWithUncoveredSpans_FormatsPercentageAndUncoveredSpans()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = true,
                Files =
                [
                    new CoverageFileResult
                    {
                        Path = "Assets/Scripts/PlayerController.cs",
                        TotalPoints = 20,
                        CoveredPoints = 15,
                        UncoveredLines = [5, 6, 7, 12, 25, 26]
                    }
                ]
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/PlayerController.cs"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("• Assets/Scripts/PlayerController.cs: 75.0% (15/20 points)", text);
            Assert.Contains("Uncovered lines: 5-7, 12, 25-26", text);
            Assert.DoesNotContain("file:///", text);
            // Single file output should not produce an aggregate total line
            Assert.DoesNotContain("Total Coverage:", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_MultipleFiles_ComputesAggregateTotalAtTop()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = true,
                Files =
                [
                    new CoverageFileResult
                    {
                        Path = "Assets/Scripts/FileA.cs",
                        TotalPoints = 10,
                        CoveredPoints = 10,
                        UncoveredLines = []
                    },
                    new CoverageFileResult
                    {
                        Path = "Assets/Scripts/FileB.cs",
                        TotalPoints = 10,
                        CoveredPoints = 5,
                        UncoveredLines = [1, 2]
                    }
                ]
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.StartsWith("Total Coverage: 75.0% (15/20 points across 2 files)", text);
            Assert.Contains("• Assets/Scripts/FileA.cs: 100.0% (10/10 points)", text);
            Assert.Contains("• Assets/Scripts/FileB.cs: 50.0% (5/10 points)", text);
            Assert.Contains("Uncovered lines: 1-2", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_ZeroSequencePoints_ReportsZeroPointsMessage()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = true,
                Files =
                [
                    new CoverageFileResult
                    {
                        Path = "Assets/Scripts/IWeapon.cs",
                        TotalPoints = 0,
                        CoveredPoints = 0,
                        UncoveredLines = []
                    }
                ]
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/IWeapon.cs"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("• Assets/Scripts/IWeapon.cs: No executable sequence points found", text);
            Assert.DoesNotContain("%", text);
            Assert.DoesNotContain("Uncovered:", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_NoResults_ReportsNoCompiledScriptsFound()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = true,
                Files = []
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/Empty/"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.Equal("No compiled C# scripts found in the specified path(s).", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_WhenPathDoesNotExist_ReturnsPathDoesNotExistError()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = false,
                Message = "Path does not exist: 'Assets/Scripts/NonExistent.cs'."
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/NonExistent.cs"]);
            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Equal("Path does not exist: 'Assets/Scripts/NonExistent.cs'.", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_FailureFromUnity_ReturnsErrorResult()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = false,
                Message = "Unity script optimization is set to Release mode. Switch Unity to Debug mode before running tests with coverage."
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/Player.cs"]);
            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("Release mode", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenSuccessResponse_ParsesFileResults()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("GET_COVERAGE"))
            {
                var payload = new
                {
                    files = new[]
                    {
                        new
                        {
                            path = "Assets/Scripts/Foo.cs",
                            totalPoints = 12,
                            coveredPoints = 9,
                            uncoveredLines = new[] { 5, 6, 7 }
                        }
                    }
                };
                string json = JsonSerializer.Serialize(payload);
                return $"SUCCESS {ProtocolCodec.EscapeLine(json)}";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/Foo.cs"], cts.Token);
        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Equal("Assets/Scripts/Foo.cs", result.Files[0].Path);
        Assert.Equal(12, result.Files[0].TotalPoints);
        Assert.Equal(9, result.Files[0].CoveredPoints);
        Assert.Equal([5, 6, 7], result.Files[0].UncoveredLines);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenFailureResponse_ReturnsErrorResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("GET_COVERAGE"))
            {
                return "FAILURE Cannot query coverage: script compilation failed";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/Foo.cs"], cts.Token);
        Assert.False(result.Success);
        Assert.Equal("Cannot query coverage: script compilation failed", result.Message);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenErrorPrefix_StripsPrefix()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("GET_COVERAGE"))
            {
                return "ERROR: Missing paths parameter";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/Foo.cs"], cts.Token);
        Assert.False(result.Success);
        Assert.Equal("Missing paths parameter", result.Message);
    }

    [Fact]
    public async Task UnityTest_ForwardsCoverageFlag()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.TestRunResultToReturn = new UnityTestRunResult
            {
                Success = true,
                PassCount = 5
            };

            var result = await tools.UnityTestAsync(mode: UnityTestMode.EditMode, coverage: true);
            Assert.False(result.IsError);
            Assert.True(client.LastCoverage);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static (string tempDir, FakeUnityProcessManager pm, FakeUnityClient client, UnityTools tools) CreateTestContext()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_cov_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));

        var pm = new FakeUnityProcessManager(tempDir);
        var client = new FakeUnityClient(pm);
        var tools = new UnityTools(client, pm);
        return (tempDir, pm, client, tools);
    }

    private static string GetResultText(CallToolResult result)
    {
        Assert.NotNull(result);
        Assert.NotEmpty(result.Content);
        var textBlock = result.Content[0] as TextContentBlock;
        Assert.NotNull(textBlock);
        return textBlock.Text;
    }

    private sealed class FakeUnityProcessManager : UnityProcessManager
    {
        public FakeUnityProcessManager(string projectRoot)
            : base(new UnityPathResolver(projectRoot), NullLogger<UnityProcessManager>.Instance)
        {
        }

        public override bool IsUnityRunning(out int? processId)
        {
            processId = 1234;
            return true;
        }

        public override string GetUnityMode(int? pid = null) => "Batchmode";
    }

    private sealed class FakeUnityClient : UnityClient
    {
        public CoverageResult CoverageResultToReturn { get; set; } = new() { Success = true };
        public UnityTestRunResult TestRunResultToReturn { get; set; } = new();
        public bool LastCoverage { get; private set; }

        public FakeUnityClient(UnityProcessManager pm)
            : base(pm, pm.PathResolver, NullLogger<UnityClient>.Instance)
        {
        }

        public override Task<UnityTestRunResult> RunTestsAsync(
            string[]? testNames,
            string[]? groupNames,
            string[]? categoryNames,
            string[]? assemblyNames,
            string? mode,
            bool failedOnly = false,
            bool coverage = false,
            IProgress<ModelContextProtocol.ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastCoverage = coverage;
            return Task.FromResult(TestRunResultToReturn);
        }

        public override Task<CoverageResult> GetCoverageAsync(
            string[] paths,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoverageResultToReturn);
        }
    }
}
