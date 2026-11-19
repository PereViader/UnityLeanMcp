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
public sealed class UnityInspectSelectionAndCaptureTests : IDisposable
{
    private readonly UnityInspectTestScope _scope = new();

    public void Dispose() => _scope.Dispose();

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
            new PropertyModification
            {
                target = null,
                propertyPath = "m_LocalPosition.y",
                value = "20"
            },
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

        string result = Inspect.PrefabOverrides(go, maxItems: 1);

        Assert.Contains("3 override(s) (1 added, 1 removed, 1 modified)", result);
        Assert.Contains("+ AudioSource", result);
        Assert.Contains(Inspect.ItemTruncationMarker, result);
        Assert.DoesNotContain("- Rigidbody", result);
        Assert.DoesNotContain("m_LocalPosition.z", result);
    }

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
    public void Selection_WhenAllObjectsAreNull_ReturnsCleanNotification()
    {
        Selection.activeGameObject = null;
        Selection.activeObject = null;
        Selection.objects = new UnityEngine.Object[] { null! };

        string result = Inspect.Selection();
        Assert.Equal("(No active selection in Editor)", result);
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

    [Fact]
    public void CaptureCamera_WhenCameraPresent_SavesPngAndReturnsFormattedSummary()
    {
        var go = new GameObject("TestCamObj");
        var cam = go.AddComponent<Camera>();
        cam.name = "MyRenderCam";
        Camera.main = cam;

        string outputPath = "Temp/test_inspect_cam_unit.png";
        _scope.TrackFile(outputPath);

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
        _scope.TrackFile(outputPath);

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
        _scope.TrackFile(outputPath);

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

        string playerMatches = Inspect.Find("Player");
        Assert.Contains("GameObjects matching 'Player' (2):", playerMatches);
        Assert.Contains("PlayerCharacter", playerMatches);
        Assert.Contains("PlayerWeapon", playerMatches);
        Assert.DoesNotContain("EnemyBoss", playerMatches);

        string spawnerExcluded = Inspect.Find("*Spawner*", includeInactive: false);
        Assert.Contains("(No GameObjects found matching '*Spawner*' in loaded scenes)", spawnerExcluded);

        string spawnerIncluded = Inspect.Find("*Spawner*", includeInactive: true);
        Assert.Contains("SecretSpawner", spawnerIncluded);
        Assert.Contains("inactive", spawnerIncluded);

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
        go.AddRawComponent(null);

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
    public void Tests_DiscoversTestsInLoadedAppDomain_AndFilters()
    {
        string allTests = Inspect.Tests();
        Assert.Contains("Available Tests", allTests);
        Assert.Contains("• UnityLeanMcp.", allTests);

        string filtered = Inspect.Tests("UnityInspectSelectionAndCaptureTests", maxItems: 200);
        Assert.Contains("Available Tests", filtered);
        Assert.Contains("UnityInspectSelectionAndCaptureTests", filtered);
        Assert.Contains("Tests_DiscoversTestsInLoadedAppDomain", filtered);

        string none = Inspect.Tests("NonExistentPattern_ZXY_987");
        Assert.Contains("(No tests found matching filter 'NonExistentPattern_ZXY_987')", none);
    }
}
