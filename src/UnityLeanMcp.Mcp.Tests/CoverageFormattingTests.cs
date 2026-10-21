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
    public void FormatUncoveredMethodSpans_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal("", UnityTools.FormatUncoveredMethodSpans(null!));
        Assert.Equal("", UnityTools.FormatUncoveredMethodSpans([]));
    }

    [Fact]
    public void FormatUncoveredMethodSpans_SingleMethodSingleSpan_FormatsCorrectly()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "MovePlayer", StartLine = 42, EndLine = 50 }
        };
        string formatted = UnityTools.FormatUncoveredMethodSpans(spans);
        Assert.Equal("MovePlayer (42-50)", formatted);
    }

    [Fact]
    public void FormatUncoveredMethodSpans_SingleMethodSingleLine_FormatsWithoutHyphen()
    {
        var spans = new List<UncoveredMethodSpan>
        {
            new() { Method = "TakeDamage", StartLine = 15, EndLine = 15 }
        };
        string formatted = UnityTools.FormatUncoveredMethodSpans(spans);
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
        string formatted = UnityTools.FormatUncoveredMethodSpans(spans);
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
        string formatted = UnityTools.FormatUncoveredMethodSpans(spans);
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
        string formatted = UnityTools.FormatUncoveredMethodSpans(spans);
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
            Assert.Contains("(10/10 points)" + Environment.NewLine + "• Assets/Scripts/FileB.cs", text);
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
    public void GetCoverageHandler_WriteCoverage_WhenScriptsRecompiled_ReturnsObsoleteFailure()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_epoch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;
        try
        {
            UnityEngine.TestTools.Coverage.enabled = true;
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 5);
            GetCoverageHandler.CaptureCoverage("run-epoch-test");

            // Simulate recompilation occurring after capture
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 6);

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([Path.Combine(projectRoot, "Assets/Foo.cs")], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("FAILURE Coverage data is obsolete because scripts were recompiled since coverage was recorded.", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetCoverageHandler_WriteCoverage_WhenQueriedFileModified_ReturnsObsoleteFailure()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_stale_" + Guid.NewGuid().ToString("N"));
        string assetsDir = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsDir);
        string csFile = Path.Combine(assetsDir, "Player.cs");
        File.WriteAllText(csFile, "public class Player {}");
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;

        try
        {
            UnityEngine.TestTools.Coverage.enabled = true;
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 0);

            // Mock sequence point for Player.cs
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = _ => new[]
            {
                new UnityEngine.TestTools.CoveredSequencePoint { filename = csFile, line = 1, hitCount = 1 }
            };

            GetCoverageHandler.CaptureCoverage("run-stale-test");

            // Simulate file edit after capture: set file write time to 1 hour in the future
            File.SetLastWriteTimeUtc(csFile, DateTime.UtcNow.AddHours(1));

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([csFile], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("FAILURE Coverage data for 'Assets/Player.cs' is obsolete because the file was modified since coverage was recorded.", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            UnityEngine.TestTools.Coverage.GetSequencePointsForFunc = null;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetCoverageHandler_WriteCoverage_WhenCompilingInProgress_ReturnsObsoleteFailure()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "unity_cov_compiling_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        bool origCoverage = UnityEngine.TestTools.Coverage.enabled;
        try
        {
            UnityEngine.TestTools.Coverage.enabled = true;
            UnityEditor.SessionState.SetInt(UnityLeanMcpCompilationTracker.CompilationEpochKey, 0);
            GetCoverageHandler.CaptureCoverage("run-compiling-test");

            UnityEditor.EditorApplication.isCompiling = true;

            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            GetCoverageHandler.WriteCoverage([Path.Combine(projectRoot, "Assets/Foo.cs")], projectRoot, writer);

            string response = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Trim();
            Assert.StartsWith("FAILURE Coverage data is obsolete because scripts were recompiled since coverage was recorded.", response);
        }
        finally
        {
            UnityEngine.TestTools.Coverage.enabled = origCoverage;
            UnityEditor.EditorApplication.isCompiling = false;
            GetCoverageHandler.ClearCapturedCoverage();
            try { Directory.Delete(projectRoot, true); } catch { }
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
