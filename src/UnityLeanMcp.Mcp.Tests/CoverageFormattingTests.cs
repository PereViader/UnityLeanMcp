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
[Collection("CommandGate")]
public class CoverageFormattingTests
{
    [Fact]
    public void FormatUncoveredSpans_EmptyOrNull_ReturnsEmptyList()
    {
        Assert.Empty(UnityToolOutputFormatter.FormatUncoveredSpans(null!));
        Assert.Empty(UnityToolOutputFormatter.FormatUncoveredSpans([]));
    }

    [Fact]
    public void FormatUncoveredSpans_SingleLine_ReturnsSingleSpan()
    {
        var spans = UnityToolOutputFormatter.FormatUncoveredSpans([42]);
        Assert.Single(spans);
        Assert.Equal(42, spans[0].StartLine);
        Assert.Equal(42, spans[0].EndLine);
    }

    [Fact]
    public void FormatUncoveredSpans_ConsecutiveLines_CollapsedIntoSingleSpan()
    {
        var spans = UnityToolOutputFormatter.FormatUncoveredSpans([10, 11, 12, 13]);
        Assert.Single(spans);
        Assert.Equal(10, spans[0].StartLine);
        Assert.Equal(13, spans[0].EndLine);
    }

    [Fact]
    public void FormatUncoveredSpans_MultipleGapsAndDuplicates_ReturnsSortedMergedSpans()
    {
        var spans = UnityToolOutputFormatter.FormatUncoveredSpans([5, 1, 2, 3, 10, 10, 11, 20]);
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
    public void FormatUncoveredMethodSpans_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal("", UnityToolOutputFormatter.FormatUncoveredMethodSpans(null!));
        Assert.Equal("", UnityToolOutputFormatter.FormatUncoveredMethodSpans([]));
    }

    [Fact]
    public void FormatUncoveredMethodSpans_SingleMethodSingleSpan_FormatsCorrectly()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "MovePlayer", StartLine = 42, EndLine = 50 }
        };
        string formatted = UnityToolOutputFormatter.FormatUncoveredMethodSpans(spans);
        Assert.Equal("MovePlayer (42-50)", formatted);
    }

    [Fact]
    public void FormatUncoveredMethodSpans_SingleMethodSingleLine_FormatsWithoutHyphen()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "TakeDamage", StartLine = 15, EndLine = 15 }
        };
        string formatted = UnityToolOutputFormatter.FormatUncoveredMethodSpans(spans);
        Assert.Equal("TakeDamage (15)", formatted);
    }

    [Fact]
    public void FormatUncoveredMethodSpans_SingleMethodMultipleSpans_CombinesIntoSingleParenthesizedGroup()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "MovePlayer", StartLine = 42, EndLine = 45 },
            new() { Method = "MovePlayer", StartLine = 48, EndLine = 50 },
            new() { Method = "MovePlayer", StartLine = 55, EndLine = 55 }
        };
        string formatted = UnityToolOutputFormatter.FormatUncoveredMethodSpans(spans);
        Assert.Equal("MovePlayer (42-45, 48-50, 55)", formatted);
    }

    [Fact]
    public void FormatUncoveredMethodSpans_MultipleMethods_FormatsCommaSeparated()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "MovePlayer", StartLine = 42, EndLine = 50 },
            new() { Method = "ApplyDamage", StartLine = 78, EndLine = 84 },
            new() { Method = "Parry", StartLine = 90, EndLine = 90 }
        };
        string formatted = UnityToolOutputFormatter.FormatUncoveredMethodSpans(spans);
        Assert.Equal("MovePlayer (42-50), ApplyDamage (78-84), Parry (90)", formatted);
    }

    [Fact]
    public void FormatUncoveredMethodSpans_UnnamedMethod_FormatsWithoutMethodName()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "", StartLine = 10, EndLine = 12 },
            new() { Method = "", StartLine = 15, EndLine = 15 }
        };
        string formatted = UnityToolOutputFormatter.FormatUncoveredMethodSpans(spans);
        Assert.Equal("10-12, 15", formatted);
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
    public async Task UnityCoverage_WithUncoveredMethodSpans_FormatsMethodAnnotatedLines()
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
                        TotalPoints = 30,
                        CoveredPoints = 20,
                        UncoveredLines = [5, 6, 7, 25, 26, 30],
                        UncoveredSpans =
                        [
                            new() { Method = "MovePlayer", StartLine = 5, EndLine = 7 },
                            new() { Method = "ApplyDamage", StartLine = 25, EndLine = 26 },
                            new() { Method = "Heal", StartLine = 30, EndLine = 30 }
                        ]
                    }
                ]
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/PlayerController.cs"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);

            Assert.Contains("• Assets/Scripts/PlayerController.cs: 66.7% (20/30 points)", text);
            Assert.Contains("Uncovered lines: MovePlayer (5-7), ApplyDamage (25-26), Heal (30)", text);
            Assert.DoesNotContain("file:///", text);
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
            Assert.Contains("Uncovered lines: 1-2" + Environment.NewLine + "• Assets/Scripts/FileA.cs", text);
            Assert.True(text.IndexOf("FileB.cs", StringComparison.Ordinal) < text.IndexOf("FileA.cs", StringComparison.Ordinal));
            Assert.Equal(2, text.Split(Environment.NewLine).Count(line => line.StartsWith("• ")));
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UnityCoverage_WhenPathsContainNullOrWhitespace_ReturnsError(string? invalidPath)
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            var result = await tools.UnityCoverageAsync(["Assets/Scripts/Player.cs", invalidPath!]);
            Assert.True(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("empty or whitespace-only", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UnityClient_GetCoverageAsync_WhenPathsContainNullOrWhitespace_ReturnsError(string? invalidPath)
    {
        var resolver = new UnityPathResolver(Path.GetTempPath());
        var pm = new UnityProcessManager(resolver, NullLogger<UnityProcessManager>.Instance);
        var client = new UnityClient(pm, resolver, NullLogger<UnityClient>.Instance);

        var result = await client.GetCoverageAsync(["Assets/Scripts/Player.cs", invalidPath!]);
        Assert.False(result.Success);
        Assert.Contains("empty or whitespace-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenEmptyFilesResponse_ReturnsSuccessWithEmptyFiles()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("GET_COVERAGE"))
            {
                var payload = new { files = Array.Empty<object>() };
                string json = JsonSerializer.Serialize(payload);
                return $"SUCCESS {ProtocolCodec.EscapeLine(json)}";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/Empty/"], cts.Token);
        Assert.True(result.Success);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task UnitySocketTransport_SendCommandAsync_WithZeroTimeout_DoesNotCancelAndSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("PING"))
            {
                return "PONG";
            }
            return null;
        });

        var transport = new UnitySocketTransport();
        string? response = await transport.SendCommandAsync(server.Port, server.ProjectRoot, "PING", timeoutSeconds: 0, cts.Token);
        Assert.Equal("PONG", response);
    }

    [Fact]
    public void RunTestsArgs_DefaultMode_IsEmptyString_AndNotObsoleteAll()
    {
        var args = new RunTestsArgs();
        Assert.Equal(string.Empty, args.Mode);
        Assert.NotEqual("all", args.Mode, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_ParsesTypedResult_WithMultipleFilesAndUncoveredSpans()
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
                            path = "Assets/Scripts/Player.cs",
                            totalPoints = 20,
                            coveredPoints = 15,
                            uncoveredLines = new[] { 10, 11, 12, 25 }
                        },
                        new
                        {
                            path = "Assets/Scripts/Enemy.cs",
                            totalPoints = 10,
                            coveredPoints = 10,
                            uncoveredLines = Array.Empty<int>()
                        }
                    }
                };
                string json = JsonSerializer.Serialize(payload);
                return $"SUCCESS {ProtocolCodec.EscapeLine(json)}";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/"], cts.Token);
        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);

        var player = result.Files[0];
        Assert.Equal("Assets/Scripts/Player.cs", player.Path);
        Assert.Equal(20, player.TotalPoints);
        Assert.Equal(15, player.CoveredPoints);
        Assert.Equal(new[] { 10, 11, 12, 25 }, player.UncoveredLines);

        var enemy = result.Files[1];
        Assert.Equal("Assets/Scripts/Enemy.cs", enemy.Path);
        Assert.Equal(10, enemy.TotalPoints);
        Assert.Equal(10, enemy.CoveredPoints);
        Assert.Empty(enemy.UncoveredLines);
    }

    [Fact]
    public async Task UnityClient_GetCoverageAsync_WhenInvalidJsonInSuccess_ReturnsFailureResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockUnityServer.StartAsync(cmd =>
        {
            if (cmd.StartsWith("GET_COVERAGE"))
            {
                return "SUCCESS {not valid json";
            }
            return null;
        });

        var result = await server.Client.GetCoverageAsync(["Assets/Scripts/Foo.cs"], cts.Token);
        Assert.False(result.Success);
        Assert.Contains("Failed to parse coverage response", result.Message);
    }

    [Fact]
    public async Task UnityTools_UnityCoverage_LeadingSlashPath_PassesToClient()
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
                        Path = "Assets/Scripts/Player.cs",
                        TotalPoints = 10,
                        CoveredPoints = 8,
                        UncoveredLines = [2, 3]
                    }
                ]
            };

            var result = await tools.UnityCoverageAsync(["/Assets/Scripts/Player.cs"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("• Assets/Scripts/Player.cs: 80.0% (8/10 points)", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverageAsync_WhenResultFilesNull_HandlesNullSafely()
    {
        var (tempDir, pm, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = new CoverageResult
            {
                Success = true,
                Files = null!
            };

            var result = await tools.UnityCoverageAsync(["Assets/Scripts/Player.cs"]);
            Assert.False(result.IsError);
            string text = GetResultText(result);
            Assert.Contains("No compiled C# scripts found", text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnityCoverage_DefaultSorting_OrdersLowestToHighestWithZeroPointFilesLastAndPreservesNewlines()
    {
        var coverageResult = new CoverageResult
        {
            Success = true,
            Files =
            [
                new CoverageFileResult { Path = "Assets/Scripts/IZeroB.cs", TotalPoints = 0, CoveredPoints = 0 },
                new CoverageFileResult { Path = "Assets/Scripts/FullB.cs", TotalPoints = 10, CoveredPoints = 10 },
                new CoverageFileResult { Path = "Assets/Scripts/Half.cs", TotalPoints = 10, CoveredPoints = 5, UncoveredLines = [1] },
                new CoverageFileResult { Path = "Assets/Scripts/ZeroCovered.cs", TotalPoints = 10, CoveredPoints = 0, UncoveredLines = [1, 2] },
                new CoverageFileResult { Path = "Assets/Scripts/FullA.cs", TotalPoints = 5, CoveredPoints = 5 },
                new CoverageFileResult { Path = "Assets/Scripts/IZeroA.cs", TotalPoints = 0, CoveredPoints = 0 },
                new CoverageFileResult { Path = "Assets/Scripts/Quarter.cs", TotalPoints = 8, CoveredPoints = 2, UncoveredLines = [3] }
            ]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.StartsWith("Total Coverage: 51.2% (22/43 points across 7 files)", text);

        var bulletLines = text.Split(Environment.NewLine).Where(line => line.StartsWith("• ")).ToArray();
        Assert.Equal(7, bulletLines.Length);
        Assert.StartsWith("• Assets/Scripts/ZeroCovered.cs: 0.0%", bulletLines[0]);
        Assert.StartsWith("• Assets/Scripts/Quarter.cs: 25.0%", bulletLines[1]);
        Assert.StartsWith("• Assets/Scripts/Half.cs: 50.0%", bulletLines[2]);
        Assert.StartsWith("• Assets/Scripts/FullA.cs: 100.0%", bulletLines[3]);
        Assert.StartsWith("• Assets/Scripts/FullB.cs: 100.0%", bulletLines[4]);
        Assert.StartsWith("• Assets/Scripts/IZeroA.cs: No executable sequence points found", bulletLines[5]);
        Assert.StartsWith("• Assets/Scripts/IZeroB.cs: No executable sequence points found", bulletLines[6]);
    }

    [Fact]
    public async Task UnityCoverage_TieBreakingAndExactRatios_AreDeterministicAndDoNotMutateInput()
    {
        var fileZeroSmall = new CoverageFileResult { Path = "Assets/Scripts/Alpha.cs", TotalPoints = 5, CoveredPoints = 0, UncoveredLines = [1] };
        var fileZeroLarge = new CoverageFileResult { Path = "Assets/Scripts/Zulu.cs", TotalPoints = 50, CoveredPoints = 0, UncoveredLines = [1] };
        var fileHalfSmall = new CoverageFileResult { Path = "Assets/Scripts/Beta.cs", TotalPoints = 10, CoveredPoints = 5, UncoveredLines = [1] };
        var fileHalfLarge = new CoverageFileResult { Path = "Assets/Scripts/Yankee.cs", TotalPoints = 50, CoveredPoints = 25, UncoveredLines = [1] };
        var fileHalfTieB = new CoverageFileResult { Path = "Assets/Scripts/CharlieB.cs", TotalPoints = 10, CoveredPoints = 5, UncoveredLines = [1] };
        var fileNearFull = new CoverageFileResult { Path = "Assets/Scripts/NearFull.cs", TotalPoints = 10000, CoveredPoints = 9999, UncoveredLines = [100] };
        var fileExactFull = new CoverageFileResult { Path = "Assets/Scripts/ExactFull.cs", TotalPoints = 10, CoveredPoints = 10 };

        var originalOrder = new List<CoverageFileResult>
        {
            fileExactFull,
            fileHalfTieB,
            fileZeroSmall,
            fileNearFull,
            fileHalfSmall,
            fileZeroLarge,
            fileHalfLarge
        };

        var coverageResult = new CoverageResult
        {
            Success = true,
            Files = [.. originalOrder]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        var bulletLines = text.Split(Environment.NewLine).Where(line => line.StartsWith("• ")).ToArray();
        Assert.Equal(7, bulletLines.Length);
        Assert.StartsWith("• Assets/Scripts/Zulu.cs: 0.0% (0/50 points)", bulletLines[0]);
        Assert.StartsWith("• Assets/Scripts/Alpha.cs: 0.0% (0/5 points)", bulletLines[1]);
        Assert.StartsWith("• Assets/Scripts/Yankee.cs: 50.0% (25/50 points)", bulletLines[2]);
        Assert.StartsWith("• Assets/Scripts/Beta.cs: 50.0% (5/10 points)", bulletLines[3]);
        Assert.StartsWith("• Assets/Scripts/CharlieB.cs: 50.0% (5/10 points)", bulletLines[4]);
        Assert.StartsWith("• Assets/Scripts/NearFull.cs: 100.0% (9999/10000 points)", bulletLines[5]);
        Assert.StartsWith("• Assets/Scripts/ExactFull.cs: 100.0% (10/10 points)", bulletLines[6]);

        // Input list must remain untouched
        Assert.Equal(originalOrder, coverageResult.Files);
    }

    [Fact]
    public async Task UnityCoverage_UncoveredOnly_OmitsFullAndZeroPointFilesWhilePreservingScopeAggregate()
    {
        var coverageResult = new CoverageResult
        {
            Success = true,
            Files =
            [
                new CoverageFileResult { Path = "Assets/Scripts/Full.cs", TotalPoints = 10, CoveredPoints = 10 },
                new CoverageFileResult { Path = "Assets/Scripts/Interface.cs", TotalPoints = 0, CoveredPoints = 0 },
                new CoverageFileResult { Path = "Assets/Scripts/NearFull.cs", TotalPoints = 10000, CoveredPoints = 9999, UncoveredLines = [99] },
                new CoverageFileResult { Path = "Assets/Scripts/Partial.cs", TotalPoints = 20, CoveredPoints = 10, UncoveredLines = [4, 5] }
            ]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.StartsWith("Total Coverage: 99.9% (10019/10030 points across 4 files; showing 2 uncovered, 2 omitted)", text);
        var bulletLines = text.Split(Environment.NewLine).Where(line => line.StartsWith("• ")).ToArray();
        Assert.Equal(2, bulletLines.Length);
        Assert.StartsWith("• Assets/Scripts/Partial.cs: 50.0% (10/20 points)", bulletLines[0]);
        Assert.StartsWith("• Assets/Scripts/NearFull.cs: 100.0% (9999/10000 points)", bulletLines[1]);
        Assert.DoesNotContain("Assets/Scripts/Full.cs", text);
        Assert.DoesNotContain("Assets/Scripts/Interface.cs", text);
    }

    [Fact]
    public async Task UnityCoverage_UncoveredOnly_SingleUncoveredFile_OmitsAggregateHeader()
    {
        var coverageResult = new CoverageResult
        {
            Success = true,
            Files =
            [
                new CoverageFileResult { Path = "Assets/Scripts/Partial.cs", TotalPoints = 10, CoveredPoints = 4, UncoveredLines = [2, 3] }
            ]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.DoesNotContain("Total Coverage:", text);
        Assert.Contains("• Assets/Scripts/Partial.cs: 40.0% (4/10 points)", text);
    }

    [Fact]
    public async Task UnityCoverage_UncoveredOnly_AllFilesUncovered_OmitsOmittedCountSuffix()
    {
        var coverageResult = new CoverageResult
        {
            Success = true,
            Files =
            [
                new CoverageFileResult { Path = "Assets/Scripts/A.cs", TotalPoints = 10, CoveredPoints = 8, UncoveredLines = [1] },
                new CoverageFileResult { Path = "Assets/Scripts/B.cs", TotalPoints = 10, CoveredPoints = 2, UncoveredLines = [5] }
            ]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.StartsWith("Total Coverage: 50.0% (10/20 points across 2 files)" + Environment.NewLine, text);
        Assert.DoesNotContain("omitted", text);
        var bulletLines = text.Split(Environment.NewLine).Where(line => line.StartsWith("• ")).ToArray();
        Assert.Equal(2, bulletLines.Length);
        Assert.StartsWith("• Assets/Scripts/B.cs: 20.0%", bulletLines[0]);
        Assert.StartsWith("• Assets/Scripts/A.cs: 80.0%", bulletLines[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnityCoverage_UncoveredOnly_AllFullyCovered_ShowsAggregateAndOmissionSummary(bool singleFile)
    {
        var files = new List<CoverageFileResult>
        {
            new() { Path = "Assets/Scripts/A.cs", TotalPoints = 10, CoveredPoints = 10 }
        };
        if (!singleFile)
        {
            files.Add(new CoverageFileResult { Path = "Assets/Scripts/B.cs", TotalPoints = 10, CoveredPoints = 10 });
        }

        var result = await InvokeUnityCoverageAsync(new CoverageResult { Success = true, Files = files }, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        if (singleFile)
        {
            Assert.Equal(
                $"Total Coverage: 100.0% (10/10 points across 1 file){Environment.NewLine}{Environment.NewLine}No uncovered files to show (1 fully covered file omitted).",
                text);
        }
        else
        {
            Assert.Equal(
                $"Total Coverage: 100.0% (20/20 points across 2 files){Environment.NewLine}{Environment.NewLine}No uncovered files to show (2 fully covered files omitted).",
                text);
        }
    }

    [Fact]
    public async Task UnityCoverage_UncoveredOnly_MixedFullAndZeroPointFiles_ShowsAggregateAndCombinedOmissionSummary()
    {
        var coverageResult = new CoverageResult
        {
            Success = true,
            Files =
            [
                new CoverageFileResult { Path = "Assets/Scripts/A.cs", TotalPoints = 10, CoveredPoints = 10 },
                new CoverageFileResult { Path = "Assets/Scripts/B.cs", TotalPoints = 10, CoveredPoints = 10 },
                new CoverageFileResult { Path = "Assets/Scripts/IThing.cs", TotalPoints = 0, CoveredPoints = 0 }
            ]
        };

        var result = await InvokeUnityCoverageAsync(coverageResult, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.Equal(
            $"Total Coverage: 100.0% (20/20 points across 3 files){Environment.NewLine}{Environment.NewLine}No uncovered files to show (2 fully covered, 1 with 0 executable points omitted).",
            text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnityCoverage_UncoveredOnly_OnlyZeroPointFiles_OmitsAggregateHeaderAndReportsZeroPointOmission(int count)
    {
        var files = new List<CoverageFileResult>();
        for (int i = 0; i < count; i++)
        {
            files.Add(new CoverageFileResult { Path = $"Assets/Scripts/IEmpty{i}.cs", TotalPoints = 0, CoveredPoints = 0 });
        }

        var result = await InvokeUnityCoverageAsync(new CoverageResult { Success = true, Files = files }, uncoveredOnly: true);
        Assert.False(result.IsError);
        string text = GetResultText(result);

        Assert.DoesNotContain("Total Coverage:", text);
        string expectedNoun = count == 1 ? "file" : "files";
        Assert.Equal($"No uncovered files to show ({count} {expectedNoun} with 0 executable points omitted).", text);
    }

    [Fact]
    public async Task UnityCoverage_UncoveredOnly_EmptyOrFailureResults_BehaveIdentically()
    {
        var emptyResult = await InvokeUnityCoverageAsync(new CoverageResult { Success = true, Files = [] }, uncoveredOnly: true);
        Assert.False(emptyResult.IsError);
        Assert.Equal("No compiled C# scripts found in the specified path(s).", GetResultText(emptyResult));

        var errorResult = await InvokeUnityCoverageAsync(
            new CoverageResult { Success = false, Message = "No coverage data recorded." },
            uncoveredOnly: true);
        Assert.True(errorResult.IsError);
        Assert.Equal("No coverage data recorded.", GetResultText(errorResult));
    }

    private static async Task<CallToolResult> InvokeUnityCoverageAsync(CoverageResult coverageResult, bool uncoveredOnly = false)
    {
        var (tempDir, _, client, tools) = CreateTestContext();
        try
        {
            client.CoverageResultToReturn = coverageResult;
            return await tools.UnityCoverageAsync(["Assets/Scripts/"], uncoveredOnly: uncoveredOnly);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("<AsyncMethod>d__0", "AsyncMethod")]
    [InlineData("<YieldEnumeratorMethod>d__1", "YieldEnumeratorMethod")]
    [InlineData("<SimpleLambdaMethod>b__0", "SimpleLambdaMethod")]
    [InlineData("<<NestedLambdaMethod>b__0>b__1", "NestedLambdaMethod")]
    [InlineData("<HostMethod>g__RegularLocal|0_0", "HostMethod.RegularLocal")]
    [InlineData("<<HostMethod>g__AsyncLocal|0_0>d", "HostMethod.AsyncLocal")]
    [InlineData("get_AutoProperty", "get_AutoProperty")]
    [InlineData("set_AutoProperty", "set_AutoProperty")]
    [InlineData(".ctor", ".ctor")]
    [InlineData(".cctor", ".cctor")]
    [InlineData("UnityLeanMcp.ITestCoverageInterface.ExplicitMethod", "ITestCoverageInterface.ExplicitMethod")]
    public void GetCoverageHandler_DemangleMethodName_HandlesAllRoslynPatterns(string rawName, string expected)
    {
        string demangled = GetCoverageHandler.DemangleMethodName(rawName);
        Assert.Equal(expected, demangled);
    }

    [Fact]
    public void GetCoverageHandler_ComputeUncoveredSpans_GroupsByMethodAndOrdersByLine()
    {
        var methodA = typeof(CoverageFormattingTests).GetMethod(nameof(GetCoverageHandler_ComputeUncoveredSpans_GroupsByMethodAndOrdersByLine))!;
        var methodB = typeof(CoverageFormattingTests).GetMethod(nameof(FormatUncoveredMethodSpans_EmptyOrNull_ReturnsEmptyString))!;

        var points = new List<UnityEngine.TestTools.CoveredSequencePoint>
        {
            new() { method = methodA, line = 10, hitCount = 1 },
            new() { method = methodA, line = 11, hitCount = 0 },
            new() { method = methodA, line = 12, hitCount = 0 },
            new() { method = methodA, line = 13, hitCount = 1 },
            new() { method = methodB, line = 15, hitCount = 0 },
            new() { method = methodB, line = 16, hitCount = 0 },
            new() { method = methodA, line = 20, hitCount = 0 }
        };

        var spans = GetCoverageHandler.ComputeUncoveredSpans(points);
        Assert.Equal(3, spans.Count);

        Assert.Equal(methodA.Name, spans[0].method);
        Assert.Equal(11, spans[0].startLine);
        Assert.Equal(12, spans[0].endLine);

        Assert.Equal(methodB.Name, spans[1].method);
        Assert.Equal(15, spans[1].startLine);
        Assert.Equal(16, spans[1].endLine);

        Assert.Equal(methodA.Name, spans[2].method);
        Assert.Equal(20, spans[2].startLine);
        Assert.Equal(20, spans[2].endLine);
    }

    [Fact]
    public void GetCoverageHandler_ProcessMethod_WhenAsyncAwaitCompletesSynchronously_DoesNotReportAwaitLineUncovered()
    {
        var stateMachineType = typeof(AsyncSampleStateMachine)
            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name.StartsWith("<RunAsync>d__", StringComparison.Ordinal));
        Assert.NotNull(stateMachineType);
        var moveNext = stateMachineType!.GetMethod(
            "MoveNext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.NotNull(moveNext);

        const string sourcePath = "Assets/Scripts/Processor.cs";
        try
        {
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = m =>
            {
                if (!ReferenceEquals(m, moveNext))
                    return Array.Empty<UnityEngine.TestTools.CoveredSequencePoint>();

                return
                [
                    new() { method = moveNext, filename = sourcePath, line = 20, column = 5, ilOffset = 14, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 15, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 55, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 82, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 22, column = 9, ilOffset = 135, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 23, column = 5, ilOffset = 170, hitCount = 1 }
                ];
            };

            var pointsByFile = new Dictionary<string, List<UnityEngine.TestTools.CoveredSequencePoint>>(GetCoverageHandler.PathComparer);
            GetCoverageHandler.ProcessMethod(moveNext!, "C:/Project/", ["Assets/"], pointsByFile);

            Assert.True(pointsByFile.TryGetValue(sourcePath, out var coveredPoints));
            Assert.Equal(4, coveredPoints!.Count);
            Assert.All(coveredPoints, p => Assert.True(p.hitCount > 0));
            Assert.Empty(GetCoverageHandler.ComputeUncoveredSpans(coveredPoints));

            // When none of the points on the await line ran, it must still be reported uncovered once.
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = m =>
            {
                if (!ReferenceEquals(m, moveNext))
                    return Array.Empty<UnityEngine.TestTools.CoveredSequencePoint>();

                return
                [
                    new() { method = moveNext, filename = sourcePath, line = 20, column = 5, ilOffset = 14, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 15, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 55, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 82, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 22, column = 9, ilOffset = 135, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 23, column = 5, ilOffset = 170, hitCount = 1 }
                ];
            };

            var unhitByFile = new Dictionary<string, List<UnityEngine.TestTools.CoveredSequencePoint>>(GetCoverageHandler.PathComparer);
            GetCoverageHandler.ProcessMethod(moveNext!, "C:/Project/", ["Assets/"], unhitByFile);
            Assert.True(unhitByFile.TryGetValue(sourcePath, out var unhitPoints));
            Assert.Equal(4, unhitPoints!.Count);
            Assert.Equal(3, unhitPoints.Count(p => p.hitCount > 0));
            var unhitSpans = GetCoverageHandler.ComputeUncoveredSpans(unhitPoints);
            Assert.Single(unhitSpans);
            Assert.Equal("RunAsync", unhitSpans[0].method);
            Assert.Equal(21, unhitSpans[0].startLine);
            Assert.Equal(21, unhitSpans[0].endLine);

            // A separate statement at a distinct column on the same line that was not hit stays uncovered.
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = m =>
            {
                if (!ReferenceEquals(m, moveNext))
                    return Array.Empty<UnityEngine.TestTools.CoveredSequencePoint>();

                return
                [
                    new() { method = moveNext, filename = sourcePath, line = 20, column = 5, ilOffset = 14, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 15, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 55, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 9, ilOffset = 82, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 21, column = 45, ilOffset = 130, hitCount = 0 },
                    new() { method = moveNext, filename = sourcePath, line = 22, column = 9, ilOffset = 135, hitCount = 1 },
                    new() { method = moveNext, filename = sourcePath, line = 23, column = 5, ilOffset = 170, hitCount = 1 }
                ];
            };

            var partialByFile = new Dictionary<string, List<UnityEngine.TestTools.CoveredSequencePoint>>(GetCoverageHandler.PathComparer);
            GetCoverageHandler.ProcessMethod(moveNext!, "C:/Project/", ["Assets/"], partialByFile);
            Assert.True(partialByFile.TryGetValue(sourcePath, out var partialPoints));
            Assert.Equal(5, partialPoints!.Count);
            var partialSpans = GetCoverageHandler.ComputeUncoveredSpans(partialPoints);
            Assert.Single(partialSpans);
            Assert.Equal("RunAsync", partialSpans[0].method);
            Assert.Equal(21, partialSpans[0].startLine);
            Assert.Equal(21, partialSpans[0].endLine);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = null;
        }
    }

    private sealed class AsyncSampleStateMachine
    {
        public async Task<bool> RunAsync()
        {
            await Task.Yield();
            return true;
        }
    }

    [Fact]
    public void GetCoverageHandler_WriteCoverage_ProfilerRecompilationWithUnchangedInputsRemainsValid()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_epoch_" + Guid.NewGuid().ToString("N"));
        string originalProjectRoot = UnityLeanMcpPaths.ProjectRoot;
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        string csFile = Path.Combine(projectRoot, "Assets", "Foo.cs");
        File.WriteAllText(csFile, "public class Foo {}");
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;
        bool wasCompiling = UnityLeanMcpCompilationTracker.IsCompiling;
        try
        {
            CreateCompletedCoverageSnapshot(projectRoot, "run-unchanged-inputs");
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 5);
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 6);

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([csFile], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("SUCCESS ", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            UnityLeanMcpCompilationTracker.IsCompiling = wasCompiling;
            UnityLeanMcpPaths.ProjectRoot = originalProjectRoot;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetCoverageHandler_WriteCoverage_WhenQueriedFileModified_ReturnsObsoleteFailure()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_stale_" + Guid.NewGuid().ToString("N"));
        string originalProjectRoot = UnityLeanMcpPaths.ProjectRoot;
        string assetsDir = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsDir);
        string csFile = Path.Combine(assetsDir, "Player.cs");
        File.WriteAllText(csFile, "public class Player {}");
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;
        bool wasCompiling = UnityLeanMcpCompilationTracker.IsCompiling;

        try
        {
            CreateCompletedCoverageSnapshot(projectRoot, "run-content-change");
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(csFile);
            File.WriteAllText(csFile, "public class Player { public int Value => 2; }");
            File.SetLastWriteTimeUtc(csFile, originalWriteTime);

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([csFile], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("FAILURE Coverage data is obsolete because compilation inputs changed during or after the test run.", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            UnityLeanMcpCompilationTracker.IsCompiling = wasCompiling;
            UnityLeanMcpPaths.ProjectRoot = originalProjectRoot;
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = null;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    [Theory]
    [InlineData("new-source")]
    [InlineData("plugin-dll")]
    [InlineData("plugin-meta")]
    [InlineData("analyzer-config")]
    [InlineData("assembly-defines")]
    [InlineData("external-package-source")]
    public void GetCoverageHandler_WriteCoverage_DetectsCompilationInputChangesBeforeImport(string inputKind)
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_input_" + Guid.NewGuid().ToString("N"));
        string originalProjectRoot = UnityLeanMcpPaths.ProjectRoot;
        string assets = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assets);
        string source = Path.Combine(assets, "Player.cs");
        File.WriteAllText(source, "public class Player {}");
        string? packageRoot = null;
        UnityEditor.Compilation.Assembly assembly = new() { name = "coverage-config", defines = new[] { "BASELINE" } };
        try
        {
            UnityLeanMcpPaths.ProjectRoot = projectRoot;
            UnityEditor.Compilation.CompilationPipeline.GetAssembliesFunc = () => new[] { assembly };
            UnityEditor.Compilation.CompilationPipeline.GetPrecompiledAssemblyPathsFunc = () => Array.Empty<string>();

            if (inputKind == "plugin-meta")
            {
                string plugins = Path.Combine(assets, "Plugins");
                Directory.CreateDirectory(plugins);
                File.WriteAllBytes(Path.Combine(plugins, "Plugin.dll"), [1, 2, 3]);
                File.WriteAllText(Path.Combine(plugins, "Plugin.dll.meta"), "PluginImporter:\n  autoReferenced: 1\n");
            }
            else if (inputKind == "external-package-source")
            {
                packageRoot = Path.Combine(Path.GetDirectoryName(projectRoot)!, "external-package-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(packageRoot);
                File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"local.external\"}");
                File.WriteAllText(Path.Combine(packageRoot, "Existing.cs"), "public class Existing {}");
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackagesFunc = () => new[]
                {
                    new UnityEditor.PackageManager.PackageInfo { resolvedPath = packageRoot }
                };
            }

            CreateCompletedCoverageSnapshot(projectRoot, "run-input-" + inputKind);

            switch (inputKind)
            {
                case "new-source":
                    File.WriteAllText(Path.Combine(assets, "Added.cs"), "public class Added {}");
                    break;
                case "plugin-dll":
                    string plugins = Path.Combine(assets, "Plugins");
                    Directory.CreateDirectory(plugins);
                    File.WriteAllBytes(Path.Combine(plugins, "Added.dll"), [4, 5, 6]);
                    File.WriteAllText(Path.Combine(plugins, "Added.dll.meta"), "PluginImporter:\n  autoReferenced: 1\n");
                    break;
                case "plugin-meta":
                    File.WriteAllText(Path.Combine(assets, "Plugins", "Plugin.dll.meta"), "PluginImporter:\n  autoReferenced: 0\n");
                    break;
                case "analyzer-config":
                    File.WriteAllText(Path.Combine(assets, "Assembly-CSharp.globalconfig"), "is_global = true\n");
                    break;
                case "assembly-defines":
                    assembly.defines = new[] { "BASELINE", "FEATURE_SWITCH" };
                    break;
                case "external-package-source":
                    File.WriteAllText(Path.Combine(packageRoot!, "Added.cs"), "public class Added {}");
                    break;
            }

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage(new List<string> { source }, projectRoot, writer);
            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("FAILURE Coverage data is obsolete because compilation inputs changed during or after the test run.", response);
        }
        finally
        {
            UnityLeanMcpPaths.ProjectRoot = originalProjectRoot;
            UnityEngine.TestTools.Coverage.enabled = false;
            UnityEditor.Compilation.CompilationPipeline.GetAssembliesFunc = null;
            UnityEditor.Compilation.CompilationPipeline.GetPrecompiledAssemblyPathsFunc = null;
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackagesFunc = null;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
            if (packageRoot != null) try { Directory.Delete(packageRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetCoverageHandler_WriteCoverage_DuringUnchangedCompilationRemainsValid()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_compiling_" + Guid.NewGuid().ToString("N"));
        string originalProjectRoot = UnityLeanMcpPaths.ProjectRoot;
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        string csFile = Path.Combine(projectRoot, "Assets", "Foo.cs");
        File.WriteAllText(csFile, "public class Foo {}");
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;
        bool wasCompiling = UnityLeanMcpCompilationTracker.IsCompiling;
        try
        {
            CreateCompletedCoverageSnapshot(projectRoot, "run-profiler-compile");
            UnityLeanMcpCompilationTracker.IncrementCompilationEpoch();
            UnityLeanMcpCompilationTracker.IsCompiling = true;

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([csFile], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("SUCCESS ", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            UnityLeanMcpCompilationTracker.IsCompiling = wasCompiling;
            UnityLeanMcpPaths.ProjectRoot = originalProjectRoot;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    private static void CreateCompletedCoverageSnapshot(string projectRoot, string runId)
    {
        UnityLeanMcpPaths.ProjectRoot = projectRoot;
        UnityEditor.EditorApplication.isCompiling = false;
        UnityEditor.EditorApplication.isUpdating = false;
        UnityLeanMcpCompilationTracker.IsCompiling = false;
        UnityLeanMcpCompilationTracker.IsUpdating = false;
        UnityLeanMcpCompilationTracker.RefreshRequired = false;
        UnityLeanMcpCompilationTracker.RefreshPending = false;
        UnityLeanMcpCompilationTracker.AwaitingCompilationStart = false;
        UnityLeanMcpCompilationTracker.CompilationInProgress = false;
        UnityLeanMcpCompilationTracker.ScriptCompilationFailed = false;
        UnityEngine.TestTools.Coverage.enabled = true;
        GetCoverageHandler.ClearCapturedCoverage();
        GetCoverageHandler.BeginCoverageAttempt(runId);
        GetCoverageHandler.ObserveCompilationStarted();
        GetCoverageHandler.ObserveCompilationFinished();
        for (int i = 0; i < 5; i++)
            Assert.Equal(i == 4, GetCoverageHandler.AdvanceCoverageLifecycle(runId, restoration: false));
        Assert.True(GetCoverageHandler.CoverageBaselineDiskInputsMatch(runId));
        Assert.True(GetCoverageHandler.EstablishCoverageBaseline(runId));
        RunTestsHandler.MarkCoverageRunStarted(runId);
        RunTestsHandler.MarkCoverageRunCompleted(runId, complete: true);
        UnityLeanMcp.CoverageCompletionOutcome outcome = default;
        string lifecycleState = "";
        for (int i = 0; i < 6; i++)
        {
            outcome = UnityLeanMcp.CoverageLifecycleManager.CompleteCoverageAttempt(runId);
            lifecycleState = string.Join(";", new[]
            {
                UnityEditor.SessionState.GetString(GetCoverageHandler.CoverageAttemptKey, ""),
                "editorCompiling=" + UnityEditor.EditorApplication.isCompiling,
                "editorUpdating=" + UnityEditor.EditorApplication.isUpdating,
                "trackerCompiling=" + UnityLeanMcpCompilationTracker.IsCompiling,
                "trackerUpdating=" + UnityLeanMcpCompilationTracker.IsUpdating,
                "refreshPending=" + UnityLeanMcpCompilationTracker.RefreshPending,
                "awaiting=" + UnityLeanMcpCompilationTracker.AwaitingCompilationStart,
                "inProgress=" + UnityLeanMcpCompilationTracker.CompilationInProgress,
                "refreshRequired=" + UnityLeanMcpCompilationTracker.RefreshRequired
            });
            if (!outcome.HasOutcome || !outcome.IsPending) break;
        }
        Assert.True(outcome.State == UnityLeanMcp.CoverageCompletionState.Available,
            lifecycleState);
    }

    private static (string tempDir, FakeUnityProcessManager pm, FakeUnityClient client, UnityTools tools) CreateTestContext()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "unity_cov_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Temp"));
        Directory.CreateDirectory(Path.Combine(tempDir, "ProjectSettings"));

        var pm = new FakeUnityProcessManager(tempDir);
        var client = new FakeUnityClient(pm);
        var tools = new UnityTools(client, pm.PathResolver);
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
