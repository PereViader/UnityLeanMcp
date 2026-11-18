using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityLeanMcp;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
public sealed class UnityInspectObjectAndAssetTests : IDisposable
{
    private readonly UnityInspectTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Inspect_Object_Null_ReturnsNullString()
    {
        string result = Inspect.Object(null);
        Assert.Equal("null", result);
    }

    [Fact]
    public void Inspect_GameObject_ListsPathActiveTagLayerComponentsAndTransform()
    {
        var parent = new GameObject("Parent");
        var go = new GameObject("Hero");
        parent.transform.AddChild(go.transform);
        go.tag = "Player";
        go.layer = 2;
        go.AddComponent<Collider>();

        string result = Inspect.GameObject(go);

        Assert.Contains("GameObject: \"Hero\"", result);
        Assert.Contains("Active: true (in hierarchy: true)", result);
        Assert.Contains("Tag: \"Player\"", result);
        Assert.Contains("Layer: 2 (Layer2)", result);
        Assert.Contains("Path: /Parent/Hero", result);
        Assert.Contains("Collider", result);
        Assert.Contains("Transform", result);
    }

    [Fact]
    public void Inspect_GameObject_WithMissingComponent_ReportsGracefully()
    {
        var go = new GameObject("ObjectWithMissingScript");
        go.AddRawComponent(null);

        string result = Inspect.GameObject(go);

        Assert.Contains("GameObject: \"ObjectWithMissingScript\"", result);
        Assert.Contains("Missing Component / Unassigned Script", result);
    }

    [Fact]
    public void Inspect_Component_ListsPropertiesAndValues()
    {
        var go = new GameObject("TestGo");
        var collider = go.AddComponent<Collider>();
        collider.enabled = true;

        string result = Inspect.Component(collider);

        Assert.Contains("Component: Collider", result);
        Assert.Contains("GameObject: \"TestGo\"", result);
        Assert.Contains("Enabled: true", result);
    }

    [Fact]
    public void Inspect_Component_WithSerializedProperties_ListsValuesAndAssetReferences()
    {
        var go = new GameObject("TestEnemy");
        var comp = go.AddComponent<Collider>();

        var referencedAsset = new UnityEngine.Object { name = "EnemyMaterial" };
        AssetDatabase.GetAssetPathFunc = obj => obj == referencedAsset ? "Assets/Materials/Enemy.mat" : "";

        SerializedObject.CustomProperties[comp] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_Health",
                displayName = "Health",
                propertyType = SerializedPropertyType.Integer,
                intValue = 100
            },
            new SerializedProperty
            {
                name = "m_Material",
                displayName = "Material",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = referencedAsset
            }
        };

        string result = Inspect.Object(comp);

        Assert.Contains("Component: Collider", result);
        Assert.Contains("Health (m_Health): 100", result);
        Assert.Contains("Material (m_Material): \"EnemyMaterial\" (Object) [Asset: \"Assets/Materials/Enemy.mat\"]", result);
    }

    [Fact]
    public void Inspect_GenericObject_CycleDetected_AppendsCycleTruncationMarker()
    {
        var nodeA = new CircularNode { Name = "NodeA" };
        var nodeB = new CircularNode { Name = "NodeB" };
        nodeA.Next = nodeB;
        nodeB.Next = nodeA;

        string result = Inspect.Object(nodeA, maxDepth: 5);

        Assert.Contains("NodeA", result);
        Assert.Contains("NodeB", result);
        Assert.Contains("... (truncated: cycle detected)", result);
    }

    private class CircularNode
    {
        public string Name { get; set; } = "";
        public CircularNode? Next { get; set; }
    }

    [Fact]
    public void Inspect_Object_ItemLimitExceeded_AppendsItemMarker()
    {
        var multiProp = new MultiPropertyClass();
        string result = Inspect.Object(multiProp, maxItems: 3);

        Assert.Contains("... (truncated: maximum item count reached)", result);
    }

    private class MultiPropertyClass
    {
        public int Prop1 { get; set; } = 1;
        public int Prop2 { get; set; } = 2;
        public int Prop3 { get; set; } = 3;
        public int Prop4 { get; set; } = 4;
        public int Prop5 { get; set; } = 5;
    }

    private class CustomTestComponent : Component
    {
        public int PublicField = 42;
        [NonSerialized] public string DebugStatus = "Active";
        public string SafeProperty => "SafeValue";
        public string material => throw new InvalidOperationException("Material getter should not be called in EditMode!");
        public string mesh => throw new InvalidOperationException("Mesh getter should not be called in EditMode!");
        public string profile => throw new InvalidOperationException("Profile getter should not be called in EditMode!");
        public string materialTemplate => throw new InvalidOperationException("MaterialTemplate getter should not be called in EditMode!");
        public string coefficients => throw new InvalidOperationException("Coefficients getter should not be called in EditMode!");
        [Obsolete] public string DeprecatedProp => throw new InvalidOperationException("Deprecated property should not be called!");
        [Obsolete] public int DeprecatedField = 999;
    }

    [Fact]
    public void Inspect_Component_ListsPublicFieldsAndIgnoresMutatingAndObsoleteMembers()
    {
        var go = new GameObject("Owner");
        var customComp = new CustomTestComponent { gameObject = go };
        go.AddRawComponent(customComp);

        string result = Inspect.Component(customComp);

        Assert.Contains("PublicField: 42", result);
        Assert.Contains("DebugStatus: \"Active\"", result);
        Assert.Contains("SafeProperty: \"SafeValue\"", result);
        Assert.DoesNotContain("Material getter", result);
        Assert.DoesNotContain("Mesh getter", result);
        Assert.DoesNotContain("Profile getter", result);
        Assert.DoesNotContain("MaterialTemplate getter", result);
        Assert.DoesNotContain("Coefficients getter", result);
        Assert.DoesNotContain("DeprecatedProp", result);
        Assert.DoesNotContain("DeprecatedField", result);
    }

    [Fact]
    public void Inspect_Object_OnComponent_IgnoresMutatingAndObsoleteMembers()
    {
        var go = new GameObject("Owner");
        var customComp = new CustomTestComponent { gameObject = go };
        go.AddRawComponent(customComp);

        string result = Inspect.Object(customComp);

        Assert.Contains("PublicField: 42", result);
        Assert.Contains("DebugStatus: \"Active\"", result);
        Assert.Contains("SafeProperty: \"SafeValue\"", result);
        Assert.DoesNotContain("Material getter", result);
        Assert.DoesNotContain("Mesh getter", result);
        Assert.DoesNotContain("Profile getter", result);
        Assert.DoesNotContain("MaterialTemplate getter", result);
        Assert.DoesNotContain("Coefficients getter", result);
        Assert.DoesNotContain("DeprecatedProp", result);
        Assert.DoesNotContain("DeprecatedField", result);
    }

    private class CustomTestObject
    {
        public int PublicField = 42;
        public string SafeProperty => "SafeValue";
        public string Material => "Steel";
        public string Mesh => "CubeMesh";
        public string Profile => "HighQuality";
        public string MaterialTemplate => "TerrainLit";
        public string Coefficients => "ClothWeights";
        [Obsolete] public string DeprecatedProp => throw new InvalidOperationException("Deprecated property should not be called!");
    }

    [Fact]
    public void Inspect_Object_OnPlainObject_PreservesComponentNamedPropertiesAndIgnoresObsoleteMembers()
    {
        var obj = new CustomTestObject();
        string result = Inspect.Object(obj);

        Assert.Contains("PublicField: 42", result);
        Assert.Contains("SafeProperty: \"SafeValue\"", result);
        Assert.Contains("Material: \"Steel\"", result);
        Assert.Contains("Mesh: \"CubeMesh\"", result);
        Assert.Contains("Profile: \"HighQuality\"", result);
        Assert.Contains("MaterialTemplate: \"TerrainLit\"", result);
        Assert.Contains("Coefficients: \"ClothWeights\"", result);
        Assert.DoesNotContain("DeprecatedProp", result);
    }

    [Fact]
    public void Inspect_ComponentAndUnityObject_DeterministicallyDisposesSerializedObject()
    {
        SerializedObject.DisposeCount = 0;

        var go = new GameObject("Owner");
        var collider = go.AddComponent<Collider>();
        Inspect.Component(collider);
        Assert.Equal(1, SerializedObject.DisposeCount);

        var asset = new UnityEngine.Object { name = "SomeAsset" };
        Inspect.Object(asset);
        Assert.Equal(2, SerializedObject.DisposeCount);
    }

    [Fact]
    public void Inspect_TransformComponent_DisplaysPositionRotationScaleAndChildren()
    {
        var go = new GameObject("Target");
        go.transform.localPosition = new Vector3(1, 2, 3);
        go.transform.localEulerAngles = new Vector3(0, 90, 0);
        go.transform.localScale = new Vector3(2, 2, 2);
        var child = new GameObject("Child");
        go.transform.AddChild(child.transform);

        string result = Inspect.Component(go.transform);

        Assert.Contains("Component: Transform", result);
        Assert.Contains("localPos=(1.00, 2.00, 3.00)", result);
        Assert.Contains("localRot=(0.00, 90.00, 0.00)", result);
        Assert.Contains("localScale=(2.00, 2.00, 2.00)", result);
        Assert.Contains("children=1", result);
    }

    [Fact]
    public void Inspect_Collections_FormatsArrayAndDictionaryItems()
    {
        var list = new List<string> { "Apple", "Banana", "Cherry" };
        string listResult = Inspect.Object(list);
        Assert.Contains("List`1 (3 items):", listResult);
        Assert.Contains("[0]: \"Apple\"", listResult);
        Assert.Contains("[1]: \"Banana\"", listResult);
        Assert.Contains("[2]: \"Cherry\"", listResult);

        var dict = new Dictionary<string, int> { ["Coins"] = 100, ["Gems"] = 5 };
        string dictResult = Inspect.Object(dict);
        Assert.Contains("Dictionary`2 (2 entries):", dictResult);
        Assert.Contains("[Coins]: 100", dictResult);
        Assert.Contains("[Gems]: 5", dictResult);
    }

    [Fact]
    public void Inspect_Component_WithArraySerializedProperty_FormatsItemCount()
    {
        var go = new GameObject("GoWithArray");
        var comp = go.AddComponent<Collider>();

        SerializedObject.CustomProperties[comp] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_Points",
                displayName = "Points",
                propertyType = SerializedPropertyType.Generic,
                isArray = true,
                arraySize = 12
            }
        };

        string result = Inspect.Component(comp);
        Assert.Contains("Points (m_Points): [12 items]", result);
    }

    [Fact]
    public void Inspect_Object_DirectLeafTypes_FormatsDirectlyWithoutReflection()
    {
        var vec = new Vector3(1.5f, 2.5f, 3.5f);
        string vecResult = Inspect.Object(vec);
        Assert.Equal("(1.50, 2.50, 3.50)", vecResult.Trim());

        string typeResult = Inspect.Object(typeof(Camera));
        Assert.Equal("Type: UnityEngine.Camera", typeResult.Trim());

        Action myAction = () => { };
        string delResult = Inspect.Object(myAction);
        Assert.Contains("Delegate:", delResult);
    }

    private class CustomDataWithLeaves
    {
        public Vector3 Position = new Vector3(10, 20, 30);
        public Color Tint = new Color(1, 0, 0, 1);
        public Type ComponentType = typeof(Transform);
        public Action? Callback = () => { };
        public DateTime Timestamp = new DateTime(2026, 1, 1);
    }

    [Fact]
    public void Inspect_GenericObject_WithLeafFields_FormatsInlineWithoutContextBloat()
    {
        var data = new CustomDataWithLeaves();
        string result = Inspect.Object(data, maxDepth: 2);

        Assert.Contains("Position: (10.00, 20.00, 30.00)", result);
        Assert.Contains("Tint: RGBA(1.000, 0.000, 0.000, 1.000)", result);
        Assert.Contains("ComponentType: typeof(UnityEngine.Transform)", result);
        Assert.Contains("Timestamp:", result);
        Assert.DoesNotContain("... (truncated: maximum depth reached)", result);
    }

    private class ComponentWithDuplicateProps : Component
    {
        public int Health { get; set; } = 100;
        public string Description { get; set; } = "Warrior";
        public float Speed => 4.5f;
    }

    [Fact]
    public void Inspect_Component_DeduplicatesSerializedPropertiesAndPublicProperties()
    {
        var go = new GameObject("PlayerWithProps");
        var comp = new ComponentWithDuplicateProps { gameObject = go };
        go.AddRawComponent(comp);

        SerializedObject.CustomProperties[comp] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_Health",
                displayName = "Health",
                propertyType = SerializedPropertyType.Integer,
                intValue = 100
            }
        };

        string result = Inspect.Component(comp);

        Assert.Contains("Health (m_Health): 100", result);
        Assert.DoesNotContain("  Health: 100", result);
        Assert.DoesNotContain("  health: 100", result);
        Assert.Contains("Speed: 4.5", result);
    }

    [Fact]
    public void FindAssets_ByNameAndType_ReturnsNormalizedProjectRelativePaths()
    {
        AssetDatabase.FindAssetsFunc = filter =>
        {
            if (filter.Contains("Player") && filter.Contains("t:Prefab"))
            {
                return new[] { "guid_player_prefab" };
            }
            return Array.Empty<string>();
        };
        AssetDatabase.GUIDToAssetPathFunc = guid =>
        {
            if (guid == "guid_player_prefab") return "Assets\\Prefabs\\Player.prefab";
            return "";
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(GameObject);

        string result = Inspect.FindAssets(filter: "Player", type: "Prefab");

        Assert.Contains("Assets found (1):", result);
        Assert.Contains("Assets/Prefabs/Player.prefab [GameObject]", result);
        Assert.DoesNotContain("\\", result);
    }

    [Fact]
    public void FindPrefabs_CallsFindAssetsWithPrefabType()
    {
        AssetDatabase.FindAssetsFunc = filter =>
        {
            if (filter.Contains("t:Prefab"))
            {
                return new[] { "guid1" };
            }
            return Array.Empty<string>();
        };
        AssetDatabase.GUIDToAssetPathFunc = guid => "Assets/Prefabs/Enemy.prefab";
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(GameObject);

        string result = Inspect.FindPrefabs("Enemy");

        Assert.Contains("Assets/Prefabs/Enemy.prefab [GameObject]", result);
    }

    [Fact]
    public void FindAssets_NoMatches_ReturnsCleanNotification()
    {
        AssetDatabase.FindAssetsFunc = filter => Array.Empty<string>();

        string result = Inspect.FindAssets("NonExistentFilter");

        Assert.Contains("(No assets found matching query: 'NonExistentFilter')", result);
    }

    [Fact]
    public void FindAssets_ItemLimitExceeded_AppendsItemTruncationMarker()
    {
        AssetDatabase.FindAssetsFunc = filter => new[] { "g1", "g2", "g3", "g4", "g5" };
        AssetDatabase.GUIDToAssetPathFunc = guid => $"Assets/Item_{guid}.asset";
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(UnityEngine.Object);

        string result = Inspect.FindAssets(maxItems: 2);

        Assert.Contains("Assets/Item_g1.asset", result);
        Assert.Contains("Assets/Item_g2.asset", result);
        Assert.Contains("... (truncated: maximum item count reached)", result);
        Assert.DoesNotContain("Assets/Item_g3.asset", result);
    }

    [Fact]
    public void FindAsset_ByPath_ReturnsDetailedMetadata()
    {
        string result = Inspect.FindAsset("Assets/Prefabs/Test.prefab");

        Assert.Contains("Asset: \"Test\"", result);
        Assert.Contains("Path: \"Assets/Prefabs/Test.prefab\"", result);
        Assert.Contains("GUID: test_guid_", result);
    }

    [Fact]
    public void FindAsset_WhenExceptionOccurs_ReturnsGracefulNotification()
    {
        AssetDatabase.FindAssetsFunc = _ => throw new InvalidOperationException("Asset database is busy");

        string result = Inspect.FindAsset("MissingAsset");
        Assert.Contains("Asset search failed: Asset database is busy", result);
    }

    [Fact]
    public void FindAsset_WhenFirstGuidHasEmptyPath_InspectsSubsequentGuids()
    {
        AssetDatabase.FindAssetsFunc = _ => new[] { "stale_guid", "valid_guid" };
        AssetDatabase.GUIDToAssetPathFunc = g => g == "valid_guid" ? "Assets/Valid.prefab" : "";

        string result = Inspect.FindAsset("TestAsset");
        Assert.Contains("Assets/Valid.prefab", result);
    }

    [Fact]
    public void FindAssets_WhenAllGuidsHaveEmptyPath_ReturnsCleanNotification()
    {
        AssetDatabase.FindAssetsFunc = _ => new[] { "stale_1", "stale_2" };
        AssetDatabase.GUIDToAssetPathFunc = _ => "";

        string result = Inspect.FindAssets("TestQuery");
        Assert.Contains("(No assets found matching query: 'TestQuery')", result);
    }

    [Fact]
    public void Dependencies_NullOrWhitespace_ReturnsMissingAssetPathDiagnostic()
    {
        Assert.Equal("(Missing asset path)", Inspect.Dependencies(null));
        Assert.Equal("(Missing asset path)", Inspect.Dependencies(""));
        Assert.Equal("(Missing asset path)", Inspect.Dependencies("   "));
    }

    [Fact]
    public void Dependencies_AssetNotFound_ReturnsDiagnostic()
    {
        AssetDatabase.LoadMainAssetAtPathFunc = _ => null;
        AssetDatabase.AssetPathToGUIDFunc = _ => "";

        string pathResult = Inspect.Dependencies("Assets/Missing.prefab");
        Assert.Equal("(Asset not found at path: \"Assets/Missing.prefab\")", pathResult);

        AssetDatabase.FindAssetsFunc = _ => Array.Empty<string>();
        string nameResult = Inspect.Dependencies("MissingName");
        Assert.Equal("(Asset not found matching: \"MissingName\")", nameResult);
    }

    [Fact]
    public void Dependencies_PathNormalization_HandlesBackslashesAndPrefixes()
    {
        AssetDatabase.GetDependenciesFunc = (path, recursive) =>
        {
            if (path == "Assets/Prefabs/Player.prefab")
            {
                return new[] { "Assets\\Materials\\PlayerMat.mat", "Assets/Prefabs/Player.prefab" };
            }
            return Array.Empty<string>();
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(UnityEngine.Material);

        string r1 = Inspect.Dependencies("Assets\\Prefabs\\Player.prefab");
        string r2 = Inspect.Dependencies("./Assets/Prefabs/Player.prefab");
        string r3 = Inspect.Dependencies("/Assets/Prefabs/Player.prefab");

        Assert.Contains("Dependencies found (1):", r1);
        Assert.Contains("Assets/Materials/PlayerMat.mat [Material]", r1);
        Assert.DoesNotContain("\\", r1);
        Assert.DoesNotContain("Assets/Prefabs/Player.prefab [", r1);

        Assert.Equal(r1, r2);
        Assert.Equal(r1, r3);
    }

    [Fact]
    public void Dependencies_ExcludesSelf_AndDeduplicatesAndSorts()
    {
        AssetDatabase.GetDependenciesFunc = (path, recursive) => new[]
        {
            "Assets/Prefabs/Player.prefab",
            "Assets/Z_Item.asset",
            "Assets/A_Item.asset",
            "Assets/A_Item.asset"
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(UnityEngine.Object);

        string result = Inspect.Dependencies("Assets/Prefabs/Player.prefab");

        Assert.Contains("Dependencies found (2):", result);
        Assert.DoesNotContain("Player.prefab [", result);
        int idxA = result.IndexOf("Assets/A_Item.asset", StringComparison.Ordinal);
        int idxZ = result.IndexOf("Assets/Z_Item.asset", StringComparison.Ordinal);
        Assert.True(idxA >= 0 && idxZ >= 0 && idxA < idxZ, "Dependencies should be sorted deterministically in ordinal order");
    }

    [Fact]
    public void Dependencies_NoDependencies_ReturnsClearMessage()
    {
        AssetDatabase.GetDependenciesFunc = (path, recursive) => new[] { "Assets/Materials/Plain.mat" };

        string result = Inspect.Dependencies("Assets/Materials/Plain.mat");

        Assert.Equal("(No dependencies found for asset: \"Assets/Materials/Plain.mat\")", result);
    }

    [Fact]
    public void Dependencies_PassesRecursiveParameter()
    {
        bool? passedRecursive = null;
        AssetDatabase.GetDependenciesFunc = (path, recursive) =>
        {
            passedRecursive = recursive;
            return new[] { "Assets/Tex.png" };
        };

        Inspect.Dependencies("Assets/Mat.mat", recursive: true);
        Assert.True(passedRecursive);

        Inspect.Dependencies("Assets/Mat.mat", recursive: false);
        Assert.False(passedRecursive);
    }

    [Fact]
    public void Dependencies_ItemTruncation_AppendsMarker()
    {
        AssetDatabase.GetDependenciesFunc = (path, recursive) => new[]
        {
            "Assets/Dep1.asset",
            "Assets/Dep2.asset",
            "Assets/Dep3.asset",
            "Assets/Dep4.asset"
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(UnityEngine.Object);

        string result = Inspect.Dependencies("Assets/Root.asset", maxItems: 2);

        Assert.Contains("Dependencies found (4):", result);
        Assert.Contains("Assets/Dep1.asset", result);
        Assert.Contains("Assets/Dep2.asset", result);
        Assert.Contains("... (truncated: maximum item count reached)", result);
        Assert.DoesNotContain("Assets/Dep3.asset", result);
    }

    [Fact]
    public void Referencers_NullOrWhitespace_ReturnsMissingTargetAssetPathDiagnostic()
    {
        Assert.Equal("(Missing target asset path)", Inspect.Referencers(null));
        Assert.Equal("(Missing target asset path)", Inspect.Referencers(""));
        Assert.Equal("(Missing target asset path)", Inspect.Referencers("   "));
    }

    [Fact]
    public void Referencers_TargetAssetNotFound_ReturnsDiagnostic()
    {
        AssetDatabase.LoadMainAssetAtPathFunc = _ => null;
        AssetDatabase.AssetPathToGUIDFunc = _ => "";

        string pathResult = Inspect.Referencers("Assets/Missing.prefab");
        Assert.Equal("(Asset not found at path: \"Assets/Missing.prefab\")", pathResult);

        AssetDatabase.FindAssetsFunc = _ => Array.Empty<string>();
        string nameResult = Inspect.Referencers("MissingName");
        Assert.Equal("(Asset not found matching: \"MissingName\")", nameResult);
    }

    [Fact]
    public void Referencers_FolderNotFound_ReturnsDiagnostic()
    {
        AssetDatabase.IsValidFolderFunc = path => path != "Assets/NonExistent";

        string result = Inspect.Referencers("Assets/Mat.mat", searchFolder: "Assets/NonExistent");

        Assert.Equal("(Folder not found: \"Assets/NonExistent\")", result);
    }

    [Fact]
    public void Referencers_FindsReferencersInFolder_WithNormalizedPathsAndTypes()
    {
        AssetDatabase.FindAssetsInFoldersFunc = (filter, folders) => new[] { "g_hero", "g_main", "g_other" };
        AssetDatabase.GUIDToAssetPathFunc = guid =>
        {
            switch (guid)
            {
                case "g_hero": return "Assets\\Prefabs\\Hero.prefab";
                case "g_main": return "Assets/Scenes/Main.unity";
                case "g_other": return "Assets/Prefabs/Other.prefab";
                default: return "";
            }
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path =>
        {
            if (path.EndsWith(".prefab")) return typeof(GameObject);
            if (path.EndsWith(".unity")) return typeof(UnityEngine.SceneManagement.Scene);
            return typeof(UnityEngine.Object);
        };
        AssetDatabase.GetDependenciesFunc = (path, recursive) =>
        {
            if (path == "Assets/Prefabs/Hero.prefab")
            {
                return new[] { "Assets\\Materials\\Target.mat" };
            }
            if (path == "Assets/Scenes/Main.unity")
            {
                return new[] { "Assets/Materials/Target.mat" };
            }
            return new[] { "Assets/Materials/Other.mat" };
        };

        string result = Inspect.Referencers("Assets\\Materials\\Target.mat", searchFolder: "Assets");

        Assert.Contains("Referencers found (2):", result);
        Assert.Contains("Assets/Prefabs/Hero.prefab [GameObject]", result);
        Assert.Contains("Assets/Scenes/Main.unity [Scene]", result);
        Assert.DoesNotContain("Other.prefab", result);
        Assert.DoesNotContain("\\", result);
    }

    [Fact]
    public void Referencers_ExcludesSelf()
    {
        AssetDatabase.FindAssetsInFoldersFunc = (filter, folders) => new[] { "g_target", "g_ref" };
        AssetDatabase.GUIDToAssetPathFunc = guid => guid == "g_target" ? "Assets/Target.prefab" : "Assets/Ref.prefab";
        AssetDatabase.GetDependenciesFunc = (path, recursive) =>
        {
            return new[] { "Assets/Target.prefab" };
        };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(GameObject);

        string result = Inspect.Referencers("Assets/Target.prefab");

        Assert.Contains("Referencers found (1):", result);
        Assert.Contains("Assets/Ref.prefab", result);
        Assert.DoesNotContain("1. Assets/Target.prefab", result);
    }

    [Fact]
    public void Referencers_NoReferencers_ReturnsClearMessage()
    {
        AssetDatabase.FindAssetsInFoldersFunc = (filter, folders) => new[] { "g_other" };
        AssetDatabase.GUIDToAssetPathFunc = guid => "Assets/Other.prefab";
        AssetDatabase.GetDependenciesFunc = (path, recursive) => new[] { "Assets/SomethingElse.mat" };

        string result = Inspect.Referencers("Assets/Materials/Unused.mat");

        Assert.Equal("(No referencers found for asset: \"Assets/Materials/Unused.mat\")", result);
    }

    [Fact]
    public void Referencers_ItemTruncation_AppendsMarker()
    {
        AssetDatabase.FindAssetsInFoldersFunc = (filter, folders) => new[] { "g1", "g2", "g3", "g4" };
        AssetDatabase.GUIDToAssetPathFunc = guid => $"Assets/Ref_{guid}.prefab";
        AssetDatabase.GetDependenciesFunc = (path, recursive) => new[] { "Assets/Target.mat" };
        AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(GameObject);

        string result = Inspect.Referencers("Assets/Target.mat", maxItems: 2);

        Assert.Contains("Referencers found (4):", result);
        Assert.Contains("Assets/Ref_g1.prefab", result);
        Assert.Contains("Assets/Ref_g2.prefab", result);
        Assert.Contains("... (truncated: maximum item count reached)", result);
        Assert.DoesNotContain("Assets/Ref_g3.prefab", result);
    }
}
