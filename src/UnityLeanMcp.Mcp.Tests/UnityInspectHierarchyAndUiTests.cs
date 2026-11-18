using System;
using System.Collections.Generic;
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
}
