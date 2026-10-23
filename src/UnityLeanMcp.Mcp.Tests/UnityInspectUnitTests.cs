using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityLeanMcp;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests
{
    [Trait("Category", "Unit")]
    public class UnityInspectUnitTests : IDisposable
    {
        private readonly List<string> _createdFiles = new();

        public UnityInspectUnitTests()
        {
            // Reset stubs before each test
            SceneManager.Scenes.Clear();
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = Array.Empty<UnityEngine.Object>();
            Selection.gameObjects = Array.Empty<GameObject>();
            Camera.main = null;
            Camera.AllCamerasList.Clear();
            SceneView.lastActiveSceneView = null;
            SceneView.SceneViewsList.Clear();
            AssetDatabase.FindAssetsFunc = null;
            AssetDatabase.FindAssetsInFoldersFunc = null;
            AssetDatabase.GUIDToAssetPathFunc = null;
            AssetDatabase.GetMainAssetTypeAtPathFunc = null;
            AssetDatabase.GetAssetPathFunc = null;
            AssetDatabase.LoadMainAssetAtPathFunc = null;
            AssetDatabase.AssetPathToGUIDFunc = null;
            AssetDatabase.GetDependenciesFunc = null;
            AssetDatabase.IsValidFolderFunc = null;
            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = null;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = null;
            PrefabUtility.GetPropertyModificationsFunc = null;
            PrefabUtility.GetAddedComponentsFunc = null;
            PrefabUtility.GetRemovedComponentsFunc = null;
            PrefabStageUtility.GetCurrentPrefabStageFunc = null;
            EditorSettings.prefabModeAllowAutoSave = true;
            SerializedObject.CustomProperties.Clear();
        }

        public void Dispose()
        {
            SceneManager.Scenes.Clear();
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = Array.Empty<UnityEngine.Object>();
            Selection.gameObjects = Array.Empty<GameObject>();
            Camera.main = null;
            Camera.AllCamerasList.Clear();
            SceneView.lastActiveSceneView = null;
            SceneView.SceneViewsList.Clear();
            AssetDatabase.FindAssetsFunc = null;
            AssetDatabase.FindAssetsInFoldersFunc = null;
            AssetDatabase.GUIDToAssetPathFunc = null;
            AssetDatabase.GetMainAssetTypeAtPathFunc = null;
            AssetDatabase.GetAssetPathFunc = null;
            AssetDatabase.LoadMainAssetAtPathFunc = null;
            AssetDatabase.AssetPathToGUIDFunc = null;
            AssetDatabase.GetDependenciesFunc = null;
            AssetDatabase.IsValidFolderFunc = null;
            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = null;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = null;
            PrefabUtility.GetPropertyModificationsFunc = null;
            PrefabUtility.GetAddedComponentsFunc = null;
            PrefabUtility.GetRemovedComponentsFunc = null;
            PrefabStageUtility.GetCurrentPrefabStageFunc = null;
            EditorSettings.prefabModeAllowAutoSave = true;
            SerializedObject.CustomProperties.Clear();

            foreach (var file in _createdFiles)
            {
                try
                {
                    if (System.IO.File.Exists(file))
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch { }
            }
            _createdFiles.Clear();
        }

        #region Hierarchy & Scene Inspection Tests

        [Fact]
        public void Hierarchy_EmptyScene_ReturnsCleanEmptyNotification()
        {
            var scene = new Scene { name = "EmptyScene", isLoaded = true, RootObjects = new List<GameObject>() };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();

            Assert.Contains("Scene \"EmptyScene\" (0 root GameObjects - empty scene)", result);
        }

        [Fact]
        public void Hierarchy_NoScenesLoaded_ReturnsCleanNotification()
        {
            string result = Inspect.Hierarchy();

            Assert.Equal("(No scenes currently loaded)", result);
        }

        [Fact]
        public void Hierarchy_SingleSceneWithHierarchy_FormatsIndentedTreeWithActiveStatesAndComponents()
        {
            var rootGo = new GameObject("RootPlayer");
            rootGo.activeSelf = true;
            rootGo.tag = "Player";
            rootGo.AddComponent<Collider>();

            var childGo = new GameObject("Weapon");
            childGo.activeSelf = false;
            rootGo.transform.AddChild(childGo.transform);

            var grandChildGo = new GameObject("Blade");
            grandChildGo.activeSelf = true;
            childGo.transform.AddChild(grandChildGo.transform);

            var scene = new Scene
            {
                name = "MainScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { rootGo }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();

            Assert.Contains("RootPlayer [active] [Transform, Collider]", result);
            Assert.Contains("  Weapon [inactive] [Transform]", result);
            Assert.Contains("    Blade [inactive (parent disabled)] [Transform]", result);
        }

        [Fact]
        public void Hierarchy_DeeplyNestedHierarchy_ExecutesWithoutStackOverflowAndTruncatesCleanly()
        {
            // Build a 100-level hierarchy
            var root = new GameObject("Level0");
            var current = root;
            for (int i = 1; i <= 100; i++)
            {
                var next = new GameObject($"Level{i}");
                current.transform.AddChild(next.transform);
                current = next;
            }

            var scene = new Scene
            {
                name = "DeepScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { root }
            };
            SceneManager.Scenes.Add(scene);

            // Setting maxDepth = 5 should stop traversal and emit truncation marker
            string result = Inspect.Hierarchy(maxDepth: 5, maxItems: 1000);

            Assert.Contains("Level0", result);
            Assert.Contains("Level5", result);
            Assert.Contains("... (truncated: maximum depth reached)", result);
            Assert.DoesNotContain("Level6", result);
        }

        [Fact]
        public void Hierarchy_WithMissingComponent_HandlesGracefullyWithoutThrowing()
        {
            var go = new GameObject("CorruptedObject");
            go.AddRawComponent(null); // Missing script on GameObject
            go.AddComponent<Collider>();

            var scene = new Scene
            {
                name = "TestScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { go }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();

            Assert.Contains("CorruptedObject [active] [Transform, Missing Script, Collider]", result);
        }

        [Fact]
        public void Hierarchy_ItemLimitExceeded_AppendsItemTruncationMarker()
        {
            var root = new GameObject("Root");
            for (int i = 1; i <= 10; i++)
            {
                var child = new GameObject($"Child_{i}");
                root.transform.AddChild(child.transform);
            }

            var scene = new Scene
            {
                name = "TestScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { root }
            };
            SceneManager.Scenes.Add(scene);

            // Limit to 3 items
            string result = Inspect.Hierarchy(maxDepth: 32, maxItems: 3);

            Assert.Contains("Root [active]", result);
            Assert.Contains("Child_1 [active]", result);
            Assert.Contains("Child_2 [active]", result);
            Assert.Contains("... (truncated: maximum item count reached)", result);
            Assert.DoesNotContain("Child_4", result);
        }

        [Fact]
        public void Hierarchy_BySceneName_FindsTargetSceneOrReportsNotLoaded()
        {
            var scene1 = new Scene { name = "SceneA", isLoaded = true, RootObjects = new List<GameObject> { new GameObject("ObjA") } };
            var scene2 = new Scene { name = "SceneB", isLoaded = true, RootObjects = new List<GameObject> { new GameObject("ObjB") } };
            SceneManager.Scenes.Add(scene1);
            SceneManager.Scenes.Add(scene2);

            string resA = Inspect.Hierarchy("SceneA");
            Assert.Contains("ObjA", resA);
            Assert.DoesNotContain("ObjB", resA);

            string resMissing = Inspect.Hierarchy("SceneNonExistent");
            Assert.Contains("Scene \"SceneNonExistent\" is not currently loaded", resMissing);
        }

        [Fact]
        public void Hierarchy_SpecificRoot_OnlyInspectsThatSubtree()
        {
            var root = new GameObject("SpecialRoot");
            var child = new GameObject("SpecialChild");
            root.transform.AddChild(child.transform);

            string result = Inspect.Hierarchy(root);

            Assert.Contains("SpecialRoot [active]", result);
            Assert.Contains("  SpecialChild [active]", result);
        }

        [Fact]
        public void PrefabStage_WhenNoStageOpen_ReturnsCleanNotification()
        {
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => null;

            string result = Inspect.PrefabStage();

            Assert.Equal("(No active Prefab Stage open in Editor)", result);
        }

        [Fact]
        public void PrefabStage_WhenStageOpen_ReturnsMetadataAndHierarchy()
        {
            var rootGo = new GameObject("Hero");
            rootGo.activeSelf = true;
            rootGo.AddComponent<Collider>();

            var childGo = new GameObject("Shield");
            childGo.activeSelf = true;
            rootGo.transform.AddChild(childGo.transform);

            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                mode = PrefabStage.Mode.InIsolation,
                prefabContentsRoot = rootGo,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string result = Inspect.PrefabStage();

            Assert.Contains("Active Prefab Stage:", result);
            Assert.Contains("Asset Path: Assets/Prefabs/Hero.prefab", result);
            Assert.Contains("Mode: InIsolation", result);
            Assert.Contains("Root GameObject: \"Hero\"", result);
            Assert.Contains("Is Dirty: false", result);
            Assert.Contains("Auto Save: true", result);
            Assert.Contains("Hierarchy:", result);
            Assert.Contains("Hero [active] [Transform, Collider]", result);
            Assert.Contains("Shield [active] [Transform]", result);
        }

        [Fact]
        public void PrefabStage_WhenDirtyAndAutoSaveFalse_ReportsCorrectFlags()
        {
            var rootGo = new GameObject("Enemy");
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Enemy.prefab",
                mode = PrefabStage.Mode.InContext,
                prefabContentsRoot = rootGo,
                scene = new Scene { isDirty = true },
                autoSave = false
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string result = Inspect.PrefabStage();

            Assert.Contains("Mode: InContext", result);
            Assert.Contains("Is Dirty: true", result);
            Assert.Contains("Auto Save: false", result);
        }

        [Fact]
        public void PrefabStage_WhenRootNull_ReportsNoRootGameObject()
        {
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Corrupt.prefab",
                mode = PrefabStage.Mode.InIsolation,
                prefabContentsRoot = null,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string result = Inspect.PrefabStage();

            Assert.Contains("Root GameObject: \"(null)\"", result);
            Assert.Contains("Hierarchy: (No root GameObject)", result);
        }

        [Fact]
        public void SceneState_WhenPrefabStageOpen_IncludesPrefabStageSectionAndScenes()
        {
            var rootGo = new GameObject("Hero");
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                mode = PrefabStage.Mode.InIsolation,
                prefabContentsRoot = rootGo,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            var scene = new Scene
            {
                handle = 1,
                name = "MainScene",
                path = "Assets/Scenes/Main.unity",
                isLoaded = true,
                isDirty = false,
                RootObjects = new List<GameObject> { new GameObject("Ground") }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.SceneState();

            Assert.Contains("Active Prefab Stage:", result);
            Assert.Contains("Asset Path: Assets/Prefabs/Hero.prefab", result);
            Assert.Contains("Root GameObject: \"Hero\"", result);
            Assert.Contains("Active Scene: \"MainScene\"", result);
            Assert.Contains("Loaded Scenes (1):", result);
        }

        [Fact]
        public void SceneState_WhenPrefabStageOpenAndNoScenesLoaded_ReportsPrefabStageMetadata()
        {
            var rootGo = new GameObject("Hero");
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                mode = PrefabStage.Mode.InIsolation,
                prefabContentsRoot = rootGo,
                scene = new Scene { isDirty = true },
                autoSave = false
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string result = Inspect.SceneState();

            Assert.Contains("Active Prefab Stage:", result);
            Assert.Contains("Asset Path: Assets/Prefabs/Hero.prefab", result);
            Assert.Contains("Is Dirty: true", result);
            Assert.Contains("Auto Save: false", result);
            Assert.DoesNotContain("Loaded Scenes", result);
        }

        [Fact]
        public void Hierarchy_WhenPrefabStageOpen_IncludesPrefabStageHierarchy()
        {
            var heroGo = new GameObject("Hero");
            var weaponGo = new GameObject("Sword");
            heroGo.transform.AddChild(weaponGo.transform);

            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                prefabContentsRoot = heroGo,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            var sceneGo = new GameObject("Environment");
            var scene = new Scene
            {
                name = "MainScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { sceneGo }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();

            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", result);
            Assert.Contains("Hero [active] [Transform]", result);
            Assert.Contains("  Sword [active] [Transform]", result);
            Assert.Contains("=== Scene: \"MainScene\" (roots: 1) ===", result);
            Assert.Contains("Environment [active] [Transform]", result);
        }

        [Fact]
        public void Hierarchy_WhenPrefabStageOpenWithNoScenes_ReturnsPrefabStageHierarchyOnly()
        {
            var heroGo = new GameObject("Hero");
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                prefabContentsRoot = heroGo,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string result = Inspect.Hierarchy();

            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", result);
            Assert.Contains("Hero [active] [Transform]", result);
            Assert.DoesNotContain("=== Scene:", result);
        }

        [Fact]
        public void Hierarchy_ByPrefabStageNameOrAssetPath_MatchesActivePrefabStage()
        {
            var heroGo = new GameObject("Hero");
            var stage = new PrefabStage
            {
                assetPath = "Assets/Prefabs/Hero.prefab",
                prefabContentsRoot = heroGo,
                scene = new Scene { isDirty = false },
                autoSave = true
            };
            PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

            string resByName = Inspect.Hierarchy("Hero");
            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", resByName);

            string resByFile = Inspect.Hierarchy("Hero.prefab");
            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", resByFile);

            string resByPath = Inspect.Hierarchy("Assets/Prefabs/Hero.prefab");
            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", resByPath);

            string resByKeyword = Inspect.Hierarchy("Prefab");
            Assert.Contains("=== Prefab Stage: \"Hero\" (Asset: \"Assets/Prefabs/Hero.prefab\") ===", resByKeyword);
        }

        [Fact]
        public void InspectHelp_IncludesPrefabStageUnderSceneAndHierarchy()
        {
            string help = Inspect.Help();

            Assert.Contains("Scene & Hierarchy:", help);
            Assert.Contains("• string PrefabStage()", help);
            Assert.Contains("• string PrefabStage(int maxDepth, int maxItems = 100)", help);
        }

        #endregion

        #region Object & Component Inspection Tests

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

            // Inspect object
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

        #endregion

        #region Asset & Prefab Search Tests

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
            // Verify path is normalized with forward slashes
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
                // Target reports target in its dependencies, ref also reports target
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

        #endregion

        #region Selection & Main Camera Tests

        [Fact]
        public void Selection_WhenEmpty_ReturnsClearEmptyNotification()
        {
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = Array.Empty<UnityEngine.Object>();

            string result = Inspect.Selection();

            Assert.Equal("(No active selection in Editor)", result);
        }

        [Fact]
        public void Selection_WithActiveGameObject_ReturnsDetailsTransformAndComponents()
        {
            var go = new GameObject("SelectedHero");
            go.tag = "Player";
            go.transform.position = new Vector3(1.5f, 2.5f, 3.5f);
            go.AddComponent<Collider>();

            Selection.activeGameObject = go;
            Selection.activeObject = go;
            Selection.objects = new UnityEngine.Object[] { go };

            string result = Inspect.Selection();

            Assert.Contains("Active Selection (GameObject): \"SelectedHero\"", result);
            Assert.Contains("Tag: \"Player\"", result);
            Assert.Contains("World Pos: (1.50, 2.50, 3.50)", result);
            Assert.Contains("Components (2): [Transform, Collider]", result);
        }

        [Fact]
        public void Selection_WithMultipleObjects_ListsCountsAndSummaries()
        {
            var go1 = new GameObject("Item1");
            var go2 = new GameObject("Item2");
            var go3 = new GameObject("Item3");

            Selection.activeGameObject = go1;
            Selection.activeObject = go1;
            Selection.objects = new UnityEngine.Object[] { go1, go2, go3 };

            string result = Inspect.Selection();

            Assert.Contains("Total Selected Objects (3):", result);
            Assert.Contains("- \"Item1\"", result);
            Assert.Contains("- \"Item2\"", result);
            Assert.Contains("- \"Item3\"", result);
        }

        [Fact]
        public void Selection_WithAssetObject_ReturnsAssetDetailsAndPath()
        {
            var asset = new UnityEngine.Object { name = "SkyMaterial" };
            AssetDatabase.GetAssetPathFunc = o => o == asset ? "Assets/Sky.mat" : "";

            Selection.activeGameObject = null;
            Selection.activeObject = asset;
            Selection.objects = new UnityEngine.Object[] { asset };

            string result = Inspect.Selection();

            Assert.Contains("Active Selection (Asset): \"SkyMaterial\" (Object)", result);
            Assert.Contains("Asset Path: \"Assets/Sky.mat\"", result);
        }

        [Fact]
        public void MainCamera_WhenNoCameraInScene_ReturnsCleanNotification()
        {
            Camera.main = null;
            Camera.AllCamerasList.Clear();

            string result = Inspect.MainCamera();

            Assert.Equal("(No active Camera found in scene)", result);
        }

        [Fact]
        public void MainCamera_PerspectiveCamera_ReturnsFOVViewportAndSettings()
        {
            var go = new GameObject("MainCameraObject");
            var cam = go.AddComponent<Camera>();
            cam.name = "Main Camera";
            cam.orthographic = false;
            cam.fieldOfView = 65.5f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.pixelWidth = 1920;
            cam.pixelHeight = 1080;
            cam.transform.position = new Vector3(0, 10, -20);
            cam.transform.eulerAngles = new Vector3(15, 0, 0);

            Camera.main = cam;

            string result = Inspect.MainCamera();

            Assert.Contains("Main Camera: \"Main Camera\" (GameObject: \"MainCameraObject\", active: true)", result);
            Assert.Contains("Position: (0.00, 10.00, -20.00)", result);
            Assert.Contains("Rotation: (15.00, 0.00, 0.00)", result);
            Assert.Contains("Projection: Perspective (FOV: 65.5°)", result);
            Assert.Contains("Clipping Planes: Near=0.10, Far=2000.00", result);
            Assert.Contains("Viewport Rect: [x:0.00, y:0.00, w:1.00, h:1.00]", result);
            Assert.Contains("Pixel Dimensions: 1920 x 1080", result);
        }

        [Fact]
        public void MainCamera_OrthographicCamera_ReturnsOrthographicSize()
        {
            var go = new GameObject("2DCamera");
            var cam = go.AddComponent<Camera>();
            cam.name = "2D Camera";
            cam.orthographic = true;
            cam.orthographicSize = 7.5f;

            Camera.main = cam;

            string result = Inspect.MainCamera();

            Assert.Contains("Main Camera: \"2D Camera\"", result);
            Assert.Contains("Projection: Orthographic (Size: 7.50)", result);
        }

        #endregion

        #region Inspect Alias & Output Limit Tests

        [Fact]
        public void Inspect_Methods_AreDeterministic()
        {
            var scene = new Scene { name = "AliasScene", isLoaded = true, RootObjects = new List<GameObject>() };
            SceneManager.Scenes.Add(scene);

            string r1 = Inspect.Hierarchy();
            string r2 = Inspect.Hierarchy();
            Assert.Equal(r1, r2);

            string s1 = Inspect.Selection();
            string s2 = Inspect.Selection();
            Assert.Equal(s1, s2);

            string c1 = Inspect.MainCamera();
            string c2 = Inspect.MainCamera();
            Assert.Equal(c1, c2);
        }

        #region Visual Capture Tests

        [Fact]
        public void CaptureCamera_WhenCameraPresent_SavesPngAndReturnsFormattedSummary()
        {
            var go = new GameObject("TestCamObj");
            var cam = go.AddComponent<Camera>();
            cam.name = "MyRenderCam";
            Camera.main = cam;

            string outputPath = "Temp/test_inspect_cam_unit.png";
            _createdFiles.Add(outputPath);

            string result = Inspect.CaptureCamera(outputPath, width: 800, height: 600, camera: cam);

            Assert.Contains("Captured camera \"MyRenderCam\"", result);
            Assert.Contains("(800x600,", result);
            Assert.Contains("Temp/test_inspect_cam_unit.png", result);
            Assert.True(System.IO.File.Exists(outputPath));
        }

        [Fact]
        public void CaptureCamera_WhenNoCameraInScene_ReturnsCleanNotification()
        {
            Camera.main = null;
            Camera.AllCamerasList.Clear();

            string result = Inspect.CaptureCamera("Temp/not_used.png");

            Assert.Equal("(No active Camera found in scene to capture)", result);
        }

        [Fact]
        public void CaptureCamera_ClampsInvalidDimensions()
        {
            var go = new GameObject("ClampCamObj");
            var cam = go.AddComponent<Camera>();
            cam.name = "ClampCam";
            Camera.main = cam;

            string outputPath = "Temp/test_inspect_cam_unit_clamp.png";
            _createdFiles.Add(outputPath);

            string result = Inspect.CaptureCamera(outputPath, width: 2, height: 8000, camera: cam);

            Assert.Contains("(16x4096,", result);
            Assert.True(System.IO.File.Exists(outputPath));
        }

        [Fact]
        public void CaptureSceneView_WhenSceneViewPresent_SavesPngAndReturnsSummary()
        {
            var camGo = new GameObject("SceneCamObj");
            var cam = camGo.AddComponent<Camera>();
            cam.name = "SceneCamera";

            var sv = new SceneView { camera = cam };
            SceneView.lastActiveSceneView = sv;

            string outputPath = "Temp/test_inspect_sceneview_unit.png";
            _createdFiles.Add(outputPath);

            string result = Inspect.CaptureSceneView(outputPath, width: 640, height: 480);

            Assert.Contains("Captured SceneView (640x480,", result);
            Assert.Contains("Temp/test_inspect_sceneview_unit.png", result);
            Assert.True(System.IO.File.Exists(outputPath));
        }

        [Fact]
        public void CaptureSceneView_WhenNoSceneView_ReturnsCleanNotification()
        {
            SceneView.lastActiveSceneView = null;
            SceneView.SceneViewsList.Clear();

            string result = Inspect.CaptureSceneView();

            Assert.Equal("(No active SceneView found in Editor to capture)", result);
        }

        [Fact]
        public void CaptureSceneView_WhenSceneViewHasNoCamera_ReturnsCleanNotification()
        {
            var sv = new SceneView { camera = null };
            SceneView.lastActiveSceneView = sv;

            string result = Inspect.CaptureSceneView();

            Assert.Equal("(SceneView does not have an active Camera to capture)", result);
        }

        #endregion

        [Fact]
        public void LimitOutput_TruncatesWhenExceedingCharacterCap()
        {
            string hugeText = new string('A', 1000);
            string capped = Inspect.LimitOutput(hugeText, maxCharacters: 100, maxBytes: 100);

            Assert.True(capped.Length <= 100);
            Assert.Contains("... (truncated: maximum output size reached)", capped);
        }

        [Fact]
        public void LimitOutput_PreservesTextWithinBudget()
        {
            string normalText = "Hello World";
            string result = Inspect.LimitOutput(normalText, maxCharacters: 100, maxBytes: 100);

            Assert.Equal("Hello World", result);
        }

        [Fact]
        public void LimitOutput_TinyBudget_StrictlyEnforcesCapsWithoutOverflow()
        {
            string text = new string('X', 500);

            // Cap at 10 chars
            string capped10 = Inspect.LimitOutput(text, maxCharacters: 10, maxBytes: 10);
            Assert.True(capped10.Length <= 10, $"Expected length <= 10 but was {capped10.Length}");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(capped10) <= 10);

            // Cap at 5 chars
            string capped5 = Inspect.LimitOutput(text, maxCharacters: 5, maxBytes: 5);
            Assert.True(capped5.Length <= 5, $"Expected length <= 5 but was {capped5.Length}");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(capped5) <= 5);
        }

        #endregion

        #region Adversarial & Edge Case Tests

        [Fact]
        public void Hierarchy_TransformRoot_InspectsSubtreeDirectly()
        {
            var root = new GameObject("Root");
            var child = new GameObject("Child");
            root.transform.AddChild(child.transform);

            string result = Inspect.Hierarchy(root.transform);
            Assert.Contains("Root [active]", result);
            Assert.Contains("  Child [active]", result);

            string aliasResult = Inspect.Hierarchy(root.transform);
            Assert.Equal(result, aliasResult);
        }

        [Fact]
        public void Hierarchy_UnloadedScene_ReportsNotLoaded()
        {
            var scene = new Scene { name = "UnloadedScene", isLoaded = false, RootObjects = new List<GameObject>() };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();
            Assert.Contains("Scene \"UnloadedScene\" (not loaded)", result);

            string singleResult = Inspect.Hierarchy("UnloadedScene");
            Assert.Contains("Scene \"UnloadedScene\" (not loaded)", singleResult);
        }

        [Fact]
        public void Hierarchy_DeeplyNested_DeduplicatesItemTruncationMarker()
        {
            var root = new GameObject("Root");
            var child1 = new GameObject("Child1");
            var child11 = new GameObject("Child11");
            var child2 = new GameObject("Child2");
            root.transform.AddChild(child1.transform);
            child1.transform.AddChild(child11.transform);
            root.transform.AddChild(child2.transform);

            var scene = new Scene { name = "TestScene", isLoaded = true, RootObjects = new List<GameObject> { root } };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy(maxDepth: 32, maxItems: 2);
            int firstIdx = result.IndexOf("... (truncated: maximum item count reached)");
            int lastIdx = result.LastIndexOf("... (truncated: maximum item count reached)");
            Assert.True(firstIdx >= 0);
            Assert.Equal(firstIdx, lastIdx);
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
        public void LimitOutput_65KMultiByteCharactersAndSurrogatePairs_TruncatesLinearlyAndPreservesValidUtf16()
        {
            // 65,536 3-byte UTF-8 characters (196,608 UTF-8 bytes)
            string multiByteBmp = new string('€', 65536);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string limitedInspect = Inspect.LimitOutput(multiByteBmp, maxCharacters: 65536, maxBytes: 65536);
            string limitedFormatter = UnityResultFormatter.FormatResult(multiByteBmp, prettyPrint: false);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected fast O(N) truncation, took {sw.ElapsedMilliseconds}ms");
            Assert.True(Encoding.UTF8.GetByteCount(limitedInspect) <= 65536);
            Assert.True(Encoding.UTF8.GetByteCount(limitedFormatter) <= 65536);
            Assert.Contains("... (truncated: maximum output size reached)", limitedInspect);
            Assert.Contains("... (truncated: maximum output size reached)", limitedFormatter);

            // 32,768 emoji surrogate pairs (65,536 UTF-16 chars, 131,072 UTF-8 bytes)
            var emojiBuilder = new StringBuilder(65536);
            for (int i = 0; i < 32768; i++)
            {
                emojiBuilder.Append("\uD83D\uDE00");
            }
            string emojiText = emojiBuilder.ToString();
            string limitedEmoji = Inspect.LimitOutput(emojiText, maxCharacters: 65536, maxBytes: 65536);
            Assert.True(Encoding.UTF8.GetByteCount(limitedEmoji) <= 65536);
            int markerIndex = limitedEmoji.IndexOf("... (truncated: maximum output size reached)", StringComparison.Ordinal);
            Assert.True(markerIndex > 0);
            Assert.False(char.IsHighSurrogate(limitedEmoji[markerIndex - 1]));
        }

        private class BaseSerializableNode
        {
            [SerializeField] private BaseSerializableNode? _serializedPrivateCycle;
            internal BaseSerializableNode? InternalNonSerializedCycle;
            protected BaseSerializableNode? ProtectedNonSerializedCycle;

            public void SetPrivateCycle(BaseSerializableNode target) => _serializedPrivateCycle = target;
            public void SetProtectedCycle(BaseSerializableNode target) => ProtectedNonSerializedCycle = target;
        }

        private sealed class DerivedSerializableNode : BaseSerializableNode
        {
            public string Label = "Node";
        }

        [Fact]
        public void FormattingContext_InspectChildren_IgnoresInternalAndProtectedFieldsAndVisitsInheritedPrivateSerializeField()
        {
            var nodeWithNonPublicCycles = new DerivedSerializableNode();
            nodeWithNonPublicCycles.InternalNonSerializedCycle = nodeWithNonPublicCycles;
            nodeWithNonPublicCycles.SetProtectedCycle(nodeWithNonPublicCycles);

            var ctx1 = new UnityResultFormatter.FormattingContext(
                UnityResultFormatter.DefaultMaxDepth,
                UnityResultFormatter.DefaultMaxItems,
                UnityResultFormatter.DefaultMaxOutputCharacters,
                UnityResultFormatter.DefaultMaxOutputBytes);
            Assert.Equal(
                UnityResultFormatter.GraphGuardResult.Safe,
                ctx1.InspectChildren(nodeWithNonPublicCycles, depth: 0, prettyPrint: false));

            // Setting the inherited [SerializeField] private field to a self-cycle must be detected
            var nodeWithInheritedSerializedCycle = new DerivedSerializableNode();
            nodeWithInheritedSerializedCycle.SetPrivateCycle(nodeWithInheritedSerializedCycle);

            var ctx2 = new UnityResultFormatter.FormattingContext(
                UnityResultFormatter.DefaultMaxDepth,
                UnityResultFormatter.DefaultMaxItems,
                UnityResultFormatter.DefaultMaxOutputCharacters,
                UnityResultFormatter.DefaultMaxOutputBytes);
            Assert.Equal(
                UnityResultFormatter.GraphGuardResult.Cycle,
                ctx2.InspectChildren(nodeWithInheritedSerializedCycle, depth: 0, prettyPrint: false));
        }

        private sealed class FallbackArrayWrapper
        {
            public int[] Items = new[] { 1, 2, 3 };
        }

        [Fact]
        public void FormattingContext_SharesUnifiedItemBudgetAcrossCustomFormattersAndFallbackInspection()
        {
            int originalMaxItems = UnityResultFormatter.MaxItems;
            try
            {
                UnityResultFormatter.MaxItems = 3;
                // Outer list takes 1 item for the wrapper; wrapper's Items array has 3 elements (total 4 > MaxItems=3)
                var composite = new List<object> { new FallbackArrayWrapper() };
                string formatted = UnityResultFormatter.FormatResult(composite, prettyPrint: false);
                Assert.Contains("... (truncated: maximum item count reached)", formatted);
            }
            finally
            {
                UnityResultFormatter.MaxItems = originalMaxItems;
            }
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
        public void MainCamera_FallbackToFindObjectOfType_WhenMainAndAllCamerasEmpty()
        {
            Camera.main = null;
            Camera.AllCamerasList.Clear();

            var go = new GameObject("HiddenCamObj");
            var cam = go.AddComponent<Camera>();
            cam.name = "HiddenCamera";

            UnityEngine.Object.FindObjectsOfTypeFunc = type => type == typeof(Camera) ? new object[] { cam } : Array.Empty<object>();

            string result = Inspect.MainCamera();
            Assert.Contains("Main Camera: \"HiddenCamera\"", result);

            UnityEngine.Object.FindObjectsOfTypeFunc = null;
        }

        [Fact]
        public void Selection_WhenAllObjectsAreNull_ReturnsCleanNotification()
        {
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = new UnityEngine.Object[] { null! };

            string result = Inspect.Selection();
            Assert.Equal("(No active selection in Editor)", result);
        }

        [Fact]
        public void Hierarchy_DestroyedGameObject_ReturnsCleanDestroyedNotification()
        {
            var go = new GameObject("DeadEnemy");
            UnityEngine.Object.DestroyImmediate(go);

            string result = Inspect.Hierarchy(go);
            Assert.Equal("(GameObject is destroyed)", result);
        }

        [Fact]
        public void Hierarchy_NullAndDestroyedTransform_ReturnsCleanNotification()
        {
            Transform nullTr = null!;
            string resNull = Inspect.Hierarchy(nullTr);
            Assert.Equal("(Transform is null)", resNull);

            var go = new GameObject("DeadTarget");
            var tr = go.transform;
            UnityEngine.Object.DestroyImmediate(go);

            string resDestroyed = Inspect.Hierarchy(tr);
            Assert.Equal("(Transform is destroyed)", resDestroyed);
        }

        [Fact]
        public void Hierarchy_MaxDepthWithMultipleChildren_DeduplicatesDepthTruncationMarker()
        {
            var root = new GameObject("Root");
            var childAtMaxDepth = new GameObject("ChildMax");
            root.transform.AddChild(childAtMaxDepth.transform);

            // Add 5 children beyond maxDepth to childAtMaxDepth
            for (int i = 0; i < 5; i++)
            {
                var beyond = new GameObject($"Beyond_{i}");
                childAtMaxDepth.transform.AddChild(beyond.transform);
            }

            var scene = new Scene { name = "TestScene", isLoaded = true, RootObjects = new List<GameObject> { root } };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy(maxDepth: 1);
            Assert.Contains("Root [active]", result);
            Assert.Contains("ChildMax [active]", result);
            Assert.DoesNotContain("Beyond_", result);

            // Depth truncation marker should appear exactly ONCE, not 5 times
            int firstIdx = result.IndexOf("... (truncated: maximum depth reached)");
            int lastIdx = result.LastIndexOf("... (truncated: maximum depth reached)");
            Assert.True(firstIdx >= 0, "Depth truncation marker missing");
            Assert.Equal(firstIdx, lastIdx);
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

            // SerializedObject exposes m_Health
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

            // Health should be listed via SerializedProperty
            Assert.Contains("Health (m_Health): 100", result);
            // Health property should NOT be repeated as reflection property
            Assert.DoesNotContain("  Health: 100", result);
            Assert.DoesNotContain("  health: 100", result);

            // Unserialized property Speed should still be listed
            Assert.Contains("Speed: 4.5", result);
        }

        [Fact]
        public void FindAsset_WhenExceptionOccurs_ReturnsGracefulNotification()
        {
            AssetDatabase.FindAssetsFunc = _ => throw new InvalidOperationException("Asset database is busy");

            string result = Inspect.FindAsset("MissingAsset");
            Assert.Contains("Asset search failed: Asset database is busy", result);
        }

        [Fact]
        public void MainCamera_WhenCameraPresent_HandlesGracefullyWithoutCrashing()
        {
            var go = new GameObject("ThrowingCam");
            var cam = go.AddComponent<Camera>();
            cam.name = "ThrowingCamera";
            Camera.main = cam;

            string result = Inspect.MainCamera();
            Assert.Contains("Main Camera: \"ThrowingCamera\"", result);
        }

        [Fact]
        public void Selection_WithMultipleDestroyedObjects_ReturnsCleanEmptyNotification()
        {
            var go1 = new GameObject("Dead1");
            var go2 = new GameObject("Dead2");
            UnityEngine.Object.DestroyImmediate(go1);
            UnityEngine.Object.DestroyImmediate(go2);

            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = new UnityEngine.Object[] { go1, go2 };

            string result = Inspect.Selection();
            Assert.Equal("(No active selection in Editor)", result);
        }

        [Fact]
        public void Selection_WithMixedValidAndDestroyedObjects_OnlyCountsValidObjects()
        {
            var validGo = new GameObject("LiveHero");
            var deadGo = new GameObject("DeadMinion");
            UnityEngine.Object.DestroyImmediate(deadGo);

            Selection.activeGameObject = validGo;
            Selection.activeObject = validGo;
            Selection.objects = new UnityEngine.Object[] { validGo, deadGo };

            string result = Inspect.Selection();
            Assert.Contains("Active Selection (GameObject): \"LiveHero\"", result);
            Assert.DoesNotContain("Total Selected Objects", result);
        }

        [Fact]
        public void Hierarchy_ByScenePath_MatchesSceneByPathAndFileNameWithoutExtension()
        {
            var scene = new Scene
            {
                name = "SampleScene",
                path = "Assets/Scenes/SampleScene.unity",
                isLoaded = true,
                RootObjects = new List<GameObject> { new GameObject("RootPlayer") }
            };
            SceneManager.Scenes.Add(scene);

            string resPath = Inspect.Hierarchy("Assets/Scenes/SampleScene.unity");
            Assert.Contains("RootPlayer", resPath);

            string resExt = Inspect.Hierarchy("SampleScene.unity");
            Assert.Contains("RootPlayer", resExt);

            string resName = Inspect.Hierarchy("SampleScene");
            Assert.Contains("RootPlayer", resName);
        }

        [Fact]
        public void Hierarchy_WhenAllRootGameObjectsAreDestroyed_ReturnsCleanNotification()
        {
            var go = new GameObject("DeadRoot");
            UnityEngine.Object.DestroyImmediate(go);

            var scene = new Scene
            {
                name = "EmptyDeadScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { go }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.Hierarchy();
            Assert.Equal("(No GameObjects found in loaded scenes)", result);
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
        public void Hierarchy_TransformWithNullGameObject_ReturnsCleanNotification()
        {
            var tr = new Transform();
            tr.gameObject = null!;

            string result = Inspect.Hierarchy(tr);
            Assert.Equal("(Transform has no GameObject)", result);
        }

        [Fact]
        public void Hierarchy_CyclicTransformTree_HandlesCycleCleanly()
        {
            var nodeA = new GameObject("NodeA");
            var nodeB = new GameObject("NodeB");
            nodeA.transform.AddChild(nodeB.transform);
            nodeB.transform.AddChild(nodeA.transform);

            string result = Inspect.Hierarchy(nodeA);
            Assert.Contains("NodeA", result);
            Assert.Contains("NodeB", result);
            Assert.Contains("... (truncated: cycle detected)", result);
        }

        [Fact]
        public void Help_ReturnsAllDocumentedInspectMethods()
        {
            string inspectHelp = Inspect.Help();

            Assert.StartsWith("UnityLeanMcp Inspection Helpers (UnityLeanMcp.Inspect):", inspectHelp);
            Assert.Contains("Hierarchy", inspectHelp);
            Assert.Contains("GameObject", inspectHelp);
            Assert.Contains("Component", inspectHelp);
            Assert.Contains("Object", inspectHelp);
            Assert.Contains("FindAssets", inspectHelp);
            Assert.Contains("FindPrefabs", inspectHelp);
            Assert.Contains("FindAsset", inspectHelp);
            Assert.Contains("Selection", inspectHelp);
            Assert.Contains("MainCamera", inspectHelp);
            Assert.Contains("PrefabOverrides", inspectHelp);
            Assert.Contains("• string PrefabOverrides(GameObject instance, int maxItems = 50)", inspectHelp);
        }

        #endregion

        #region Prefab Overrides Tests

        [Fact]
        public void PrefabOverrides_NullGameObject_ReturnsNullNotification()
        {
            string result = Inspect.PrefabOverrides(null);
            Assert.Equal("(GameObject is null)", result);
        }

        [Fact]
        public void PrefabOverrides_DestroyedGameObject_ReturnsDestroyedNotification()
        {
            var go = new GameObject("DeadObject");
            UnityEngine.Object.DestroyImmediate(go);

            string result = Inspect.PrefabOverrides(go);
            Assert.Equal("(GameObject is destroyed)", result);
        }

        [Fact]
        public void PrefabOverrides_NonPrefabInstance_ReturnsNotPrefabNotification()
        {
            var go = new GameObject("PlainObject");
            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => false;

            string result = Inspect.PrefabOverrides(go);
            Assert.Equal("(GameObject is not a prefab instance)", result);
        }

        [Fact]
        public void PrefabOverrides_CleanPrefabInstance_ReturnsNoOverridesCleanStatus()
        {
            var go = new GameObject("CleanPlayer");
            var source = new GameObject("PlayerSource");
            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => true;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = _ => source;
            AssetDatabase.GetAssetPathFunc = obj => "Assets/Prefabs/Player.prefab";

            string result = Inspect.PrefabOverrides(go);

            Assert.Contains("Prefab Instance: \"CleanPlayer\" (Source: Assets/Prefabs/Player.prefab)", result);
            Assert.Contains("Status: No overrides (clean instance)", result);
        }

        [Fact]
        public void PrefabOverrides_AddedComponents_FormatsAddedComponentsCleanly()
        {
            var go = new GameObject("VariantPlayer");
            var source = new GameObject("PlayerSource");
            var audio = go.AddComponent<AudioSource>();

            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => true;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = _ => source;
            AssetDatabase.GetAssetPathFunc = obj => "Assets/Prefabs/Player.prefab";
            PrefabUtility.GetAddedComponentsFunc = _ => new List<AddedComponent>
            {
                new AddedComponent { instanceComponent = audio }
            };

            string result = Inspect.PrefabOverrides(go);

            Assert.Contains("1 override(s) (1 added, 0 removed, 0 modified)", result);
            Assert.Contains("Added Components:", result);
            Assert.Contains("+ AudioSource", result);
        }

        [Fact]
        public void PrefabOverrides_RemovedComponents_FormatsRemovedComponentsCleanly()
        {
            var go = new GameObject("VariantPlayer");
            var source = new GameObject("PlayerSource");
            var rb = source.AddComponent<Rigidbody>();

            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => true;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = _ => source;
            AssetDatabase.GetAssetPathFunc = obj => "Assets/Prefabs/Player.prefab";
            PrefabUtility.GetRemovedComponentsFunc = _ => new List<RemovedComponent>
            {
                new RemovedComponent { assetComponent = rb }
            };

            string result = Inspect.PrefabOverrides(go);

            Assert.Contains("1 override(s) (0 added, 1 removed, 0 modified)", result);
            Assert.Contains("Removed Components:", result);
            Assert.Contains("- Rigidbody", result);
        }

        [Fact]
        public void PrefabOverrides_PropertyModifications_FiltersEngineNoiseAndFormatsDeltas()
        {
            var go = new GameObject("VariantPlayer");
            var source = new GameObject("PlayerSource");

            var mat = new Material { name = "HeroMat" };
            var targetTr = go.transform;

            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => true;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = _ => source;
            AssetDatabase.GetAssetPathFunc = obj => "Assets/Prefabs/Player.prefab";
            PrefabUtility.GetPropertyModificationsFunc = _ => new[]
            {
                // Valid modifications
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_LocalPosition.x",
                    value = "10.5"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_Material",
                    objectReference = mat
                },
                // Internal engine noise: null target
                new PropertyModification
                {
                    target = null,
                    propertyPath = "m_LocalPosition.y",
                    value = "20"
                },
                // Internal engine noise: m_ObjectHideFlags
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_ObjectHideFlags",
                    value = "0"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_ObjectHideFlags.someField",
                    value = "0"
                },
                // Internal engine noise: compiler-generated / internal bookkeeping
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_CorrespondingSourceObject",
                    value = "12345"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_PrefabInstance",
                    value = "67890"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "m_PrefabAsset",
                    value = "54321"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "<>c__DisplayClass0_0",
                    value = "noise"
                },
                new PropertyModification
                {
                    target = targetTr,
                    propertyPath = "$internal$field",
                    value = "noise"
                }
            };

            string result = Inspect.PrefabOverrides(go);

            Assert.Contains("2 override(s) (0 added, 0 removed, 2 modified)", result);
            Assert.Contains("Modified Properties:", result);
            Assert.Contains("~ Transform.m_LocalPosition.x = 10.5", result);
            Assert.Contains("~ Transform.m_Material = HeroMat (Material)", result);

            // Verify noise is completely filtered
            Assert.DoesNotContain("m_ObjectHideFlags", result);
            Assert.DoesNotContain("m_CorrespondingSourceObject", result);
            Assert.DoesNotContain("m_PrefabInstance", result);
            Assert.DoesNotContain("m_PrefabAsset", result);
            Assert.DoesNotContain("DisplayClass", result);
            Assert.DoesNotContain("$internal$field", result);
        }

        [Fact]
        public void PrefabOverrides_ItemTruncation_TruncatesWhenExceedingMaxItems()
        {
            var go = new GameObject("TruncatedPlayer");
            var source = new GameObject("PlayerSource");
            var audio = go.AddComponent<AudioSource>();
            var rb = source.AddComponent<Rigidbody>();

            PrefabUtility.IsPartOfAnyPrefabInstanceFunc = _ => true;
            PrefabUtility.GetCorrespondingObjectFromSourceFunc = _ => source;
            AssetDatabase.GetAssetPathFunc = obj => "Assets/Prefabs/Player.prefab";
            PrefabUtility.GetAddedComponentsFunc = _ => new List<AddedComponent>
            {
                new AddedComponent { instanceComponent = audio }
            };
            PrefabUtility.GetRemovedComponentsFunc = _ => new List<RemovedComponent>
            {
                new RemovedComponent { assetComponent = rb }
            };
            PrefabUtility.GetPropertyModificationsFunc = _ => new[]
            {
                new PropertyModification
                {
                    target = go.transform,
                    propertyPath = "m_LocalPosition.z",
                    value = "5.0"
                }
            };

            // Max items = 1, while there are 3 overrides
            string result = Inspect.PrefabOverrides(go, maxItems: 1);

            Assert.Contains("3 override(s) (1 added, 1 removed, 1 modified)", result);
            Assert.Contains("+ AudioSource", result);
            Assert.Contains(Inspect.ItemTruncationMarker, result);
            // Since maxItems is 1, later sections should not display their items
            Assert.DoesNotContain("- Rigidbody", result);
            Assert.DoesNotContain("m_LocalPosition.z", result);
        }

        [Fact]
        public void MainCamera_FindFirstObjectByType_ResolvesCameraSuccessfully()
        {
            Camera.main = null;
            Camera.AllCamerasList.Clear();

            var go = new GameObject("CameraGo");
            var cam = go.AddComponent<Camera>();
            cam.name = "MyUniqueCamera";

            UnityEngine.Object.FindObjectsOfTypeFunc = type => type == typeof(Camera) ? new object[] { cam } : Array.Empty<object>();

            var resolved = UnityEngine.Object.FindFirstObjectByType<Camera>();
            Assert.NotNull(resolved);
            Assert.Equal("MyUniqueCamera", resolved!.name);

            string result = Inspect.MainCamera();
            Assert.Contains("Main Camera: \"MyUniqueCamera\"", result);

            UnityEngine.Object.FindObjectsOfTypeFunc = null;
        }

        [Fact]
        public void Find_MatchesSubstringsAndWildcards_RespectsInactiveFlag()
        {
            var rootGo = new GameObject("PlayerCharacter");
            rootGo.activeSelf = true;

            var childGo = new GameObject("PlayerWeapon");
            childGo.activeSelf = true;
            rootGo.transform.AddChild(childGo.transform);

            var enemyGo = new GameObject("EnemyBoss");
            enemyGo.activeSelf = true;

            var hiddenGo = new GameObject("SecretSpawner");
            hiddenGo.activeSelf = false;

            var scene = new Scene
            {
                name = "TestScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { rootGo, enemyGo, hiddenGo }
            };
            SceneManager.Scenes.Add(scene);

            // Substring search
            string playerMatches = Inspect.Find("Player");
            Assert.Contains("GameObjects matching 'Player' (2):", playerMatches);
            Assert.Contains("PlayerCharacter", playerMatches);
            Assert.Contains("PlayerWeapon", playerMatches);
            Assert.DoesNotContain("EnemyBoss", playerMatches);

            // Wildcard search with inactive exclusion
            string spawnerExcluded = Inspect.Find("*Spawner*", includeInactive: false);
            Assert.Contains("(No GameObjects found matching '*Spawner*' in loaded scenes)", spawnerExcluded);

            // Wildcard search with inactive inclusion
            string spawnerIncluded = Inspect.Find("*Spawner*", includeInactive: true);
            Assert.Contains("SecretSpawner", spawnerIncluded);
            Assert.Contains("inactive", spawnerIncluded);

            // Non-matching query
            string noMatches = Inspect.Find("NonExistentNameXYZ");
            Assert.Contains("(No GameObjects found matching 'NonExistentNameXYZ' in loaded scenes)", noMatches);
        }

        [Fact]
        public void MissingScripts_WhenNoMissingScripts_ReturnsCleanMessage()
        {
            var go = new GameObject("ValidObject");
            go.AddComponent<Collider>();

            var scene = new Scene
            {
                name = "CleanScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { go }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.MissingScripts();
            Assert.Equal("(No missing MonoBehaviour scripts found in loaded scenes)", result);
        }

        [Fact]
        public void MissingScripts_WhenMissingScriptsExist_ReportsGameObjectAndComponentIndices()
        {
            var go = new GameObject("BrokenObject");
            go.AddComponent<Collider>();
            go.AddRawComponent(null); // Missing script

            var scene = new Scene
            {
                name = "BrokenScene",
                isLoaded = true,
                RootObjects = new List<GameObject> { go }
            };
            SceneManager.Scenes.Add(scene);

            string result = Inspect.MissingScripts();
            Assert.Contains("Missing MonoBehaviour scripts found (1 missing across 1 GameObjects):", result);
            Assert.Contains("BrokenObject", result);
            Assert.Contains("component index [2]", result);
            Assert.Contains("BrokenScene", result);
        }

        [Fact]
        public void SceneState_MultipleScenes_ReportsActiveDirtyAndLoadedSummary()
        {
            var mainScene = new Scene
            {
                handle = 1,
                name = "MainScene",
                path = "Assets/Scenes/Main.unity",
                isLoaded = true,
                isDirty = true,
                RootObjects = new List<GameObject> { new GameObject("RootA"), new GameObject("RootB") }
            };

            var uiScene = new Scene
            {
                handle = 2,
                name = "UIScene",
                path = "Assets/Scenes/UI.unity",
                isLoaded = true,
                isDirty = false,
                RootObjects = new List<GameObject> { new GameObject("Canvas") }
            };

            SceneManager.Scenes.Add(mainScene);
            SceneManager.Scenes.Add(uiScene);

            string result = Inspect.SceneState();

            Assert.Contains("Active Scene: \"MainScene\"", result);
            Assert.Contains("Path: Assets/Scenes/Main.unity", result);
            Assert.Contains("Root GameObjects: 2", result);
            Assert.Contains("Is Dirty: true", result);
            Assert.Contains("Is Loaded: true", result);

            Assert.Contains("Loaded Scenes (2):", result);
            Assert.Contains("1. \"MainScene\" [Active] (roots: 2, dirty, path: Assets/Scenes/Main.unity)", result);
            Assert.Contains("2. \"UIScene\" (roots: 1, path: Assets/Scenes/UI.unity)", result);
        }

        [Fact]
        public void Tests_DiscoversTestsInLoadedAppDomain_AndFilters()
        {
            // All tests
            string allTests = Inspect.Tests();
            Assert.Contains("Available Tests", allTests);
            Assert.Contains("• UnityLeanMcp.", allTests);

            // Filter matching this test class
            string filtered = Inspect.Tests("UnityInspectUnitTests", maxItems: 200);
            Assert.Contains("Available Tests", filtered);
            Assert.Contains("UnityInspectUnitTests", filtered);
            Assert.Contains("Tests_DiscoversTestsInLoadedAppDomain", filtered);

            // Filter matching nothing
            string none = Inspect.Tests("NonExistentPattern_ZXY_987");
            Assert.Contains("(No tests found matching filter 'NonExistentPattern_ZXY_987')", none);
        }

        [Fact]
        public void Help_ContainsCategorizedSections()
        {
            string help = Inspect.Help();

            Assert.Contains("Scene & Hierarchy:", help);
            Assert.Contains("Objects & Components:", help);
            Assert.Contains("Assets & Prefabs:", help);
            Assert.Contains("Visual Capture:", help);
            Assert.Contains("Editor & Tests:", help);
            Assert.Contains("Utilities:", help);

            Assert.Contains("• string Find(string namePattern", help);
            Assert.Contains("• string MissingScripts(", help);
            Assert.Contains("• string SceneState()", help);
            Assert.Contains("• string Dependencies(string assetPath, bool recursive = false, int maxItems = 100)", help);
            Assert.Contains("• string Referencers(string targetAssetPath, string searchFolder = \"Assets\", int maxItems = 100)", help);
            Assert.Contains("• string CaptureCamera(string outputPath = \"Temp/unity_capture.png\", int width = 1024, int height = 768, Camera camera = null)", help);
            Assert.Contains("• string CaptureSceneView(string outputPath = \"Temp/unity_sceneview.png\", int width = 1024, int height = 768)", help);
            Assert.Contains("• string Tests(string filter = null", help);
        }

        #endregion
    }
}
