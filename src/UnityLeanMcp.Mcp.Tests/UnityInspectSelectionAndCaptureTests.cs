using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
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
    public void CaptureCamera_WhenCameraRenderMutatesActiveRenderTexture_BindsTargetBeforeReadPixelsAndRestoresPrevious()
    {
        var go = new GameObject("RenderActiveCamObj");
        var cam = go.AddComponent<Camera>();
        cam.name = "RenderActiveCam";

        var prevActive = new RenderTexture { width = 64, height = 64 };
        var mutatedActive = new RenderTexture { width = 128, height = 128 };
        RenderTexture.active = prevActive;

        RenderTexture? renderedTarget = null;
        RenderTexture? activeDuringReadPixels = null;

        Camera.OnBeforeRenderAction = c =>
        {
            renderedTarget = c.targetTexture;
            RenderTexture.active = mutatedActive;
        };
        Texture2D.OnReadPixelsAction = _ => activeDuringReadPixels = RenderTexture.active;

        string outputPath = "Temp/test_inspect_cam_render_active.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.Contains("Captured camera \"RenderActiveCam\"", result);
        Assert.NotNull(renderedTarget);
        Assert.Same(renderedTarget, activeDuringReadPixels);
        Assert.Same(prevActive, RenderTexture.active);
    }

    [Fact]
    public void CaptureCamera_WhenRenderPipelineRequestMutatesActiveRenderTexture_BindsTargetBeforeReadPixelsAndRestoresPrevious()
    {
        var go = new GameObject("SrpActiveCamObj");
        var cam = go.AddComponent<Camera>();
        cam.name = "SrpActiveCam";

        var prevActive = new RenderTexture { width = 64, height = 64 };
        RenderTexture.active = prevActive;

        GraphicsSettings.currentRenderPipeline = new RenderPipelineAsset();
        RenderPipeline.SupportsRenderRequestFunc = (_, _) => true;

        RenderTexture? requestedDestination = null;
        RenderTexture? activeDuringReadPixels = null;

        RenderPipeline.SubmitRenderRequestAction = (_, req) =>
        {
            requestedDestination = req.destination;
            RenderTexture.active = null;
        };
        Texture2D.OnReadPixelsAction = _ => activeDuringReadPixels = RenderTexture.active;

        string outputPath = "Temp/test_inspect_cam_srp_active.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.Contains("Captured camera \"SrpActiveCam\"", result);
        Assert.NotNull(requestedDestination);
        Assert.Same(requestedDestination, activeDuringReadPixels);
        Assert.Same(prevActive, RenderTexture.active);
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
    public void CaptureCamera_WithScreenSpaceOverlayCanvas_AppendsWarning()
    {
        var camGo = new GameObject("CamGo");
        var cam = camGo.AddComponent<Camera>();
        cam.name = "Main Camera";
        Camera.main = cam;

        var canvasGo = new GameObject("OverlayCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        UnityEngine.Object.FindObjectsOfTypeFunc = type =>
            type == typeof(Canvas) ? new object[] { canvas } : Array.Empty<object>();

        string outputPath = "Temp/test_inspect_cam_overlay_canvas.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.Contains("Captured camera \"Main Camera\"", result);
        Assert.Contains("Warning: 1 active ScreenSpaceOverlay Canvas not included in Camera.targetTexture render.", result);
        Assert.Contains("await Inspect.CaptureGameView(...)", result);
        Assert.Equal(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
    }

    [Fact]
    public void CaptureCamera_WithActiveUIDocument_AppendsWarning()
    {
        var camGo = new GameObject("CamGo");
        var cam = camGo.AddComponent<Camera>();
        cam.name = "Main Camera";
        Camera.main = cam;

        var uiGo = new GameObject("HudDoc");
        uiGo.AddComponent<UnityEngine.UIElements.UIDocument>();

        string outputPath = "Temp/test_inspect_cam_uidoc.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.Contains("Captured camera \"Main Camera\"", result);
        Assert.Contains("Warning: 1 active UIDocument not included in Camera.targetTexture render.", result);
        Assert.Contains("await Inspect.CaptureGameView(...)", result);
    }

    [Fact]
    public void CaptureCamera_WithOverlayCanvasAndUIDocument_CombinesWarning()
    {
        var camGo = new GameObject("CamGo");
        var cam = camGo.AddComponent<Camera>();
        Camera.main = cam;

        var canvasGo1 = new GameObject("OverlayCanvas1");
        var canvas1 = canvasGo1.AddComponent<Canvas>();
        canvas1.renderMode = RenderMode.ScreenSpaceOverlay;

        var canvasGo2 = new GameObject("OverlayCanvas2");
        var canvas2 = canvasGo2.AddComponent<Canvas>();
        canvas2.renderMode = RenderMode.ScreenSpaceOverlay;

        var docGo = new GameObject("HudDoc");
        var doc = docGo.AddComponent<UnityEngine.UIElements.UIDocument>();

        UnityEngine.Object.FindObjectsOfTypeFunc = type =>
        {
            if (type == typeof(Canvas)) return new object[] { canvas1, canvas2 };
            if (type == typeof(UnityEngine.UIElements.UIDocument)) return new object[] { doc };
            return Array.Empty<object>();
        };

        string outputPath = "Temp/test_inspect_cam_both_ui.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.Contains("Warning: 2 active ScreenSpaceOverlay Canvases and 1 active UIDocument not included in Camera.targetTexture render.", result);
    }

    [Fact]
    public void CaptureCamera_WithNonOverlayOrInactiveUi_DoesNotWarn()
    {
        var camGo = new GameObject("CamGo");
        var cam = camGo.AddComponent<Camera>();
        Camera.main = cam;

        var camCanvasGo = new GameObject("CameraCanvas");
        var camCanvas = camCanvasGo.AddComponent<Canvas>();
        camCanvas.renderMode = RenderMode.ScreenSpaceCamera;

        var disabledOverlayGo = new GameObject("DisabledOverlay");
        var disabledOverlay = disabledOverlayGo.AddComponent<Canvas>();
        disabledOverlay.renderMode = RenderMode.ScreenSpaceOverlay;
        disabledOverlay.enabled = false;

        var inactiveDocGo = new GameObject("InactiveDoc") { activeSelf = false };
        var inactiveDoc = inactiveDocGo.AddComponent<UnityEngine.UIElements.UIDocument>();

        UnityEngine.Object.FindObjectsOfTypeFunc = type =>
        {
            if (type == typeof(Canvas)) return new object[] { camCanvas, disabledOverlay };
            if (type == typeof(UnityEngine.UIElements.UIDocument)) return new object[] { inactiveDoc };
            return Array.Empty<object>();
        };

        string outputPath = "Temp/test_inspect_cam_no_warn.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

        Assert.DoesNotContain("Warning:", result);
    }

    [Fact]
    public void CaptureSceneView_WithOverlayUi_DoesNotWarn()
    {
        var camGo = new GameObject("SceneCamObj");
        var cam = camGo.AddComponent<Camera>();
        SceneView.lastActiveSceneView = new SceneView { camera = cam };

        var canvasGo = new GameObject("OverlayCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        UnityEngine.Object.FindObjectsOfTypeFunc = type =>
            type == typeof(Canvas) ? new object[] { canvas } : Array.Empty<object>();

        string outputPath = "Temp/test_inspect_sceneview_no_warn.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureSceneView(outputPath, width: 320, height: 240);

        Assert.Contains("Captured SceneView", result);
        Assert.DoesNotContain("Warning:", result);
    }

    [Fact]
    public async Task CaptureGameView_WhenBatchMode_FailsFast()
    {
        Application.isBatchMode = true;

        string result = await Inspect.CaptureGameView("Temp/test_gameview_batch.png");

        Assert.Contains("batchmode", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inspect.CaptureCamera", result);
    }

    [Fact]
    public async Task CaptureGameView_WhenNullGraphicsDevice_FailsFast()
    {
        SystemInfo.graphicsDeviceType = GraphicsDeviceType.Null;

        string result = await Inspect.CaptureGameView("Temp/test_gameview_null_gfx.png");

        Assert.Contains("GraphicsDeviceType.Null", result);
        Assert.Contains("Inspect.CaptureCamera", result);
    }

    [Fact]
    public async Task CaptureGameView_HappyPath_WritesPngClampsSuperSizeAndRestoresFocus()
    {
        var prevWin = new EditorWindow();
        EditorWindow.focusedWindow = prevWin;

        var gameView = new GameView();
        Resources.FindObjectsOfTypeAllFunc = type =>
            type == typeof(GameView) ? new UnityEngine.Object[] { gameView } : Array.Empty<UnityEngine.Object>();

        int capturedSuperSize = 0;
        byte[] validPng = CreateValidTestPngBytes();
        ScreenCapture.CaptureScreenshotAction = (path, superSize) =>
        {
            capturedSuperSize = superSize;
            File.WriteAllBytes(path, validPng);
        };

        string outputPath = "Temp/SubGameView/test_gameview_unit.png";
        _scope.TrackFile(outputPath);

        string result = await Inspect.CaptureGameView(outputPath, superSize: 99);

        Assert.Equal(4, capturedSuperSize);
        Assert.Contains("Captured GameView (superSize: 4,", result);
        Assert.Contains("Temp/SubGameView/test_gameview_unit.png", result);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(validPng, File.ReadAllBytes(outputPath));
        Assert.Same(prevWin, EditorWindow.focusedWindow);
        Assert.False(gameView.IsClosed);
        Assert.True(gameView.FocusCount >= 1);
        Assert.True(gameView.RepaintCount >= 1);
    }

    [Fact]
    public async Task CaptureGameView_WhenNoExistingGameView_OpensAndClosesTemporaryGameView()
    {
        var prevWin = new EditorWindow();
        EditorWindow.focusedWindow = prevWin;

        GameView? openedView = null;
        EditorWindow.GetWindowFunc = (type, utility, title, focus) =>
        {
            openedView = new GameView();
            return openedView;
        };

        byte[] validPng = CreateValidTestPngBytes();
        ScreenCapture.CaptureScreenshotAction = (path, _) =>
        {
            File.WriteAllBytes(path, validPng);
        };

        string outputPath = "Temp/test_gameview_temp_window.png";
        _scope.TrackFile(outputPath);

        string result = await Inspect.CaptureGameView(outputPath, superSize: 0);

        Assert.Contains("Captured GameView (superSize: 1,", result);
        Assert.NotNull(openedView);
        Assert.True(openedView!.IsClosed);
        Assert.Same(prevWin, EditorWindow.focusedWindow);
    }

    [Fact]
    public async Task CaptureGameView_WhenCanceled_CleansUpTempFileAndRestoresFocus()
    {
        var prevWin = new EditorWindow();
        EditorWindow.focusedWindow = prevWin;

        GameView? openedView = null;
        EditorWindow.GetWindowFunc = (type, utility, title, focus) =>
        {
            openedView = new GameView();
            return openedView;
        };

        string? capturedTempPath = null;
        using var cts = new CancellationTokenSource();
        ScreenCapture.CaptureScreenshotAction = (path, _) =>
        {
            capturedTempPath = path;
            // Write incomplete bytes (no IEND trailer) so polling waits until canceled
            File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            cts.Cancel();
        };

        string outputPath = "Temp/test_gameview_canceled.png";
        _scope.TrackFile(outputPath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Inspect.CaptureGameView(outputPath, cancellationToken: cts.Token));

        Assert.NotNull(capturedTempPath);
        Assert.False(File.Exists(capturedTempPath));
        Assert.False(File.Exists(outputPath));
        Assert.NotNull(openedView);
        Assert.True(openedView!.IsClosed);
        Assert.Same(prevWin, EditorWindow.focusedWindow);
    }

    private static byte[] CreateValidTestPngBytes()
    {
        // Minimal PNG header + IEND trailer for structural validation
        return new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        };
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
    public void CaptureTarget_NullOrDestroyed_ReturnsCleanNotification()
    {
        Assert.Equal("(GameObject is null)", Inspect.CaptureTarget(null!));

        var dead = new GameObject("DeadTarget");
        UnityEngine.Object.DestroyImmediate(dead);
        Assert.Equal("(GameObject is destroyed)", Inspect.CaptureTarget(dead));
    }

    [Fact]
    public void CaptureTarget_FramesCombinedChildRendererBounds_FrontRelativeDefaultAndDestroysTempCam()
    {
        var mainCamGo = new GameObject("MainCam");
        var mainCam = mainCamGo.AddComponent<Camera>();
        mainCamGo.transform.position = new Vector3(100, 100, 100);
        Camera.main = mainCam;

        var target = new GameObject("HeroVehicle")
        {
            scene = new Scene { name = "PrefabStage", isLoaded = true }
        };
        // Rotated 90 degrees around Y so local +Z (front) points along world +X
        target.transform.eulerAngles = new Vector3(0f, 90f, 0f);
        var rootRenderer = target.AddComponent<Renderer>();
        rootRenderer.bounds = new Bounds(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));

        var child = new GameObject("FrontBumper");
        target.transform.AddChild(child.transform);
        var childRenderer = child.AddComponent<Renderer>();
        childRenderer.bounds = new Bounds(new Vector3(4f, 0f, 0f), new Vector3(2f, 2f, 2f));

        // Combined bounds span X: [-1, 5] -> center (2, 0, 0), size (6, 2, 2)
        Camera? renderedCam = null;
        Vector3 capturedPos = Vector3.zero;
        Vector3? capturedLookAt = null;
        HideFlags capturedHideFlags = HideFlags.None;
        Scene capturedScene = default;
        Camera.OnBeforeRenderAction = cam =>
        {
            renderedCam = cam;
            capturedPos = cam.transform.position;
            capturedLookAt = cam.transform.LastLookAtPosition;
            capturedHideFlags = cam.gameObject.hideFlags;
            capturedScene = cam.scene;
        };

        string outputPath = "Temp/test_capture_target_front.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureTarget(target, outputPath, width: 800, height: 600);

        Assert.Contains("Captured target \"HeroVehicle\" (800x600,", result);
        Assert.True(File.Exists(outputPath));
        Assert.NotNull(renderedCam);
        Assert.Equal(HideFlags.HideAndDontSave, capturedHideFlags);
        Assert.Equal("PrefabStage", capturedScene.name);
        Assert.True(renderedCam!.gameObject._isDestroyed);
        Assert.True(capturedLookAt.HasValue);
        Assert.InRange(capturedLookAt!.Value.x, 1.99f, 2.01f);
        Assert.InRange(capturedLookAt!.Value.y, -0.01f, 0.01f);
        Assert.InRange(capturedLookAt!.Value.z, -0.01f, 0.01f);
        // Rotated 90 deg around Y: local +Z maps to world +X, local +X maps to world -Z
        Assert.True(capturedPos.x > capturedLookAt.Value.x, $"Expected camera in front (+X) of target, got pos {capturedPos}");
        Assert.True(capturedPos.y > capturedLookAt.Value.y, $"Expected camera elevated (+Y) above target, got pos {capturedPos}");
        Assert.True(capturedPos.z < capturedLookAt.Value.z, $"Expected camera to target's right (-Z in world), got pos {capturedPos}");
        // Verify Camera.main was untouched
        Assert.Equal(100f, mainCamGo.transform.position.x);
    }

    [Fact]
    public void CaptureTarget_WithColliderFallbackAndUpViewDirection_UsesSafeUpVector()
    {
        var target = new GameObject("TriggerVolume");
        target.transform.position = new Vector3(10f, 5f, -3f);
        var collider = target.AddComponent<Collider>();
        collider.bounds = new Bounds(new Vector3(10f, 5f, -3f), new Vector3(4f, 2f, 4f));

        Vector3? capturedUp = null;
        Vector3 capturedPos = Vector3.zero;
        Camera.OnBeforeRenderAction = cam =>
        {
            capturedPos = cam.transform.position;
            capturedUp = cam.transform.LastLookAtUp;
        };

        string outputPath = "Temp/test_capture_target_top.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureTarget(target, outputPath, width: 512, height: 512, viewDirection: Vector3.up);

        Assert.Contains("Captured target \"TriggerVolume\" (512x512,", result);
        Assert.True(File.Exists(outputPath));
        Assert.True(capturedPos.y > 5f);
        Assert.InRange(capturedPos.x, 9.99f, 10.01f);
        Assert.InRange(capturedPos.z, -3.01f, -2.99f);
        Assert.True(capturedUp.HasValue);
        Assert.InRange(capturedUp!.Value.z, 0.99f, 1.01f);
    }

    [Fact]
    public void CaptureTarget_WhenNoRendererOrCollider_FallsBackToTransformPosition()
    {
        var target = new GameObject("EmptyMarker");
        target.transform.position = new Vector3(3f, 4f, 5f);

        Vector3? capturedLookAt = null;
        Camera.OnBeforeRenderAction = cam =>
        {
            capturedLookAt = cam.transform.LastLookAtPosition;
        };

        string outputPath = "Temp/test_capture_target_empty.png";
        _scope.TrackFile(outputPath);

        string result = Inspect.CaptureTarget(target, outputPath);

        Assert.Contains("Captured target \"EmptyMarker\"", result);
        Assert.True(File.Exists(outputPath));
        Assert.True(capturedLookAt.HasValue);
        Assert.InRange(capturedLookAt!.Value.x, 2.99f, 3.01f);
        Assert.InRange(capturedLookAt!.Value.y, 3.99f, 4.01f);
        Assert.InRange(capturedLookAt!.Value.z, 4.99f, 5.01f);
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
    public void Find_SearchesActivePrefabStageAndLoadedScenes()
    {
        var prefabRoot = new GameObject("PlayerPrefabRoot");
        prefabRoot.activeSelf = true;
        var prefabChild = new GameObject("PlayerWeaponSlot");
        prefabChild.activeSelf = false;
        prefabRoot.transform.AddChild(prefabChild.transform);

        var stage = new PrefabStage
        {
            assetPath = "Assets/Prefabs/Player.prefab",
            prefabContentsRoot = prefabRoot
        };
        PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

        var sceneGo = new GameObject("PlayerSceneClone");
        sceneGo.activeSelf = true;
        SceneManager.Scenes.Add(new Scene
        {
            name = "MainScene",
            isLoaded = true,
            RootObjects = new List<GameObject> { sceneGo }
        });

        string allMatches = Inspect.Find("Player", includeInactive: true);
        Assert.Contains("GameObjects matching 'Player' (3):", allMatches);
        Assert.Contains("prefabStage: \"Assets/Prefabs/Player.prefab\"", allMatches);
        Assert.Contains("PlayerWeaponSlot", allMatches);
        Assert.Contains("scene: \"MainScene\"", allMatches);

        string activeOnly = Inspect.Find("PlayerWeaponSlot", includeInactive: false);
        Assert.Equal("(No GameObjects found matching 'PlayerWeaponSlot' in Prefab Stage or loaded scenes)", activeOnly);
    }

    private sealed class TestPlayerController : MonoBehaviour, ITestDamageable
    {
    }

    private interface ITestDamageable
    {
    }

    [Fact]
    public void Find_CoversPrefabStageOnlyInactiveParentDestroyedRootAndTruncation()
    {
        Assert.Equal("(No scenes currently loaded)", Inspect.Find("Anything"));

        var destroyedRoot = new GameObject("DestroyedPrefabRoot");
        UnityEngine.Object.DestroyImmediate(destroyedRoot);
        PrefabStageUtility.GetCurrentPrefabStageFunc = () => new PrefabStage
        {
            assetPath = null!,
            prefabContentsRoot = destroyedRoot
        };
        Assert.Equal("(No GameObjects found matching 'Anything' in Prefab Stage or loaded scenes)", Inspect.Find("Anything"));

        var prefabRoot = new GameObject("PrefabRoot");
        prefabRoot.activeSelf = false;
        var activeChildUnderInactiveParent = new GameObject("ChildUnderInactive");
        activeChildUnderInactiveParent.activeSelf = true;
        prefabRoot.transform.AddChild(activeChildUnderInactiveParent.transform);

        PrefabStageUtility.GetCurrentPrefabStageFunc = () => new PrefabStage
        {
            assetPath = "",
            prefabContentsRoot = prefabRoot
        };

        string skippedChild = Inspect.Find("ChildUnderInactive", includeInactive: false);
        Assert.Equal("(No GameObjects found matching 'ChildUnderInactive' in Prefab Stage or loaded scenes)", skippedChild);

        string includedChild = Inspect.Find("ChildUnderInactive", includeInactive: true);
        Assert.Contains("prefabStage: \"(Unknown)\"", includedChild);
        Assert.Contains("inactive", includedChild);

        string exactLimit = Inspect.Find("", includeInactive: true, maxItems: 2);
        Assert.Contains("GameObjects matching '' (2):", exactLimit);
        Assert.DoesNotContain("truncated", exactLimit);

        string overLimit = Inspect.Find("", includeInactive: true, maxItems: 0);
        Assert.Contains("GameObjects matching '' (1):", overLimit);
        Assert.Contains("... (truncated: maximum item count reached)", overLimit);
    }

    [Fact]
    public void FindByComponent_MatchesShortFullBaseInterfaceWildcardAndPrefabStage()
    {
        Assert.Equal("(No scenes currently loaded)", Inspect.FindByComponent("Collider"));

        var playerGo = new GameObject("Player");
        playerGo.activeSelf = true;
        playerGo.AddComponent<TestPlayerController>();
        playerGo.AddComponent<Collider>();
        playerGo.AddRawComponent(null);
        var destroyedComp = playerGo.AddComponent<Collider>();
        UnityEngine.Object.DestroyImmediate(destroyedComp);

        var parentDisabled = new GameObject("DisabledParent");
        parentDisabled.activeSelf = false;
        var childActiveFlag = new GameObject("ChildWithCollider");
        childActiveFlag.activeSelf = true;
        childActiveFlag.AddComponent<Collider>();
        parentDisabled.transform.AddChild(childActiveFlag.transform);

        SceneManager.Scenes.Add(new Scene
        {
            name = "GameplayScene",
            isLoaded = true,
            RootObjects = new List<GameObject> { playerGo, parentDisabled }
        });

        var prefabRoot = new GameObject("PrefabColliderRoot");
        prefabRoot.activeSelf = true;
        prefabRoot.AddComponent<Collider>();
        var stage = new PrefabStage
        {
            assetPath = "Assets/Prefabs/Prop.prefab",
            prefabContentsRoot = prefabRoot
        };
        PrefabStageUtility.GetCurrentPrefabStageFunc = () => stage;

        Assert.Equal("(Component type name is required)", Inspect.FindByComponent("  "));

        string shortMatch = Inspect.FindByComponent("testplayercontroller");
        Assert.Contains("GameObjects with component 'testplayercontroller' (1):", shortMatch);
        Assert.Contains("Player [TestPlayerController]", shortMatch);
        Assert.Contains("scene: \"GameplayScene\"", shortMatch);

        string fullMatch = Inspect.FindByComponent(typeof(TestPlayerController).FullName!);
        Assert.Contains("Player [TestPlayerController]", fullMatch);

        string monoBehaviourMatch = Inspect.FindByComponent("MonoBehaviour");
        Assert.Contains("Player [TestPlayerController]", monoBehaviourMatch);

        string behaviourMatch = Inspect.FindByComponent("Behaviour");
        Assert.Contains("Player [TestPlayerController]", behaviourMatch);

        string interfaceMatch = Inspect.FindByComponent("ITestDamageable");
        Assert.Contains("Player [TestPlayerController]", interfaceMatch);

        string baseMatch = Inspect.FindByComponent("Component");
        Assert.Contains("Player [Transform, TestPlayerController, Collider]", baseMatch);

        // Must stop before UnityEngine.Object / System.Object
        Assert.Contains("(No GameObjects with component 'Object' found", Inspect.FindByComponent("Object"));
        Assert.Contains("(No GameObjects with component 'UnityEngine.Object' found", Inspect.FindByComponent("UnityEngine.Object"));
        Assert.Contains("(No GameObjects with component '*Object*' found", Inspect.FindByComponent("*Object*"));

        // Inactive parent must hide child even when child.activeSelf is true
        string wildcardActiveOnly = Inspect.FindByComponent("*Collider*", includeInactive: false);
        Assert.Contains("GameObjects with component '*Collider*' (2):", wildcardActiveOnly);
        Assert.Contains("PrefabColliderRoot [Collider]", wildcardActiveOnly);
        Assert.Contains("prefabStage: \"Assets/Prefabs/Prop.prefab\"", wildcardActiveOnly);
        Assert.Contains("Player [Collider]", wildcardActiveOnly);
        Assert.DoesNotContain("ChildWithCollider", wildcardActiveOnly);
        Assert.DoesNotContain("truncated", wildcardActiveOnly);

        string includeInactiveAll = Inspect.FindByComponent("*Collider*", includeInactive: true);
        Assert.Contains("GameObjects with component '*Collider*' (3):", includeInactiveAll);
        Assert.Contains("ChildWithCollider [Collider]", includeInactiveAll);
        Assert.Contains("inactive", includeInactiveAll);

        string truncated = Inspect.FindByComponent("*Collider*", includeInactive: true, maxItems: 1);
        Assert.Contains("GameObjects with component '*Collider*' (1):", truncated);
        Assert.Contains("... (truncated: maximum item count reached)", truncated);
    }
}
