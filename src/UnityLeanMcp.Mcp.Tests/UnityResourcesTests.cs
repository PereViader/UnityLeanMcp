using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
public class UnityResourcesTests
{
    private static readonly string[] ExpectedCategoryOrder =
    [
        "Scene & Hierarchy",
        "Objects & Components",
        "Assets & Prefabs",
        "Visual Capture",
        "Editor & Tests"
    ];

    [Fact]
    public void InspectType_DoesNotExposeHelpMethod()
    {
        var helpMethod = typeof(Inspect).GetMethod(
            "Help",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        Assert.Null(helpMethod);
    }

    [Fact]
    public void InspectHelpCatalog_MatchesAllPublicStaticInspectMethodsAndCategories()
    {
        var actualByCategory = ExpectedCategoryOrder.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);

        var methods = typeof(Inspect)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName);

        foreach (var method in methods)
        {
            string category = GetCategoryForMethod(method.Name);
            actualByCategory[category].Add(FormatMethodSignature(method));
        }

        var catalogByCategory = ParseSignatureReferenceByCategory(InspectHelpCatalog.SignatureReference);

        Assert.Equal(ExpectedCategoryOrder, catalogByCategory.Keys.ToArray());

        foreach (string category in ExpectedCategoryOrder)
        {
            var expectedSorted = actualByCategory[category].OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var catalogSorted = catalogByCategory[category].OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedSorted, catalogSorted);
        }
    }

    [Fact]
    public void GetInspectHelp_IsBoundedAndExcludesHelp()
    {
        var resources = new UnityResources();
        string helpText = resources.GetInspectHelp();

        Assert.False(string.IsNullOrWhiteSpace(helpText));
        Assert.Equal(InspectHelpCatalog.FullHelpText, helpText);
        Assert.DoesNotContain("Help()", helpText);
        Assert.DoesNotContain("Utilities:", helpText);
        Assert.Contains("Usage in unity_eval:", helpText);
        Assert.True(helpText.Length <= McpOutputLimits.MaxFormattedOutputCharacters);
        Assert.True(Encoding.UTF8.GetByteCount(helpText) <= McpOutputLimits.MaxFormattedOutputBytes);
    }

    private static string GetCategoryForMethod(string methodName) => methodName switch
    {
        nameof(Inspect.Hierarchy) or
        nameof(Inspect.Find) or
        nameof(Inspect.FindByComponent) or
        nameof(Inspect.MissingScripts) or
        nameof(Inspect.SceneState) or
        nameof(Inspect.PrefabStage) or
        nameof(Inspect.UIDocument) => "Scene & Hierarchy",

        nameof(Inspect.GameObject) or
        nameof(Inspect.Component) or
        nameof(Inspect.Object) or
        nameof(Inspect.PrefabOverrides) => "Objects & Components",

        nameof(Inspect.FindAsset) or
        nameof(Inspect.FindAssets) or
        nameof(Inspect.FindPrefabs) or
        nameof(Inspect.Dependencies) or
        nameof(Inspect.Referencers) => "Assets & Prefabs",

        nameof(Inspect.CaptureCamera) or
        nameof(Inspect.CaptureGameView) or
        nameof(Inspect.CaptureSceneView) or
        nameof(Inspect.CaptureTarget) => "Visual Capture",

        nameof(Inspect.Selection) or
        nameof(Inspect.MainCamera) or
        nameof(Inspect.Tests) => "Editor & Tests",

        _ => throw new InvalidOperationException($"Uncategorized public static Inspect method: {methodName}")
    };

    private static string FormatMethodSignature(MethodInfo method)
    {
        string returnType = FormatTypeName(method.ReturnType);
        var paramStrings = new List<string>();
        foreach (var p in method.GetParameters())
        {
            string paramType = FormatTypeName(p.ParameterType);
            string paramText = $"{paramType} {p.Name}";
            if (p.HasDefaultValue)
            {
                string defaultValue = p.DefaultValue switch
                {
                    null when p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null => "default",
                    null => "null",
                    bool b => b ? "true" : "false",
                    string s => $"\"{s}\"",
                    _ => Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) ?? "null"
                };
                paramText += $" = {defaultValue}";
            }
            paramStrings.Add(paramText);
        }

        return $"• {returnType} {method.Name}({string.Join(", ", paramStrings)})";
    }

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type.IsArray) return FormatTypeName(type.GetElementType()!) + "[]";

        var underlyingNullable = Nullable.GetUnderlyingType(type);
        if (underlyingNullable != null) return FormatTypeName(underlyingNullable) + "?";

        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(object)) return "object";
        if (type.IsGenericType)
        {
            string genericName = type.Name;
            int tickIndex = genericName.IndexOf('`');
            if (tickIndex >= 0)
            {
                genericName = genericName.Substring(0, tickIndex);
            }

            string typeArgs = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{genericName}<{typeArgs}>";
        }
        return type.Name;
    }

    private static Dictionary<string, List<string>> ParseSignatureReferenceByCategory(string signatureReference)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string[] lines = signatureReference.Split('\n');
        Assert.True(lines.Length > 0);
        Assert.Equal("UnityLeanMcp Inspection Helpers (UnityLeanMcp.Inspect):", lines[0]);

        string? currentCategory = null;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.EndsWith(':'))
            {
                currentCategory = line[..^1];
                Assert.False(result.ContainsKey(currentCategory), $"Duplicate category header '{currentCategory}'.");
                result[currentCategory] = new List<string>();
                continue;
            }

            Assert.NotNull(currentCategory);
            Assert.StartsWith("• ", line);
            result[currentCategory!].Add(line);
        }

        return result;
    }
}
