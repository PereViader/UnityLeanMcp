using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityLeanMcp;
using Xunit;

namespace UnityLeanMcp.Mcp.Tests;

[Trait("Category", "Unit")]
[Collection("CommandGate")]
public sealed class UnityInspectHierarchyAndUiTests : IDisposable
{
    private readonly UnityInspectTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

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
        go.AddRawComponent(null);
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
    public void PrefabStage_WithMaxDepthZero_PrintsRootWithPrefixAndTruncatesChildren()
    {
        var rootGo = new GameObject("Hero");
        var childGo = new GameObject("Shield");
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

        string result = Inspect.PrefabStage(maxDepth: 0);

        Assert.Contains("  Hero [active] [Transform]", result);
        Assert.Contains("    ... (truncated: maximum depth reached)", result);
        Assert.DoesNotContain("Shield", result);
    }

    [Fact]
    public void PrefabStage_WithChildDepthLimit_PrintsThroughMaxDepthAndTruncatesDeeper()
    {
        var rootGo = new GameObject("Hero");
        var childGo = new GameObject("Shield");
        var grandchildGo = new GameObject("Emblem");
        rootGo.transform.AddChild(childGo.transform);
        childGo.transform.AddChild(grandchildGo.transform);

        var stage = new PrefabStage
        {
            assetPath = "Assets/Prefabs/Hero.prefab",
            mode = PrefabStage.Mode.InIsolation,
            prefabContentsRoot = rootGo,
            scene = new Scene { isDirty = false },
            autoSave = true
        };
        PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

        string result = Inspect.PrefabStage(maxDepth: 1);

        Assert.Contains("  Hero [active] [Transform]", result);
        Assert.Contains("    Shield [active] [Transform]", result);
        Assert.Contains("      ... (truncated: maximum depth reached)", result);
        Assert.DoesNotContain("Emblem", result);
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

        int firstIdx = result.IndexOf("... (truncated: maximum depth reached)");
        int lastIdx = result.LastIndexOf("... (truncated: maximum depth reached)");
        Assert.True(firstIdx >= 0, "Depth truncation marker missing");
        Assert.Equal(firstIdx, lastIdx);
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

    public class TestRaycastGraphic : Component
    {
        public bool raycastTarget { get; set; } = false;
    }

    public class TestTextElement : Component
    {
        public string text { get; set; } = "Start Game";
        public bool raycastTarget { get; set; } = true;
    }

    [Fact]
    public void GameObject_WithRectTransform_FormatsRectTransformMetrics()
    {
        var go = new GameObject("TestButton", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = new Vector2(10f, 20f);
        rt.sizeDelta = new Vector2(120f, 40f);
        rt.rect = new Rect(-60f, -20f, 120f, 40f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);

        string result = Inspect.GameObject(go);

        Assert.Contains("RectTransform: anchoredPos=(10.00, 20.00), sizeDelta=(120.00, 40.00), rect=(120.00x40.00)", result);
        Assert.Contains("anchors=[(0.50, 0.50) to (0.50, 0.50)]", result);
        Assert.Contains("pivot=(0.50, 0.50)", result);
    }

    [Fact]
    public void GameObject_WithCanvas_FormatsCanvasSettings()
    {
        var go = new GameObject("RootCanvas", typeof(RectTransform), typeof(Canvas));
        var canvas = go.GetComponent<Canvas>()!;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5;
        canvas.sortingLayerName = "UI";

        string result = Inspect.GameObject(go);

        Assert.Contains("Canvas: renderMode=ScreenSpaceOverlay, sortingOrder=5, sortingLayer=\"UI\"", result);
    }

    [Fact]
    public void GameObject_WithUIDocument_FormatsUIDocumentHeader()
    {
        var go = new GameObject("UIDocHost", typeof(UnityEngine.UIElements.UIDocument));
        var doc = go.GetComponent<UnityEngine.UIElements.UIDocument>()!;
        doc.rootVisualElement = new UnityEngine.UIElements.VisualElement();
        doc.rootVisualElement.Add(new UnityEngine.UIElements.Label("Hello"));
        doc.sortingOrder = 2;

        string result = Inspect.GameObject(go);

        Assert.Contains("UIDocument: sortingOrder=2, rootVisualElement children=1", result);
    }

    [Fact]
    public void Component_RectTransform_FormatsLayoutAndChildren()
    {
        var go = new GameObject("Panel", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(400f, 300f);
        rt.rect = new Rect(-200f, -150f, 400f, 300f);

        string result = Inspect.Component(rt);

        Assert.Contains("RectTransform: anchoredPos=(0.00, 0.00), sizeDelta=(400.00, 300.00), rect=(400.00x300.00)", result);
    }

    [Fact]
    public void Component_Canvas_FormatsSettings()
    {
        var go = new GameObject("CanvasGo", typeof(Canvas));
        var canvas = go.GetComponent<Canvas>()!;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 10;
        canvas.sortingLayerName = "Overlay";

        string result = Inspect.Component(canvas);

        Assert.Contains("Canvas: renderMode=WorldSpace, sortingOrder=10, sortingLayer=\"Overlay\"", result);
    }

    [Fact]
    public void Component_UIDocument_FormatsVisualTree()
    {
        var go = new GameObject("Host", typeof(UnityEngine.UIElements.UIDocument));
        var doc = go.GetComponent<UnityEngine.UIElements.UIDocument>()!;
        doc.sortingOrder = 1;
        var root = new UnityEngine.UIElements.VisualElement { name = "container" };
        root.AddToClassList("main-box");
        root.Add(new UnityEngine.UIElements.Button(null) { text = "Press Me" });
        doc.rootVisualElement = root;

        string result = Inspect.Component(doc);

        Assert.Contains("UIDocument: sortingOrder=1", result);
        Assert.Contains("VisualTree:", result);
        Assert.Contains("VisualElement #container .main-box", result);
        Assert.Contains("Button (text: \"Press Me\")", result);
    }

    [Fact]
    public void Selection_WithRectTransform_FormatsRectTransform()
    {
        var go = new GameObject("SelectedUI", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = new Vector2(50f, 75f);
        rt.sizeDelta = new Vector2(200f, 50f);
        rt.rect = new Rect(-100f, -25f, 200f, 50f);
        Selection.activeGameObject = go;

        string result = Inspect.Selection();

        Assert.Contains("RectTransform: anchoredPos=(50.00, 75.00), sizeDelta=(200.00, 50.00), rect=(200.00x50.00)", result);
    }

    [Fact]
    public void Hierarchy_WithUiDetails_FormatsRectAndCanvasAndText()
    {
        var canvasGo = new GameObject("MainCanvas", typeof(RectTransform), typeof(Canvas));
        var canvas = canvasGo.GetComponent<Canvas>()!;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 0;
        var canvasRt = (RectTransform)canvasGo.transform;
        canvasRt.rect = new Rect(0, 0, 1920, 1080);

        var buttonGo = new GameObject("StartBtn", typeof(RectTransform));
        var btnRt = (RectTransform)buttonGo.transform;
        btnRt.anchoredPosition = new Vector2(0, -100);
        btnRt.rect = new Rect(-80, -20, 160, 40);
        var textComp = buttonGo.AddComponent<TestTextElement>();
        textComp.text = "Start Adventure";
        canvasRt.AddChild(btnRt);

        string result = Inspect.Hierarchy(canvasGo, includeUiDetails: true);

        Assert.Contains("MainCanvas [active] [RectTransform, Canvas] (canvas: ScreenSpaceOverlay, order=0 | pos: 0, 0 | size: 1920x1080)", result);
        Assert.Contains("StartBtn [active] [RectTransform, TestTextElement] (pos: 0, -100 | size: 160x40 | text: \"Start Adventure\")", result);
    }

    [Fact]
    public void Hierarchy_WithUiDetails_IncludesRaycastFalse()
    {
        var panelGo = new GameObject("Panel", typeof(RectTransform));
        var rt = (RectTransform)panelGo.transform;
        rt.rect = new Rect(0, 0, 300, 200);
        panelGo.AddComponent<TestRaycastGraphic>();

        string result = Inspect.Hierarchy(panelGo, includeUiDetails: true);

        Assert.Contains("(pos: 0, 0 | size: 300x200 | raycast: false)", result);
    }

    [Fact]
    public void UIDocument_NoDocuments_ReturnsEmptyNotification()
    {
        string result = Inspect.UIDocument();
        Assert.Contains("(No UIDocument found in loaded scenes or Prefab Stage)", result);
    }

    [Fact]
    public void UIDocument_WithDocument_FormatsVisualElementTree()
    {
        var go = new GameObject("HUDDoc", typeof(UnityEngine.UIElements.UIDocument));
        var doc = go.GetComponent<UnityEngine.UIElements.UIDocument>()!;
        doc.name = "HUD";
        doc.sortingOrder = 3;

        var root = new UnityEngine.UIElements.VisualElement { name = "root-panel" };
        root.layout = new Rect(0, 0, 1920, 1080);
        root.AddToClassList("root-style");

        var label = new UnityEngine.UIElements.Label("Score: 100") { name = "score-label" };
        label.layout = new Rect(10, 10, 200, 30);
        label.AddToClassList("label-style");
        root.Add(label);

        var button = new UnityEngine.UIElements.Button(null) { name = "pause-btn", visible = false, enabledSelf = false };
        button.layout = new Rect(1800, 10, 50, 50);
        button.text = "Pause";
        root.Add(button);

        doc.rootVisualElement = root;

        string result = Inspect.UIDocument(doc);

        Assert.Contains("=== UIDocument: \"HUD\" (GameObject: \"HUDDoc\", SortingOrder: 3) ===", result);
        Assert.Contains("VisualElement #root-panel .root-style (size: 1920x1080)", result);
        Assert.Contains("Label #score-label .label-style (pos: 10, 10 | size: 200x30 | text: \"Score: 100\")", result);
        Assert.Contains("Button #pause-btn [hidden, disabled] (pos: 1800, 10 | size: 50x50 | text: \"Pause\")", result);
    }

    [Fact]
    public void UIDocument_VisualElementRoot_FormatsSubtree()
    {
        var container = new UnityEngine.UIElements.VisualElement { name = "dialog" };
        container.layout = new Rect(100, 100, 400, 250);
        var btn = new UnityEngine.UIElements.Button(null) { text = "OK" };
        btn.layout = new Rect(150, 180, 100, 40);
        container.Add(btn);

        string result = Inspect.UIDocument(container);

        Assert.Contains("VisualElement #dialog (pos: 100, 100 | size: 400x250)", result);
        Assert.Contains("Button (pos: 150, 180 | size: 100x40 | text: \"OK\")", result);
    }

    [Fact]
    public void UIDocument_FromGameObject_ReturnsResultOrMissingNotification()
    {
        var emptyGo = new GameObject("EmptyObj");
        string missingResult = Inspect.UIDocument(emptyGo);
        Assert.Contains("(GameObject \"EmptyObj\" has no UIDocument component)", missingResult);

        var docGo = new GameObject("DocObj", typeof(UnityEngine.UIElements.UIDocument));
        var doc = docGo.GetComponent<UnityEngine.UIElements.UIDocument>()!;
        doc.rootVisualElement = new UnityEngine.UIElements.VisualElement { name = "root" };
        string foundResult = Inspect.UIDocument(docGo);
        Assert.Contains("=== UIDocument:", foundResult);
        Assert.Contains("VisualElement #root", foundResult);
    }

    [Fact]
    public void UIDocument_TruncatesAtMaxDepthAndMaxItems()
    {
        var root = new UnityEngine.UIElements.VisualElement { name = "L0" };
        var current = root;
        for (int i = 1; i <= 10; i++)
        {
            var next = new UnityEngine.UIElements.VisualElement { name = $"L{i}" };
            current.Add(next);
            current = next;
        }

        string depthResult = Inspect.UIDocument(root, maxDepth: 3);
        Assert.Contains("L0", depthResult);
        Assert.Contains("L3", depthResult);
        Assert.Contains("... (truncated: maximum depth reached)", depthResult);
        Assert.DoesNotContain("L5", depthResult);

        var wideRoot = new UnityEngine.UIElements.VisualElement { name = "Wide" };
        for (int i = 0; i < 20; i++)
        {
            wideRoot.Add(new UnityEngine.UIElements.VisualElement { name = $"Child_{i}" });
        }

        string itemsResult = Inspect.UIDocument(wideRoot, maxItems: 5);
        Assert.Contains("Wide", itemsResult);
        Assert.Contains("... (truncated: maximum item count reached)", itemsResult);
    }

    [Fact]
    public void Object_WithVisualElement_TraversesElementTree()
    {
        var ve = new UnityEngine.UIElements.VisualElement { name = "polyVE" };
        ve.layout = new Rect(0, 0, 100, 100);
        ve.Add(new UnityEngine.UIElements.Label("Polymorphic"));

        string result = Inspect.Object(ve);

        Assert.Contains("VisualElement #polyVE (size: 100x100)", result);
        Assert.Contains("Label (text: \"Polymorphic\")", result);
    }

    public class EventTargetComponent : MonoBehaviour
    {
        public void ValidVoidAction() { }
        public void ValidIntAction(int count) { }
        public void ValidObjectAction(GameObject go) { }
    }

    public class EventSystem : Behaviour { }

    public class CustomGraphicComponent : Behaviour
    {
        public Material? sharedMaterial { get; set; }
    }

    [Fact]
    public void Audit_WhenNoScenesOrPrefabStageLoaded_ReturnsCleanNotification()
    {
        string result = Inspect.Audit();
        Assert.Equal("(No scenes currently loaded)", result);
    }

    [Fact]
    public void Audit_NullOrDestroyedGameObject_ReturnsCleanNotification()
    {
        GameObject nullGo = null!;
        Assert.Equal("(GameObject is null)", Inspect.Audit(nullGo));

        var deadGo = new GameObject("DeadGo");
        UnityEngine.Object.DestroyImmediate(deadGo);
        Assert.Equal("(GameObject is destroyed)", Inspect.Audit(deadGo));
    }

    [Fact]
    public void Audit_CleanSceneWithMainCamera_ReportsZeroIssues()
    {
        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";

        var scene = new Scene
        {
            name = "CleanScene",
            isLoaded = true,
            RootObjects = new List<GameObject> { camGo }
        };
        SceneManager.Scenes.Add(scene);

        string result = Inspect.Audit();
        Assert.Contains("Audit of 1 loaded scene(s): 0 issues found (1 GameObject(s) scanned).", result);
    }

    [Fact]
    public void Audit_DetectsAllIntegrityCategoriesInScene()
    {
        var brokenGo = new GameObject("Player");
        var childGo = new GameObject("HUDButton");
        brokenGo.transform.AddChild(childGo.transform);

        // 1. Missing script
        brokenGo.AddRawComponent(null);

        // 2. Broken ObjectReference (null value with non-zero instanceID) + valid null reference (instanceID == 0)
        var targetComp = brokenGo.AddComponent<EventTargetComponent>();
        SerializedObject.CustomProperties[targetComp] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_WeaponPrefab",
                propertyPath = "m_WeaponPrefab",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = null,
                objectReferenceInstanceIDValue = 4242
            },
            new SerializedProperty
            {
                name = "m_OptionalEffect",
                propertyPath = "m_OptionalEffect",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = null,
                objectReferenceInstanceIDValue = 0
            }
        };

        // 3. Broken UnityEvent listeners (missing target, missing method, and valid method)
        var buttonComp = childGo.AddComponent<EventTargetComponent>();
        var onClickProp = CreateUnityEventProperty(
            "m_OnClick",
            CreatePersistentCall(target: null, targetInstanceId: 9999, methodName: "OnFire", mode: 1),
            CreatePersistentCall(target: targetComp, targetInstanceId: 100, methodName: "RenamedOrDeletedMethod", mode: 1),
            CreatePersistentCall(target: targetComp, targetInstanceId: 100, methodName: "ValidVoidAction", mode: 1));
        SerializedObject.CustomProperties[buttonComp] = new List<SerializedProperty> { onClickProp };

        // 4. Error shaders (Hidden/InternalErrorShader, null shader, and ShaderUtil compilation error)
        var renderer = brokenGo.AddComponent<Renderer>();
        var pinkMat = new Material(new Shader { name = "Hidden/InternalErrorShader" }) { name = "PinkMat" };
        var nullShaderMat = new Material(null) { name = "NullShaderMat" };
        var brokenCustomShader = new Shader { name = "Custom/BrokenLit" };
        var brokenShaderMat = new Material(brokenCustomShader) { name = "BrokenLitMat" };
        ShaderUtil.ShaderHasErrorFunc = s => ReferenceEquals(s, brokenCustomShader);
        renderer.sharedMaterials = new[] { pinkMat, nullShaderMat, brokenShaderMat };

        var graphic = childGo.AddComponent<CustomGraphicComponent>();
        graphic.sharedMaterial = pinkMat;

        // 5. Scene hygiene: duplicate AudioListener, duplicate EventSystem, and missing MainCamera tag (untagged Camera present)
        brokenGo.AddComponent<Camera>(); // Untagged!
        brokenGo.AddComponent<AudioListener>();
        childGo.AddComponent<AudioListener>();
        brokenGo.AddComponent<EventSystem>();
        childGo.AddComponent<EventSystem>();

        var scene = new Scene
        {
            name = "GameplayScene",
            path = "Assets/Scenes/GameplayScene.unity",
            isLoaded = true,
            RootObjects = new List<GameObject> { brokenGo }
        };
        SceneManager.Scenes.Add(scene);

        string result = Inspect.Audit();

        Assert.Contains("Audit of 1 loaded scene(s): 11 issue(s) found (2 GameObject(s) scanned):", result);
        Assert.Contains("Missing Scripts (1):", result);
        Assert.Contains("Player (path: \"/Player\", scene: \"GameplayScene\"): 1 missing script at component index [1]", result);

        Assert.Contains("Broken Object References (1):", result);
        Assert.Contains("Player (path: \"/Player\", scene: \"GameplayScene\"): EventTargetComponent.m_WeaponPrefab is Missing (instanceID: 4242)", result);
        Assert.DoesNotContain("m_OptionalEffect", result);

        Assert.Contains("Broken UnityEvent Listeners (2):", result);
        Assert.Contains("HUDButton (path: \"/Player/HUDButton\", scene: \"GameplayScene\"): EventTargetComponent.m_OnClick[0] has missing target object (instanceID: 9999) (method: \"OnFire\")", result);
        Assert.Contains("HUDButton (path: \"/Player/HUDButton\", scene: \"GameplayScene\"): EventTargetComponent.m_OnClick[1] method \"EventTargetComponent.RenamedOrDeletedMethod\" not found on target \"EventTargetComponent\"", result);
        Assert.DoesNotContain("ValidVoidAction", result);

        Assert.Contains("Material & Shader Errors (4):", result);
        Assert.Contains("Renderer.sharedMaterials[0] (\"PinkMat\") uses error shader \"Hidden/InternalErrorShader\"", result);
        Assert.Contains("Renderer.sharedMaterials[1] (\"NullShaderMat\") has missing/null Shader", result);
        Assert.Contains("Renderer.sharedMaterials[2] (\"BrokenLitMat\") shader \"Custom/BrokenLit\" has compilation errors", result);
        Assert.Contains("CustomGraphicComponent.sharedMaterial (\"PinkMat\") uses error shader \"Hidden/InternalErrorShader\"", result);

        Assert.Contains("Scene Hygiene Issues (3):", result);
        Assert.Contains("Duplicate active AudioListener components (2) in scene: \"GameplayScene\": [/Player, /Player/HUDButton]", result);
        Assert.Contains("Duplicate active EventSystem components (2) in scene: \"GameplayScene\": [/Player, /Player/HUDButton]", result);
        Assert.Contains("Missing \"MainCamera\" tag in scene: \"GameplayScene\" (1 untagged Camera component(s) present)", result);
    }

    [Fact]
    public void Audit_GameObjectRoot_ScansSubtreeOnlyAndOmitsSceneMainCameraWarning()
    {
        var root = new GameObject("EnemyRoot");
        var child = new GameObject("EnemyChild");
        root.transform.AddChild(child.transform);
        child.AddRawComponent(null);

        string result = Inspect.Audit(root);

        Assert.Contains("Audit of GameObject \"EnemyRoot\"", result);
        Assert.Contains("1 issue(s) found (2 GameObject(s) scanned)", result);
        Assert.Contains("EnemyChild (path: \"/EnemyRoot/EnemyChild\"", result);
        Assert.DoesNotContain("MainCamera", result);
    }

    [Fact]
    public void Audit_PrefabAssetPath_LoadsAndUnloadsPrefabContentsSafely()
    {
        var prefabRoot = new GameObject("ChestPrefab");
        prefabRoot.AddRawComponent(null);

        bool loaded = false;
        bool unloaded = false;

        AssetDatabase.LoadMainAssetAtPathFunc = path =>
            path == "Assets/Prefabs/Chest.prefab" ? prefabRoot : null;
        PrefabUtility.LoadPrefabContentsFunc = path =>
        {
            if (path == "Assets/Prefabs/Chest.prefab")
            {
                loaded = true;
                return prefabRoot;
            }
            return null;
        };
        PrefabUtility.UnloadPrefabContentsAction = go =>
        {
            if (ReferenceEquals(go, prefabRoot))
            {
                unloaded = true;
            }
        };

        string result = Inspect.Audit("Assets/Prefabs/Chest.prefab");

        Assert.True(loaded);
        Assert.True(unloaded);
        Assert.Contains("Audit of Prefab Asset \"Assets/Prefabs/Chest.prefab\": 1 issue(s) found (1 GameObject(s) scanned):", result);
        Assert.Contains("ChestPrefab (path: \"/ChestPrefab\", prefab: \"Assets/Prefabs/Chest.prefab\"): 1 missing script", result);
    }

    [Fact]
    public void Audit_FolderTarget_ScansAllPrefabsInFolderWithoutItemCap()
    {
        // Create 120 prefabs (> DefaultMaxItems = 100) to verify there is no maxItems limit on folder audits
        const int prefabCount = 120;
        var prefabsByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var guidsToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < prefabCount; i++)
        {
            string guid = $"guid_prefab_{i:D3}";
            string path = $"Assets/Prefabs/Props/Prop_{i:D3}.prefab";
            var go = new GameObject($"Prop_{i:D3}");
            if (i == 0 || i == 119)
            {
                go.AddRawComponent(null);
            }
            prefabsByPath[path] = go;
            guidsToPath[guid] = path;
        }

        int unloadedCount = 0;
        AssetDatabase.IsValidFolderFunc = path =>
            string.Equals(path, "Assets/Prefabs/Props", StringComparison.OrdinalIgnoreCase);
        AssetDatabase.FindAssetsInFoldersFunc = (filter, folders) =>
            folders.Length == 1 && folders[0] == "Assets/Prefabs/Props"
                ? new List<string>(guidsToPath.Keys).ToArray()
                : Array.Empty<string>();
        AssetDatabase.GUIDToAssetPathFunc = guid =>
            guidsToPath.TryGetValue(guid, out var p) ? p : string.Empty;
        PrefabUtility.LoadPrefabContentsFunc = path =>
            prefabsByPath.TryGetValue(path, out var go) ? go : null;
        PrefabUtility.UnloadPrefabContentsAction = _ => unloadedCount++;

        string result = Inspect.Audit("Assets/Prefabs/Props/");

        Assert.Equal(prefabCount, unloadedCount);
        Assert.Contains("Audit of Folder \"Assets/Prefabs/Props\" (120 prefab(s)): 2 issue(s) found (120 GameObject(s) scanned):", result);
        Assert.Contains("Prop_000 (path: \"/Prop_000\", prefab: \"Assets/Prefabs/Props/Prop_000.prefab\"): 1 missing script", result);
        Assert.Contains("Prop_119 (path: \"/Prop_119\", prefab: \"Assets/Prefabs/Props/Prop_119.prefab\"): 1 missing script", result);
        Assert.DoesNotContain(Inspect.ItemTruncationMarker, result);
    }

    [Fact]
    public void Audit_PrefabStageAndSpecificSceneAndEmptyFolder_HandlesEdgeCases()
    {
        var stageRoot = new GameObject("StageHero");
        stageRoot.AddRawComponent(null);
        var stage = new PrefabStage
        {
            assetPath = "Assets/Prefabs/StageHero.prefab",
            prefabContentsRoot = stageRoot,
            scene = new Scene { isDirty = false }
        };
        PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

        string stageAudit = Inspect.Audit("PrefabStage");
        Assert.Contains("Audit of Prefab Stage \"Assets/Prefabs/StageHero.prefab\": 1 issue(s) found", stageAudit);
        Assert.Contains("StageHero (path: \"/StageHero\", prefabStage: \"Assets/Prefabs/StageHero.prefab\"): 1 missing script", stageAudit);

        var unloadedScene = new Scene { name = "UnloadedScene", isLoaded = false };
        SceneManager.Scenes.Add(unloadedScene);
        Assert.Equal("(Scene \"UnloadedScene\" is not currently loaded)", Inspect.Audit("UnloadedScene"));

        AssetDatabase.IsValidFolderFunc = path => path == "Assets/EmptyPrefabs";
        AssetDatabase.FindAssetsInFoldersFunc = (_, _) => Array.Empty<string>();
        Assert.Equal("(No prefab assets found in folder: \"Assets/EmptyPrefabs\")", Inspect.Audit("Assets/EmptyPrefabs"));

        AssetDatabase.LoadMainAssetAtPathFunc = _ => null;
        AssetDatabase.AssetPathToGUIDFunc = _ => string.Empty;
        Assert.Equal("(Audit target not found: \"Assets/NonExistent/MissingPrefab.prefab\")", Inspect.Audit("Assets/NonExistent/MissingPrefab.prefab"));
    }

    [Fact]
    public void Audit_LargeOutput_TruncatesAt64KiBOutputLimit()
    {
        var root = new GameObject("MassiveBrokenRoot");
        for (int i = 0; i < 1200; i++)
        {
            var child = new GameObject($"BrokenChildWithLongDescriptiveHierarchyName_{i:D4}");
            child.AddRawComponent(null);
            root.transform.AddChild(child.transform);
        }

        string result = Inspect.Audit(root, maxIssuesPerCategory: 2000);

        Assert.True(result.Length <= Inspect.DefaultMaxOutputCharacters);
        Assert.EndsWith(Inspect.OutputTruncationMarker, result);
    }

    [Fact]
    public void Audit_FolderTarget_RespectsMaxPrefabsCapAndReportsTruncation()
    {
        const int totalPrefabs = 10;
        var prefabsByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var guidsToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < totalPrefabs; i++)
        {
            string guid = $"guid_cap_{i:D2}";
            string path = $"Assets/Prefabs/CapTest/Item_{i:D2}.prefab";
            var go = new GameObject($"Item_{i:D2}");
            prefabsByPath[path] = go;
            guidsToPath[guid] = path;
        }

        int loadedCount = 0;
        int unloadedCount = 0;
        AssetDatabase.IsValidFolderFunc = path =>
            string.Equals(path, "Assets/Prefabs/CapTest", StringComparison.OrdinalIgnoreCase);
        AssetDatabase.FindAssetsInFoldersFunc = (_, _) => new List<string>(guidsToPath.Keys).ToArray();
        AssetDatabase.GUIDToAssetPathFunc = guid =>
            guidsToPath.TryGetValue(guid, out var p) ? p : string.Empty;
        PrefabUtility.LoadPrefabContentsFunc = path =>
        {
            loadedCount++;
            return prefabsByPath.TryGetValue(path, out var go) ? go : null;
        };
        PrefabUtility.UnloadPrefabContentsAction = _ => unloadedCount++;

        string result = Inspect.Audit("Assets/Prefabs/CapTest", maxPrefabs: 4);

        Assert.Equal(4, loadedCount);
        Assert.Equal(4, unloadedCount);
        Assert.Contains("Folder \"Assets/Prefabs/CapTest\" (4 of 10 prefab(s) scanned)", result);
        Assert.Contains("reached maxPrefabs limit of 4; 4 of 10 prefab(s) scanned", result);
    }

    [Fact]
    public void Audit_FolderTarget_TriggersPeriodicUnloadUnusedAssetsImmediate()
    {
        const int prefabCount = 120;
        var prefabsByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var guidsToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < prefabCount; i++)
        {
            string guid = $"guid_cleanup_{i:D3}";
            string path = $"Assets/Prefabs/Cleanup/Item_{i:D3}.prefab";
            prefabsByPath[path] = new GameObject($"Item_{i:D3}");
            guidsToPath[guid] = path;
        }

        int unloadUnusedCount = 0;
        EditorUtility.UnloadUnusedAssetsImmediateAction = () => unloadUnusedCount++;
        AssetDatabase.IsValidFolderFunc = path =>
            string.Equals(path, "Assets/Prefabs/Cleanup", StringComparison.OrdinalIgnoreCase);
        AssetDatabase.FindAssetsInFoldersFunc = (_, _) => new List<string>(guidsToPath.Keys).ToArray();
        AssetDatabase.GUIDToAssetPathFunc = guid =>
            guidsToPath.TryGetValue(guid, out var p) ? p : string.Empty;
        PrefabUtility.LoadPrefabContentsFunc = path =>
            prefabsByPath.TryGetValue(path, out var go) ? go : null;

        string result = Inspect.Audit("Assets/Prefabs/Cleanup", maxPrefabs: 120);

        Assert.Equal(2, unloadUnusedCount);
        Assert.Contains("Folder \"Assets/Prefabs/Cleanup\" (120 prefab(s)): 0 issues found", result);
    }

    [Fact]
    public void Audit_PerCategoryIssueCap_TruncatesCategoryAndStopsEarlyWhenAllCategoriesFull()
    {
        var root = new GameObject("RootWithManyMissingScripts");
        for (int i = 0; i < 10; i++)
        {
            var child = new GameObject($"Broken_{i}");
            child.AddRawComponent(null);
            root.transform.AddChild(child.transform);
        }

        string singleRootResult = Inspect.Audit(root, maxIssuesPerCategory: 3);
        Assert.Contains("3 issue(s) reported (7 additional omitted)", singleRootResult);
        Assert.Contains("Missing Scripts (3+, 7 omitted):", singleRootResult);
        Assert.Contains("7 more omitted; maxIssuesPerCategory=3", singleRootResult);

        // Folder early-exit test: 10 prefabs, each hits all 5 issue categories, cap = 2 per category
        var prefabsByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var guidsToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var brokenMat = new Material { name = "BrokenMat", shader = new Shader { name = "Hidden/InternalErrorShader" } };

        for (int i = 0; i < 10; i++)
        {
            string guid = $"guid_full_{i:D2}";
            string path = $"Assets/Prefabs/FullTest/Full_{i:D2}.prefab";
            var prefabRoot = new GameObject($"Full_{i:D2}");
            prefabRoot.AddRawComponent(null); // MissingScript
            var comp = prefabRoot.AddComponent<Renderer>();
            comp.sharedMaterials = new[] { brokenMat }; // MaterialAndShaderIssue

            var refProp = new SerializedProperty
            {
                name = "m_MissingRef",
                propertyPath = "m_MissingRef",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = null,
                objectReferenceInstanceIDValue = 999
            };
            var eventProp = CreateUnityEventProperty("m_OnClick", CreatePersistentCall(null, 0, "DoWork", 1));
            SerializedObject.CustomProperties[comp] = new List<SerializedProperty> { refProp, eventProp };

            var listenerA = new GameObject("AL1");
            listenerA.AddComponent<AudioListener>();
            var listenerB = new GameObject("AL2");
            listenerB.AddComponent<AudioListener>();
            prefabRoot.transform.AddChild(listenerA.transform);
            prefabRoot.transform.AddChild(listenerB.transform); // SceneHygieneIssue (duplicate AudioListener)

            prefabsByPath[path] = prefabRoot;
            guidsToPath[guid] = path;
        }

        int loadedCount = 0;
        AssetDatabase.IsValidFolderFunc = path =>
            string.Equals(path, "Assets/Prefabs/FullTest", StringComparison.OrdinalIgnoreCase);
        AssetDatabase.FindAssetsInFoldersFunc = (_, _) => new List<string>(guidsToPath.Keys).ToArray();
        AssetDatabase.GUIDToAssetPathFunc = guid =>
            guidsToPath.TryGetValue(guid, out var p) ? p : string.Empty;
        PrefabUtility.LoadPrefabContentsFunc = path =>
        {
            loadedCount++;
            return prefabsByPath.TryGetValue(path, out var go) ? go : null;
        };

        string folderResult = Inspect.Audit("Assets/Prefabs/FullTest", maxPrefabs: 10, maxIssuesPerCategory: 2);

        Assert.Equal(2, loadedCount);
        Assert.Contains("2 of 10 prefab(s) scanned", folderResult);
        Assert.Contains("output or per-category issue limit reached", folderResult);
    }

    [Fact]
    public void Audit_FolderTarget_HonorsCallerCancellationAndActiveOperationToken()
    {
        var prefabsByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var guidsToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 10; i++)
        {
            string guid = $"guid_cancel_{i:D2}";
            string path = $"Assets/Prefabs/CancelTest/Item_{i:D2}.prefab";
            prefabsByPath[path] = new GameObject($"Item_{i:D2}");
            guidsToPath[guid] = path;
        }

        AssetDatabase.IsValidFolderFunc = path =>
            string.Equals(path, "Assets/Prefabs/CancelTest", StringComparison.OrdinalIgnoreCase);
        AssetDatabase.FindAssetsInFoldersFunc = (_, _) => new List<string>(guidsToPath.Keys).ToArray();
        AssetDatabase.GUIDToAssetPathFunc = guid =>
            guidsToPath.TryGetValue(guid, out var p) ? p : string.Empty;

        // 1. Caller CancellationToken
        using (var cts = new CancellationTokenSource())
        {
            int loadedCount = 0;
            int unloadedCount = 0;
            PrefabUtility.LoadPrefabContentsFunc = path =>
            {
                loadedCount++;
                if (loadedCount == 3)
                {
                    cts.Cancel();
                }
                return prefabsByPath.TryGetValue(path, out var go) ? go : null;
            };
            PrefabUtility.UnloadPrefabContentsAction = _ => unloadedCount++;

            Assert.Throws<OperationCanceledException>(() =>
                Inspect.Audit("Assets/Prefabs/CancelTest", maxPrefabs: 10, cancellationToken: cts.Token));
            Assert.Equal(3, loadedCount);
            Assert.Equal(3, unloadedCount);
        }

        // 2. Active OperationExecutionEngine token (without explicit caller token)
        const string opId = "audit_cancel_op";
        OperationExecutionEngine.RegisterActiveOperation(opId, isCancelable: true);
        try
        {
            Assert.True(OperationExecutionEngine.GetActiveOperationToken().CanBeCanceled);

            int loadedCount = 0;
            int unloadedCount = 0;
            PrefabUtility.LoadPrefabContentsFunc = path =>
            {
                loadedCount++;
                if (loadedCount == 2)
                {
                    OperationExecutionEngine.TryCancel(opId);
                }
                return prefabsByPath.TryGetValue(path, out var go) ? go : null;
            };
            PrefabUtility.UnloadPrefabContentsAction = _ => unloadedCount++;

            Assert.Throws<OperationCanceledException>(() =>
                Inspect.Audit("Assets/Prefabs/CancelTest", maxPrefabs: 10));
            Assert.Equal(2, loadedCount);
            Assert.Equal(2, unloadedCount);
        }
        finally
        {
            OperationExecutionEngine.FinishOperation(opId, OperationKinds.Eval, string.Empty, true, string.Empty, 0, null);
        }

        Assert.Equal(CancellationToken.None, OperationExecutionEngine.GetActiveOperationToken());
    }

    [Fact]
    public void Audit_SkipsTransformAndRectTransformSerializedObjectCreation()
    {
        var regularGo = new GameObject("Regular");
        var uiGo = new GameObject("Panel", typeof(RectTransform));
        regularGo.transform.AddChild(uiGo.transform);

        var regularComp = regularGo.AddComponent<EventTargetComponent>();
        var uiComp = uiGo.AddComponent<EventTargetComponent>();

        // Even if custom properties are attached to Transform/RectTransform, they must not be inspected
        SerializedObject.CustomProperties[regularGo.transform] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_BogusRef",
                propertyPath = "m_BogusRef",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = null,
                objectReferenceInstanceIDValue = 111
            }
        };
        SerializedObject.CustomProperties[uiGo.transform] = new List<SerializedProperty>
        {
            new SerializedProperty
            {
                name = "m_BogusUiRef",
                propertyPath = "m_BogusUiRef",
                propertyType = SerializedPropertyType.ObjectReference,
                objectReferenceValue = null,
                objectReferenceInstanceIDValue = 222
            }
        };

        string result = Inspect.Audit(regularGo);

        Assert.Contains("0 issues found", result);
        Assert.DoesNotContain(SerializedObject.CreatedTargets, t => t is Transform);
        Assert.Contains(regularComp, SerializedObject.CreatedTargets);
        Assert.Contains(uiComp, SerializedObject.CreatedTargets);
    }

    [Fact]
    public void Audit_GuardsUnityEventLookupToNonArrayGenericPropertiesOnly()
    {
        var go = new GameObject("GuardTest");
        var comp = go.AddComponent<EventTargetComponent>();

        var arrayOfBytes = new SerializedProperty
        {
            name = "m_Bytes",
            propertyPath = "m_Bytes",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };
        arrayOfBytes.AddArrayElement(new SerializedProperty { propertyType = SerializedPropertyType.Integer, intValue = 1 });

        var genericStruct = new SerializedProperty
        {
            name = "m_Settings",
            propertyPath = "m_Settings",
            propertyType = SerializedPropertyType.Generic,
            isArray = false
        };

        var unityEvent = CreateUnityEventProperty(
            "m_OnClick",
            CreatePersistentCall(target: comp, targetInstanceId: 10, methodName: "ValidVoidAction", mode: 1));

        SerializedObject.CustomProperties[comp] = new List<SerializedProperty>
        {
            new SerializedProperty { name = "m_Script", propertyPath = "m_Script", propertyType = SerializedPropertyType.ObjectReference },
            new SerializedProperty { name = "m_Count", propertyPath = "m_Count", propertyType = SerializedPropertyType.Integer, intValue = 5 },
            new SerializedProperty { name = "m_Speed", propertyPath = "m_Speed", propertyType = SerializedPropertyType.Float, floatValue = 1.5f },
            new SerializedProperty { name = "m_EnabledFlag", propertyPath = "m_EnabledFlag", propertyType = SerializedPropertyType.Boolean, boolValue = true },
            new SerializedProperty { name = "m_Label", propertyPath = "m_Label", propertyType = SerializedPropertyType.String, stringValue = "abc" },
            new SerializedProperty { name = "m_Pos", propertyPath = "m_Pos", propertyType = SerializedPropertyType.Vector3 },
            new SerializedProperty { name = "m_Color", propertyPath = "m_Color", propertyType = SerializedPropertyType.Color },
            new SerializedProperty { name = "m_Mode", propertyPath = "m_Mode", propertyType = SerializedPropertyType.Enum },
            new SerializedProperty { name = "m_TargetRef", propertyPath = "m_TargetRef", propertyType = SerializedPropertyType.ObjectReference, objectReferenceValue = comp },
            arrayOfBytes,
            genericStruct,
            unityEvent
        };

        string result = Inspect.Audit(go);

        Assert.Contains("0 issues found", result);

        var lookupPaths = SerializedProperty.FindPropertyRelativeCalls
            .Select(c => c.PropertyPath)
            .Distinct()
            .ToList();

        Assert.DoesNotContain("m_Script", lookupPaths);
        Assert.DoesNotContain("m_Count", lookupPaths);
        Assert.DoesNotContain("m_Speed", lookupPaths);
        Assert.DoesNotContain("m_EnabledFlag", lookupPaths);
        Assert.DoesNotContain("m_Label", lookupPaths);
        Assert.DoesNotContain("m_Pos", lookupPaths);
        Assert.DoesNotContain("m_Color", lookupPaths);
        Assert.DoesNotContain("m_Mode", lookupPaths);
        Assert.DoesNotContain("m_TargetRef", lookupPaths);
        Assert.DoesNotContain("m_Bytes", lookupPaths);
        Assert.Contains("m_Settings", lookupPaths);
        Assert.Contains("m_OnClick", lookupPaths);
    }

    [Fact]
    public void Audit_SkipsPrimitiveAndEmptyArraysWhileAuditingObjectReferenceAndGenericArrays()
    {
        var go = new GameObject("ArrayAuditTest");
        var comp = go.AddComponent<EventTargetComponent>();

        // 1. Primitive array (byte[] / Integer): enterChildren must be false and child elements must not be visited
        var byteArray = new SerializedProperty
        {
            name = "m_ByteData",
            propertyPath = "m_ByteData",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };
        byteArray.AddArrayElement(new SerializedProperty { propertyType = SerializedPropertyType.Integer, intValue = 42 });
        byteArray.AddChildProperty(new SerializedProperty
        {
            name = "data",
            propertyPath = "m_ByteData.Array.data[0]",
            propertyType = SerializedPropertyType.Integer,
            intValue = 42
        });

        // 2. Value-type math array (Vector3[]): enterChildren must be false and child elements must not be visited
        var vecArray = new SerializedProperty
        {
            name = "m_Points",
            propertyPath = "m_Points",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };
        vecArray.AddArrayElement(new SerializedProperty { propertyType = SerializedPropertyType.Vector3 });
        vecArray.AddChildProperty(new SerializedProperty
        {
            name = "data",
            propertyPath = "m_Points.Array.data[0]",
            propertyType = SerializedPropertyType.Vector3
        });

        // 3. Empty array: enterChildren must be false
        var emptyArray = new SerializedProperty
        {
            name = "m_Empty",
            propertyPath = "m_Empty",
            propertyType = SerializedPropertyType.Generic,
            isArray = true,
            arraySize = 0
        };

        // 4. ObjectReference array: enterChildren must be true and broken element reference must be reported
        var refArray = new SerializedProperty
        {
            name = "m_Targets",
            propertyPath = "m_Targets",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };
        refArray.AddArrayElement(new SerializedProperty
        {
            propertyType = SerializedPropertyType.ObjectReference,
            objectReferenceValue = null,
            objectReferenceInstanceIDValue = 777
        });
        refArray.AddChildProperty(new SerializedProperty
        {
            name = "data",
            propertyPath = "m_Targets.Array.data[0]",
            propertyType = SerializedPropertyType.ObjectReference,
            objectReferenceValue = null,
            objectReferenceInstanceIDValue = 777
        });

        // 5. Generic struct/UnityEvent array: enterChildren must be true and nested broken reference/event must be reported
        var genericArray = new SerializedProperty
        {
            name = "m_Entries",
            propertyPath = "m_Entries",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };
        genericArray.AddArrayElement(new SerializedProperty { propertyType = SerializedPropertyType.Generic });
        genericArray.AddChildProperty(new SerializedProperty
        {
            name = "m_Ref",
            propertyPath = "m_Entries.Array.data[0].m_Ref",
            propertyType = SerializedPropertyType.ObjectReference,
            objectReferenceValue = null,
            objectReferenceInstanceIDValue = 888
        });
        genericArray.AddChildProperty(CreateUnityEventProperty(
            "m_Entries.Array.data[0].m_OnTriggered",
            CreatePersistentCall(target: null, targetInstanceId: 999, methodName: "MissingHandler", mode: 1)));

        SerializedObject.CustomProperties[comp] = new List<SerializedProperty>
        {
            byteArray,
            vecArray,
            emptyArray,
            refArray,
            genericArray
        };

        string result = Inspect.Audit(go);

        Assert.Contains("3 issue(s) found", result);
        Assert.Contains("EventTargetComponent.m_Targets.Array.data[0] is Missing (instanceID: 777)", result);
        Assert.Contains("EventTargetComponent.m_Entries.Array.data[0].m_Ref is Missing (instanceID: 888)", result);
        Assert.Contains("EventTargetComponent.m_Entries.Array.data[0].m_OnTriggered[0] has missing target object (instanceID: 999)", result);

        Assert.Contains(("m_ByteData", false), SerializedProperty.NextVisibleCalls);
        Assert.Contains(("m_Points", false), SerializedProperty.NextVisibleCalls);
        Assert.Contains(("m_Empty", false), SerializedProperty.NextVisibleCalls);
        Assert.Contains(("m_Targets", true), SerializedProperty.NextVisibleCalls);
        Assert.Contains(("m_Entries", true), SerializedProperty.NextVisibleCalls);

        Assert.DoesNotContain(SerializedProperty.NextVisibleCalls, c => c.PropertyPath == "m_ByteData.Array.data[0]");
        Assert.DoesNotContain(SerializedProperty.NextVisibleCalls, c => c.PropertyPath == "m_Points.Array.data[0]");
    }

    private static SerializedProperty CreateUnityEventProperty(string propertyPath, params SerializedProperty[] calls)
    {
        var eventProp = new SerializedProperty
        {
            name = propertyPath,
            propertyPath = propertyPath,
            propertyType = SerializedPropertyType.Generic
        };

        var callsArrayProp = new SerializedProperty
        {
            name = "m_Calls",
            propertyPath = $"{propertyPath}.m_PersistentCalls.m_Calls",
            propertyType = SerializedPropertyType.Generic,
            isArray = true
        };

        foreach (var call in calls)
        {
            callsArrayProp.AddArrayElement(call);
        }

        eventProp.SetRelativeProperty("m_PersistentCalls.m_Calls", callsArrayProp);
        return eventProp;
    }

    private static SerializedProperty CreatePersistentCall(
        UnityEngine.Object? target,
        int targetInstanceId,
        string methodName,
        int mode)
    {
        var callProp = new SerializedProperty { propertyType = SerializedPropertyType.Generic };
        callProp.SetRelativeProperty("m_Target", new SerializedProperty
        {
            propertyType = SerializedPropertyType.ObjectReference,
            objectReferenceValue = target,
            objectReferenceInstanceIDValue = targetInstanceId
        });
        callProp.SetRelativeProperty("m_MethodName", new SerializedProperty
        {
            propertyType = SerializedPropertyType.String,
            stringValue = methodName
        });
        callProp.SetRelativeProperty("m_Mode", new SerializedProperty
        {
            propertyType = SerializedPropertyType.Enum,
            intValue = mode
        });
        return callProp;
    }
}
