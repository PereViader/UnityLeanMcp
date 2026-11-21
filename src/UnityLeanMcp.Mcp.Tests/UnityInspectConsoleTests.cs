using System;
using UnityEditor;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Collection("CommandGate")]
[Trait("Category", "Unit")]
public class UnityInspectConsoleTests
{
    private const int ModeError = 1 << 0;
    private const int ModeAssert = 1 << 1;
    private const int ModeLog = 1 << 2;
    private const int ModeFatal = 1 << 4;
    private const int ModeAssetImportError = 1 << 6;
    private const int ModeAssetImportWarning = 1 << 7;
    private const int ModeScriptingError = 1 << 8;
    private const int ModeScriptingWarning = 1 << 9;
    private const int ModeScriptingLog = 1 << 10;
    private const int ModeScriptCompileError = 1 << 11;
    private const int ModeScriptCompileWarning = 1 << 12;
    private const int ModeScriptingException = 1 << 17;
    private const int ModeGraphCompileError = 1 << 20;

    [Fact]
    public void Console_WhenEmpty_ReturnsEmptyMessageAndPairsStartEnd()
    {
        using var scope = new UnityInspectTestScope();

        string result = Inspect.Console();

        Assert.Equal("(No entries in Unity Console)", result);
        Assert.Equal(1, LogEntries.StartGettingEntriesCallCount);
        Assert.Equal(1, LogEntries.EndGettingEntriesCallCount);
    }

    [Fact]
    public void Console_WhenSeverityNone_ReturnsShortCircuitMessageWithoutReadingEntries()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.Add(new LogEntries.StubEntry { Message = "Hello", Mode = ModeScriptingLog });

        string result = Inspect.Console(severity: ConsoleSeverity.None);

        Assert.Equal("(No Console entries matched: severity is ConsoleSeverity.None)", result);
        Assert.Equal(0, LogEntries.StartGettingEntriesCallCount);
    }

    [Fact]
    public void Console_MapsAllUnityModeFlagsToThreeSeverityBuckets()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.AddRange(new[]
        {
            new LogEntries.StubEntry { Message = "m_err", Mode = ModeError },
            new LogEntries.StubEntry { Message = "m_assert", Mode = ModeAssert },
            new LogEntries.StubEntry { Message = "m_fatal", Mode = ModeFatal },
            new LogEntries.StubEntry { Message = "m_import_err", Mode = ModeAssetImportError },
            new LogEntries.StubEntry { Message = "m_script_err", Mode = ModeScriptingError },
            new LogEntries.StubEntry { Message = "m_compile_err", Mode = ModeScriptCompileError },
            new LogEntries.StubEntry { Message = "m_exception", Mode = ModeScriptingException },
            new LogEntries.StubEntry { Message = "m_graph_err", Mode = ModeGraphCompileError },
            new LogEntries.StubEntry { Message = "m_import_warn", Mode = ModeAssetImportWarning },
            new LogEntries.StubEntry { Message = "m_script_warn", Mode = ModeScriptingWarning },
            new LogEntries.StubEntry { Message = "m_compile_warn", Mode = ModeScriptCompileWarning },
            new LogEntries.StubEntry { Message = "m_log", Mode = ModeLog },
            new LogEntries.StubEntry { Message = "m_script_log", Mode = ModeScriptingLog },
            new LogEntries.StubEntry { Message = "m_fallback", Mode = 0 }
        });

        string all = Inspect.Console();
        Assert.Contains("Console entries (14 shown, 14 unique matched of 14 total; 8 error, 3 warning, 3 log):", all);
        Assert.Contains("[Error] m_assert", all);
        Assert.Contains("[Error] m_exception", all);
        Assert.Contains("[Error] m_graph_err", all);
        Assert.Contains("[Warning] m_compile_warn", all);
        Assert.Contains("[Log] m_fallback", all);

        string errorsAndWarnings = Inspect.Console(severity: ConsoleSeverity.Error | ConsoleSeverity.Warning);
        Assert.Contains("Console entries (11 shown, 11 unique matched of 14 total; 8 error, 3 warning, 3 log):", errorsAndWarnings);
        Assert.DoesNotContain("[Log]", errorsAndWarnings);
        Assert.Contains("[Error] m_exception", errorsAndWarnings);
        Assert.Contains("[Warning] m_script_warn", errorsAndWarnings);
    }

    [Fact]
    public void Console_AlwaysCollapsesDuplicatesAndPlacesCountPrefixFirstWithTimestamp()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.AddRange(new[]
        {
            new LogEntries.StubEntry
            {
                Message = "[18:24:01.100] NullReferenceException: Object reference not set\nPlayer:Update () (at Assets/Scripts/Player.cs:42)",
                File = @"C:\Project\Assets\Scripts\Player.cs",
                Line = 42,
                Mode = ModeScriptingException,
                Count = 5
            },
            new LogEntries.StubEntry
            {
                Message = "[18:24:03.000] Low memory warning",
                File = "Assets/Scripts/Mem.cs",
                Line = 10,
                Mode = ModeScriptingWarning,
                Count = 1
            },
            new LogEntries.StubEntry
            {
                Message = "[18:24:05.812] NullReferenceException: Object reference not set\nPlayer:Update () (at Assets/Scripts/Player.cs:42)",
                File = "Assets/Scripts/Player.cs",
                Line = 42,
                Mode = ModeScriptingException,
                Count = 7
            },
            new LogEntries.StubEntry
            {
                Message = "Tick\nUnityEngine.Debug:Log (object)",
                Mode = ModeScriptingLog,
                Count = 2
            }
        });

        string result = Inspect.Console();

        Assert.Contains("Console entries (3 shown, 3 unique matched of 15 total; 12 error, 1 warning, 2 log):", result);
        Assert.Contains("(x12) [18:24:05.812] [Error] NullReferenceException: Object reference not set (Assets/Scripts/Player.cs:42)", result);
        Assert.Contains("[18:24:03.000] [Warning] Low memory warning (Assets/Scripts/Mem.cs:10)", result);
        Assert.DoesNotContain("(x1)", result);
        Assert.Contains("(x2) [Log] Tick", result);

        // Latest occurrence order: 18:24:03.000 warning precedes 18:24:05.812 recurring error
        int warnIdx = result.IndexOf("[18:24:03.000] [Warning]", StringComparison.Ordinal);
        int errIdx = result.IndexOf("(x12) [18:24:05.812] [Error]", StringComparison.Ordinal);
        Assert.True(warnIdx >= 0 && errIdx > warnIdx);
    }

    [Fact]
    public void Console_RecurringOldErrorMovesToLatestPositionAndSurvivesMaxItemsTailTruncation()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.Add(new LogEntries.StubEntry { Message = "Recurring error", Mode = ModeScriptingError });
        for (int i = 1; i <= 5; i++)
        {
            LogEntries.Entries.Add(new LogEntries.StubEntry { Message = $"Noise {i}", Mode = ModeScriptingLog });
        }
        LogEntries.Entries.Add(new LogEntries.StubEntry { Message = "Recurring error", Mode = ModeScriptingError });

        string result = Inspect.Console(maxItems: 2);

        Assert.Contains("Console entries (2 shown, 6 unique matched of 7 total; 2 error, 0 warning, 5 log):", result);
        Assert.Contains("[Log] Noise 5", result);
        Assert.Contains("(x2) [Error] Recurring error", result);
        Assert.Contains(Inspect.ItemTruncationMarker, result);
    }

    [Fact]
    public void Console_WildcardFilterMatchesMultilineMessageAndMultilineStackTrace()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.AddRange(new[]
        {
            new LogEntries.StubEntry
            {
                Message = "First line\nSecond target line\nUnityEngine.DebugLogHandler:Internal_Log ()\nUnityEngine.Debug:Assert (bool)\nMySystem:Verify () (at Assets/Scripts/MySystem.cs:77)\nMySystem:Tick () (at Assets/Scripts/MySystem.cs:99)",
                Mode = ModeAssert
            },
            new LogEntries.StubEntry
            {
                Message = "Unrelated log",
                Mode = ModeScriptingLog
            }
        });

        string byMultilineMessage = Inspect.Console(filter: "*Second target*");
        Assert.Contains("First line\\nSecond target line", byMultilineMessage);
        Assert.DoesNotContain("Unrelated log", byMultilineMessage);

        string byMultilineStack = Inspect.Console(filter: "*MySystem:Tick*", includeStackTrace: true);
        Assert.Contains("First line\\nSecond target line (Assets/Scripts/MySystem.cs:77)", byMultilineStack);
        Assert.DoesNotContain("UnityEngine.DebugLogHandler:", byMultilineStack);
        Assert.DoesNotContain("UnityEngine.Debug:Assert", byMultilineStack);
        Assert.Contains("  MySystem:Verify () (at Assets/Scripts/MySystem.cs:77)", byMultilineStack);
        Assert.Contains("  MySystem:Tick () (at Assets/Scripts/MySystem.cs:99)", byMultilineStack);
        Assert.DoesNotContain("Unrelated log", byMultilineStack);
    }

    [Fact]
    public void Console_FiltersBySubstringAndWildcardAcrossMessageStackAndFile()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.AddRange(new[]
        {
            new LogEntries.StubEntry
            {
                Message = "Failed to load texture\nAssetLoader:Load () (at Assets/Scripts/AssetLoader.cs:88)",
                File = "Assets/Scripts/AssetLoader.cs",
                Line = 88,
                Mode = ModeScriptingError
            },
            new LogEntries.StubEntry
            {
                Message = "Player spawned",
                File = "Assets/Scripts/Spawner.cs",
                Line = 12,
                Mode = ModeScriptingLog
            }
        });

        string byWildcard = Inspect.Console(filter: "*texture*");
        Assert.Contains("Failed to load texture", byWildcard);
        Assert.DoesNotContain("Player spawned", byWildcard);

        string byFile = Inspect.Console(filter: "Spawner.cs");
        Assert.Contains("Player spawned", byFile);
        Assert.DoesNotContain("Failed to load texture", byFile);

        string byStack = Inspect.Console(filter: "AssetLoader:Load");
        Assert.Contains("Failed to load texture", byStack);
        Assert.DoesNotContain("Player spawned", byStack);

        string noMatch = Inspect.Console(filter: "DoesNotExist");
        Assert.Equal("(No Console entries matched; 2 total in Console: 1 error, 0 warning, 1 log)", noMatch);
    }

    [Fact]
    public void Console_IncludeStackTraceTogglesIndentedFramesAndStripsDebugLogPlumbing()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.Add(new LogEntries.StubEntry
        {
            Message = "Something happened\nUnityEngine.Debug:LogWarning (object)\nMySystem:Check () (at Assets/Scripts/MySystem.cs:25)\nMySystem:Update () (at Assets/Scripts/MySystem.cs:10)",
            File = string.Empty,
            Line = 0,
            Mode = ModeScriptingWarning
        });

        string compact = Inspect.Console(includeStackTrace: false);
        Assert.Contains("[Warning] Something happened (Assets/Scripts/MySystem.cs:25)", compact);
        Assert.DoesNotContain("MySystem:Update ()", compact);

        string detailed = Inspect.Console(includeStackTrace: true);
        Assert.Contains("[Warning] Something happened (Assets/Scripts/MySystem.cs:25)", detailed);
        Assert.DoesNotContain("UnityEngine.Debug:LogWarning", detailed);
        Assert.Contains("  MySystem:Check () (at Assets/Scripts/MySystem.cs:25)", detailed);
        Assert.Contains("  MySystem:Update () (at Assets/Scripts/MySystem.cs:10)", detailed);
    }

    [Fact]
    public void Console_FiltersOutInternalUnityLeanMcpHarnessLogs()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.AddRange(new[]
        {
            new LogEntries.StubEntry { Message = "UnityLeanMcp: Server started", Mode = ModeScriptingLog },
            new LogEntries.StubEntry { Message = "[12:00:00] UnityLeanMcp: Refresh complete", Mode = ModeScriptingLog },
            new LogEntries.StubEntry { Message = "User log", Mode = ModeScriptingLog }
        });

        string result = Inspect.Console();

        Assert.Contains("Console entries (1 shown, 1 unique matched of 1 total; 0 error, 0 warning, 1 log):", result);
        Assert.Contains("[Log] User log", result);
        Assert.DoesNotContain("UnityLeanMcp:", result);
    }

    [Fact]
    public void Console_MaxItemsReturnsMostRecentTailInChronologicalOrderWithTruncationMarker()
    {
        using var scope = new UnityInspectTestScope();
        for (int i = 1; i <= 5; i++)
        {
            LogEntries.Entries.Add(new LogEntries.StubEntry
            {
                Message = $"Entry {i}",
                Mode = ModeScriptingLog
            });
        }

        string result = Inspect.Console(maxItems: 2);

        Assert.Contains("Console entries (2 shown, 5 unique matched of 5 total; 0 error, 0 warning, 5 log):", result);
        Assert.DoesNotContain("Entry 1", result);
        Assert.DoesNotContain("Entry 2", result);
        Assert.DoesNotContain("Entry 3", result);
        int idx4 = result.IndexOf("[Log] Entry 4", StringComparison.Ordinal);
        int idx5 = result.IndexOf("[Log] Entry 5", StringComparison.Ordinal);
        Assert.True(idx4 >= 0 && idx5 > idx4);
        Assert.Contains(Inspect.ItemTruncationMarker, result);
    }

    [Fact]
    public void Console_DoesNotDuplicateInlineFileLocationForCompilerErrors()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.Add(new LogEntries.StubEntry
        {
            Message = "Assets/Scripts/Broken.cs(12,5): error CS1002: ; expected",
            File = "Assets/Scripts/Broken.cs",
            Line = 12,
            Mode = ModeScriptCompileError
        });

        string result = Inspect.Console();

        Assert.Contains("[Error] Assets/Scripts/Broken.cs(12,5): error CS1002: ; expected", result);
        Assert.DoesNotContain("(Assets/Scripts/Broken.cs:12)", result);
    }

    [Fact]
    public void Console_WhenGetEntryThrows_StillCallsEndGettingEntries()
    {
        using var scope = new UnityInspectTestScope();
        LogEntries.Entries.Add(new LogEntries.StubEntry { Message = "Boom", Mode = ModeError });
        LogEntries.ThrowOnGetEntry = true;

        string result = Inspect.Console();

        Assert.StartsWith("(Unity Console inspection failed:", result);
        Assert.Equal(1, LogEntries.StartGettingEntriesCallCount);
        Assert.Equal(1, LogEntries.EndGettingEntriesCallCount);
    }
}
