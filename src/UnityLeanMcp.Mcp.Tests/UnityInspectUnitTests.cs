using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityLeanMcp;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests
{
    [Trait("Category", "Unit")]
    public class UnityInspectUnitTests : IDisposable
    {
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
            AssetDatabase.FindAssetsFunc = null;
            AssetDatabase.GUIDToAssetPathFunc = null;
            AssetDatabase.GetMainAssetTypeAtPathFunc = null;
            AssetDatabase.GetAssetPathFunc = null;
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
            AssetDatabase.FindAssetsFunc = null;
            AssetDatabase.GUIDToAssetPathFunc = null;
            AssetDatabase.GetMainAssetTypeAtPathFunc = null;
            AssetDatabase.GetAssetPathFunc = null;
            SerializedObject.CustomProperties.Clear();
        }

        #region Hierarchy & Scene Inspection Tests

        [Fact]
        public void Hierarchy_EmptyScene_ReturnsCleanEmptyNotification()
        {
            var scene = new Scene { name = "EmptyScene", isLoaded = true, RootObjects = new List<GameObject>() };
            SceneManager.Scenes.Add(scene);

            string result = UnityInspect.Hierarchy();

            Assert.Contains("Scene \"EmptyScene\" (0 root GameObjects - empty scene)", result);
        }

        [Fact]
        public void Hierarchy_NoScenesLoaded_ReturnsCleanNotification()
        {
            string result = UnityInspect.Hierarchy();

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

            string result = UnityInspect.Hierarchy();

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
            string result = UnityInspect.Hierarchy(maxDepth: 5, maxItems: 1000);

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

            string result = UnityInspect.Hierarchy();

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
            string result = UnityInspect.Hierarchy(maxDepth: 32, maxItems: 3);

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

            string resA = UnityInspect.Hierarchy("SceneA");
            Assert.Contains("ObjA", resA);
            Assert.DoesNotContain("ObjB", resA);

            string resMissing = UnityInspect.Hierarchy("SceneNonExistent");
            Assert.Contains("Scene \"SceneNonExistent\" is not currently loaded", resMissing);
        }

        [Fact]
        public void Hierarchy_SpecificRoot_OnlyInspectsThatSubtree()
        {
            var root = new GameObject("SpecialRoot");
            var child = new GameObject("SpecialChild");
            root.transform.AddChild(child.transform);

            string result = UnityInspect.Hierarchy(root);

            Assert.Contains("SpecialRoot [active]", result);
            Assert.Contains("  SpecialChild [active]", result);
        }

        #endregion

        #region Object & Component Inspection Tests

        [Fact]
        public void Inspect_Object_Null_ReturnsNullString()
        {
            string result = UnityInspect.Object(null);
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

            string result = UnityInspect.GameObject(go);

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

            string result = UnityInspect.GameObject(go);

            Assert.Contains("GameObject: \"ObjectWithMissingScript\"", result);
            Assert.Contains("Missing Component / Unassigned Script", result);
        }

        [Fact]
        public void Inspect_Component_ListsPropertiesAndValues()
        {
            var go = new GameObject("TestGo");
            var collider = go.AddComponent<Collider>();
            collider.enabled = true;

            string result = UnityInspect.Component(collider);

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
            string result = UnityInspect.Object(comp);

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

            string result = UnityInspect.Object(nodeA, maxDepth: 5);

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
            string result = UnityInspect.Object(multiProp, maxItems: 3);

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

            string result = UnityInspect.FindAssets(filter: "Player", type: "Prefab");

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

            string result = UnityInspect.FindPrefabs("Enemy");

            Assert.Contains("Assets/Prefabs/Enemy.prefab [GameObject]", result);
        }

        [Fact]
        public void FindAssets_NoMatches_ReturnsCleanNotification()
        {
            AssetDatabase.FindAssetsFunc = filter => Array.Empty<string>();

            string result = UnityInspect.FindAssets("NonExistentFilter");

            Assert.Contains("(No assets found matching query: 'NonExistentFilter')", result);
        }

        [Fact]
        public void FindAssets_ItemLimitExceeded_AppendsItemTruncationMarker()
        {
            AssetDatabase.FindAssetsFunc = filter => new[] { "g1", "g2", "g3", "g4", "g5" };
            AssetDatabase.GUIDToAssetPathFunc = guid => $"Assets/Item_{guid}.asset";
            AssetDatabase.GetMainAssetTypeAtPathFunc = path => typeof(UnityEngine.Object);

            string result = UnityInspect.FindAssets(maxItems: 2);

            Assert.Contains("Assets/Item_g1.asset", result);
            Assert.Contains("Assets/Item_g2.asset", result);
            Assert.Contains("... (truncated: maximum item count reached)", result);
            Assert.DoesNotContain("Assets/Item_g3.asset", result);
        }

        [Fact]
        public void FindAsset_ByPath_ReturnsDetailedMetadata()
        {
            string result = UnityInspect.FindAsset("Assets/Prefabs/Test.prefab");

            Assert.Contains("Asset: \"Test\"", result);
            Assert.Contains("Path: \"Assets/Prefabs/Test.prefab\"", result);
            Assert.Contains("GUID: test_guid_", result);
        }

        #endregion

        #region Selection & Main Camera Tests

        [Fact]
        public void Selection_WhenEmpty_ReturnsClearEmptyNotification()
        {
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = Array.Empty<UnityEngine.Object>();

            string result = UnityInspect.Selection();

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

            string result = UnityInspect.Selection();

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

            string result = UnityInspect.Selection();

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

            string result = UnityInspect.Selection();

            Assert.Contains("Active Selection (Asset): \"SkyMaterial\" (Object)", result);
            Assert.Contains("Asset Path: \"Assets/Sky.mat\"", result);
        }

        [Fact]
        public void MainCamera_WhenNoCameraInScene_ReturnsCleanNotification()
        {
            Camera.main = null;
            Camera.AllCamerasList.Clear();

            string result = UnityInspect.MainCamera();

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

            string result = UnityInspect.MainCamera();

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

            string result = UnityInspect.MainCamera();

            Assert.Contains("Main Camera: \"2D Camera\"", result);
            Assert.Contains("Projection: Orthographic (Size: 7.50)", result);
        }

        #endregion

        #region Inspect Alias & Output Limit Tests

        [Fact]
        public void Inspect_AliasClass_ForwardsToUnityInspect()
        {
            var scene = new Scene { name = "AliasScene", isLoaded = true, RootObjects = new List<GameObject>() };
            SceneManager.Scenes.Add(scene);

            string r1 = Inspect.Hierarchy();
            string r2 = UnityInspect.Hierarchy();
            Assert.Equal(r1, r2);

            string s1 = Inspect.Selection();
            string s2 = UnityInspect.Selection();
            Assert.Equal(s1, s2);

            string c1 = Inspect.MainCamera();
            string c2 = UnityInspect.MainCamera();
            Assert.Equal(c1, c2);
        }

        [Fact]
        public void LimitOutput_TruncatesWhenExceedingCharacterCap()
        {
            string hugeText = new string('A', 1000);
            string capped = UnityInspect.LimitOutput(hugeText, maxCharacters: 100, maxBytes: 100);

            Assert.True(capped.Length <= 100);
            Assert.Contains("... (truncated: maximum output size reached)", capped);
        }

        [Fact]
        public void LimitOutput_PreservesTextWithinBudget()
        {
            string normalText = "Hello World";
            string result = UnityInspect.LimitOutput(normalText, maxCharacters: 100, maxBytes: 100);

            Assert.Equal("Hello World", result);
        }

        [Fact]
        public void LimitOutput_TinyBudget_StrictlyEnforcesCapsWithoutOverflow()
        {
            string text = new string('X', 500);

            // Cap at 10 chars
            string capped10 = UnityInspect.LimitOutput(text, maxCharacters: 10, maxBytes: 10);
            Assert.True(capped10.Length <= 10, $"Expected length <= 10 but was {capped10.Length}");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(capped10) <= 10);

            // Cap at 5 chars
            string capped5 = UnityInspect.LimitOutput(text, maxCharacters: 5, maxBytes: 5);
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

            string result = UnityInspect.Hierarchy(root.transform);
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

            string result = UnityInspect.Hierarchy();
            Assert.Contains("Scene \"UnloadedScene\" (not loaded)", result);

            string singleResult = UnityInspect.Hierarchy("UnloadedScene");
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

            string result = UnityInspect.Hierarchy(maxDepth: 32, maxItems: 2);
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
            [Obsolete] public string DeprecatedProp => throw new InvalidOperationException("Deprecated property should not be called!");
            [Obsolete] public int DeprecatedField = 999;
        }

        [Fact]
        public void Inspect_Component_ListsPublicFieldsAndIgnoresMutatingAndObsoleteMembers()
        {
            var go = new GameObject("Owner");
            var customComp = new CustomTestComponent { gameObject = go };
            go.AddRawComponent(customComp);

            string result = UnityInspect.Component(customComp);

            Assert.Contains("PublicField: 42", result);
            Assert.Contains("DebugStatus: \"Active\"", result);
            Assert.Contains("SafeProperty: \"SafeValue\"", result);
            Assert.DoesNotContain("Material getter", result);
            Assert.DoesNotContain("Mesh getter", result);
            Assert.DoesNotContain("DeprecatedProp", result);
            Assert.DoesNotContain("DeprecatedField", result);
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

            string result = UnityInspect.Component(go.transform);

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
            string listResult = UnityInspect.Object(list);
            Assert.Contains("List`1 (3 items):", listResult);
            Assert.Contains("[0]: \"Apple\"", listResult);
            Assert.Contains("[1]: \"Banana\"", listResult);
            Assert.Contains("[2]: \"Cherry\"", listResult);

            var dict = new Dictionary<string, int> { ["Coins"] = 100, ["Gems"] = 5 };
            string dictResult = UnityInspect.Object(dict);
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

            string result = UnityInspect.Component(comp);
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

            string result = UnityInspect.MainCamera();
            Assert.Contains("Main Camera: \"HiddenCamera\"", result);

            UnityEngine.Object.FindObjectsOfTypeFunc = null;
        }

        [Fact]
        public void Selection_WhenAllObjectsAreNull_ReturnsCleanNotification()
        {
            Selection.activeGameObject = null;
            Selection.activeObject = null;
            Selection.objects = new UnityEngine.Object[] { null! };

            string result = UnityInspect.Selection();
            Assert.Equal("(No active selection in Editor)", result);
        }

        [Fact]
        public void Hierarchy_DestroyedGameObject_ReturnsCleanDestroyedNotification()
        {
            var go = new GameObject("DeadEnemy");
            UnityEngine.Object.DestroyImmediate(go);

            string result = UnityInspect.Hierarchy(go);
            Assert.Equal("(GameObject is destroyed)", result);
        }

        [Fact]
        public void Hierarchy_NullAndDestroyedTransform_ReturnsCleanNotification()
        {
            Transform nullTr = null!;
            string resNull = UnityInspect.Hierarchy(nullTr);
            Assert.Equal("(Transform is null)", resNull);

            var go = new GameObject("DeadTarget");
            var tr = go.transform;
            UnityEngine.Object.DestroyImmediate(go);

            string resDestroyed = UnityInspect.Hierarchy(tr);
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

            string result = UnityInspect.Hierarchy(maxDepth: 1);
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
            string vecResult = UnityInspect.Object(vec);
            Assert.Equal("(1.50, 2.50, 3.50)", vecResult.Trim());

            string typeResult = UnityInspect.Object(typeof(Camera));
            Assert.Equal("Type: UnityEngine.Camera", typeResult.Trim());

            Action myAction = () => { };
            string delResult = UnityInspect.Object(myAction);
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
            string result = UnityInspect.Object(data, maxDepth: 2);

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

            string result = UnityInspect.Component(comp);

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

            string result = UnityInspect.FindAsset("MissingAsset");
            Assert.Contains("Asset search failed: Asset database is busy", result);
        }

        [Fact]
        public void MainCamera_WhenCameraPresent_HandlesGracefullyWithoutCrashing()
        {
            var go = new GameObject("ThrowingCam");
            var cam = go.AddComponent<Camera>();
            cam.name = "ThrowingCamera";
            Camera.main = cam;

            string result = UnityInspect.MainCamera();
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

            string result = UnityInspect.Selection();
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

            string result = UnityInspect.Selection();
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

            string resPath = UnityInspect.Hierarchy("Assets/Scenes/SampleScene.unity");
            Assert.Contains("RootPlayer", resPath);

            string resExt = UnityInspect.Hierarchy("SampleScene.unity");
            Assert.Contains("RootPlayer", resExt);

            string resName = UnityInspect.Hierarchy("SampleScene");
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

            string result = UnityInspect.Hierarchy();
            Assert.Equal("(No GameObjects found in loaded scenes)", result);
        }

        [Fact]
        public void FindAsset_WhenFirstGuidHasEmptyPath_InspectsSubsequentGuids()
        {
            AssetDatabase.FindAssetsFunc = _ => new[] { "stale_guid", "valid_guid" };
            AssetDatabase.GUIDToAssetPathFunc = g => g == "valid_guid" ? "Assets/Valid.prefab" : "";

            string result = UnityInspect.FindAsset("TestAsset");
            Assert.Contains("Assets/Valid.prefab", result);
        }

        [Fact]
        public void FindAssets_WhenAllGuidsHaveEmptyPath_ReturnsCleanNotification()
        {
            AssetDatabase.FindAssetsFunc = _ => new[] { "stale_1", "stale_2" };
            AssetDatabase.GUIDToAssetPathFunc = _ => "";

            string result = UnityInspect.FindAssets("TestQuery");
            Assert.Contains("(No assets found matching query: 'TestQuery')", result);
        }

        [Fact]
        public void Hierarchy_TransformWithNullGameObject_ReturnsCleanNotification()
        {
            var tr = new Transform();
            tr.gameObject = null!;

            string result = UnityInspect.Hierarchy(tr);
            Assert.Equal("(Transform has no GameObject)", result);
        }

        [Fact]
        public void Hierarchy_CyclicTransformTree_HandlesCycleCleanly()
        {
            var nodeA = new GameObject("NodeA");
            var nodeB = new GameObject("NodeB");
            nodeA.transform.AddChild(nodeB.transform);
            nodeB.transform.AddChild(nodeA.transform);

            string result = UnityInspect.Hierarchy(nodeA);
            Assert.Contains("NodeA", result);
            Assert.Contains("NodeB", result);
            Assert.Contains("... (truncated: cycle detected)", result);
        }

        [Fact]
        public void Help_ReturnsAllDocumentedInspectMethods()
        {
            string unityInspectHelp = UnityInspect.Help();
            string inspectHelp = Inspect.Help();

            Assert.Equal(unityInspectHelp, inspectHelp);
            Assert.Contains("Hierarchy", inspectHelp);
            Assert.Contains("GameObject", inspectHelp);
            Assert.Contains("Component", inspectHelp);
            Assert.Contains("Object", inspectHelp);
            Assert.Contains("FindAssets", inspectHelp);
            Assert.Contains("FindPrefabs", inspectHelp);
            Assert.Contains("FindAsset", inspectHelp);
            Assert.Contains("Selection", inspectHelp);
            Assert.Contains("MainCamera", inspectHelp);
        }

        #endregion
    }
}
