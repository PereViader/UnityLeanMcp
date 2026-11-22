#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        #region Selection & Main Camera Viewport

        /// <summary>
        /// Returns a high-density summary of the active Editor selection, or a clear notification if empty.
        /// </summary>
        public static string Selection(int maxItems = 50)
        {
            maxItems = Math.Max(1, maxItems);
            var activeGo = UnityEditor.Selection.activeGameObject;
            var activeObj = UnityEditor.Selection.activeObject;
            var allObjects = UnityEditor.Selection.objects;

            var validObjects = new List<UnityEngine.Object>();
            if (allObjects != null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    if (allObjects[i] != null)
                    {
                        validObjects.Add(allObjects[i]);
                    }
                }
            }

            if (activeGo == null && activeObj == null && validObjects.Count == 0)
            {
                return "(No active selection in Editor)";
            }

            var sb = new StringBuilder();

            if (activeGo != null)
            {
                string goName = "Unnamed";
                bool activeSelf = false;
                bool activeInHierarchy = false;
                string tag = "Untagged";
                int layer = 0;
                string layerName = "";

                try { goName = activeGo.name; } catch { }
                try { activeSelf = activeGo.activeSelf; } catch { }
                try { activeInHierarchy = activeGo.activeInHierarchy; } catch { }
                try { tag = activeGo.tag; } catch { }
                try { layer = activeGo.layer; } catch { }
                try { layerName = LayerMask.LayerToName(layer); } catch { }

                sb.AppendLine($"Active Selection (GameObject): \"{goName}\"");
                string path = GetHierarchyPath(activeGo.transform);
                sb.AppendLine($"  Path: {(string.IsNullOrEmpty(path) ? "/" : path)}");
                sb.AppendLine($"  Active: {(activeSelf ? "true" : "false")} (in hierarchy: {(activeInHierarchy ? "true" : "false")})");
                sb.AppendLine($"  Tag: \"{tag}\" | Layer: {layer} ({layerName})");

                if (activeGo.transform != null)
                {
                    try
                    {
                        var t = activeGo.transform;
                        if (t is RectTransform rt)
                        {
                            sb.AppendLine($"  {FormatRectTransformSummary(rt)}");
                        }
                        else
                        {
                            string pos = t.position.ToString("F2", CultureInfo.InvariantCulture);
                            string rot = t.eulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                            string scale = t.localScale.ToString("F2", CultureInfo.InvariantCulture);
                            sb.AppendLine($"  World Pos: {pos} | World Rot: {rot} | Scale: {scale}");
                        }
                    }
                    catch { }
                }

                Component[] comps = null;
                try { comps = activeGo.GetComponents<Component>(); } catch { }

                var compNames = new List<string>();
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        string cName = "Missing Script";
                        if (c != null)
                        {
                            try { cName = c.GetType().Name; } catch { cName = "Unknown Component"; }
                        }
                        compNames.Add(cName);
                    }
                }
                sb.AppendLine($"  Components ({compNames.Count}): [{string.Join(", ", compNames)}]");
            }
            else if (activeObj != null)
            {
                string assetPath = null;
                try { assetPath = AssetDatabase.GetAssetPath(activeObj); } catch { }

                string objName = "Unnamed";
                try { objName = activeObj.name; } catch { }

                sb.AppendLine($"Active Selection (Asset): \"{objName}\" ({activeObj.GetType().Name})");
                if (!string.IsNullOrEmpty(assetPath))
                {
                    sb.AppendLine($"  Asset Path: \"{assetPath}\"");
                }
            }

            int totalCount = validObjects.Count > 0 ? validObjects.Count : (activeObj != null ? 1 : (activeGo != null ? 1 : 0));
            if (totalCount > 1)
            {
                sb.AppendLine($"Total Selected Objects ({totalCount}):");
                int limit = Math.Min(validObjects.Count, maxItems);
                for (int i = 0; i < limit; i++)
                {
                    var o = validObjects[i];
                    string oName = "Unnamed";
                    string oType = "Object";
                    try { oName = o.name; } catch { }
                    try { oType = o.GetType().Name; } catch { }
                    sb.AppendLine($"  - \"{oName}\" ({oType})");
                }
                if (validObjects.Count > maxItems)
                {
                    sb.AppendLine($"  ... and {validObjects.Count - maxItems} more");
                }
            }

            if (sb.Length == 0)
            {
                return "(No active selection in Editor)";
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static Camera ResolveTargetCamera(Camera explicitCamera = null)
        {
            if (explicitCamera != null)
            {
                return explicitCamera;
            }

            Camera cam = null;
            try { cam = Camera.main; } catch { }

            if (cam == null)
            {
                try
                {
                    if (Camera.allCamerasCount > 0)
                    {
                        cam = Camera.allCameras[0];
                    }
                }
                catch { }
            }

            if (cam == null)
            {
                try
                {
#if UNITY_2023_1_OR_NEWER
                    cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
#else
                    cam = UnityEngine.Object.FindObjectOfType<Camera>();
#endif
                }
                catch { }
            }

            return cam;
        }

        /// <summary>
        /// Returns high-density viewport, projection, and configuration settings for the main scene camera.
        /// </summary>
        public static string MainCamera()
        {
            Camera cam = ResolveTargetCamera();
            if (cam == null)
            {
                return "(No active Camera found in scene)";
            }

            var sb = new StringBuilder();
            string camName = "Camera";
            string goName = "null";
            bool active = false;
            try
            {
                camName = cam.name;
                goName = cam.gameObject != null ? cam.gameObject.name : cam.name;
                active = cam.gameObject != null && cam.gameObject.activeInHierarchy;
            }
            catch { }

            sb.AppendLine($"Main Camera: \"{camName}\" (GameObject: \"{goName}\", active: {(active ? "true" : "false")})");

            if (cam.transform != null)
            {
                try
                {
                    string pos = cam.transform.position.ToString("F2", CultureInfo.InvariantCulture);
                    string rot = cam.transform.eulerAngles.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Position: {pos} | Rotation: {rot}");
                }
                catch { }
            }

            try
            {
                if (cam.orthographic)
                {
                    string orthoSize = cam.orthographicSize.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Projection: Orthographic (Size: {orthoSize})");
                }
                else
                {
                    string fov = cam.fieldOfView.ToString("F1", CultureInfo.InvariantCulture);
                    sb.AppendLine($"  Projection: Perspective (FOV: {fov}°)");
                }

                string near = cam.nearClipPlane.ToString("F2", CultureInfo.InvariantCulture);
                string far = cam.farClipPlane.ToString("F2", CultureInfo.InvariantCulture);
                sb.AppendLine($"  Clipping Planes: Near={near}, Far={far}");

                var r = cam.rect;
                string rectStr = $"[x:{r.x.ToString("F2", CultureInfo.InvariantCulture)}, y:{r.y.ToString("F2", CultureInfo.InvariantCulture)}, w:{r.width.ToString("F2", CultureInfo.InvariantCulture)}, h:{r.height.ToString("F2", CultureInfo.InvariantCulture)}]";
                sb.AppendLine($"  Viewport Rect: {rectStr}");
                sb.AppendLine($"  Pixel Dimensions: {cam.pixelWidth} x {cam.pixelHeight}");
                sb.AppendLine($"  Clear Flags: {cam.clearFlags} | Background: {cam.backgroundColor}");
                sb.AppendLine($"  Culling Mask: {cam.cullingMask} | Depth: {cam.depth} | Rendering Path: {cam.renderingPath}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  <Error reading camera settings: {ex.Message}>");
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        #endregion

        #region Visual Capture

        private static readonly byte[] s_PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] s_PngIendTrailer = { 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

        /// <summary>
        /// Captures a frame from a scene Camera or the Main Camera and writes it to disk as a PNG.
        /// </summary>
        public static string CaptureCamera(
            string outputPath = "Temp/unity_capture.png",
            int width = 1024,
            int height = 768,
            Camera camera = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Temp/unity_capture.png";
            }

            camera = ResolveTargetCamera(camera);
            if (camera == null)
            {
                return "(No active Camera found in scene to capture)";
            }

            string camName = "Camera";
            try
            {
                camName = !string.IsNullOrEmpty(camera.name) ? camera.name : "Camera";
            }
            catch { }

            string result = CaptureCameraToPng(camera, outputPath, width, height, $"camera \"{camName}\"");
            if (result.StartsWith("Captured ", StringComparison.Ordinal))
            {
                string warning = FormatOverlayUiWarning();
                if (!string.IsNullOrEmpty(warning))
                {
                    result += "\n" + warning;
                }
            }

            return result;
        }

        /// <summary>
        /// Captures what the player sees in the Game View (all composited cameras plus ScreenSpaceOverlay Canvases and UI Toolkit UIDocuments).
        /// </summary>
        public static async Task<string> CaptureGameView(
            string outputPath = "Temp/unity_gameview.png",
            int superSize = 1,
            CancellationToken cancellationToken = default)
        {
            CancellationTokenSource linkedCts = null;
            CancellationToken effectiveToken = CreateEffectiveOperationToken(cancellationToken, out linkedCts);
            try
            {
                effectiveToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = "Temp/unity_gameview.png";
                }

                superSize = Mathf.Clamp(superSize, 1, 4);

                if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                {
                    return "(GameView capture requires an interactive graphical Unity Editor and is unavailable in batchmode or with GraphicsDeviceType.Null. Use Inspect.CaptureCamera(...) for offscreen camera rendering, or Inspect.Hierarchy(includeUiDetails: true) / Inspect.UIDocument() to inspect UI layout.)";
                }

                string normalizedPath = outputPath.Replace('\\', '/');
                string fullPath = Path.GetFullPath(outputPath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = Path.Combine(
                    string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir,
                    $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp.png");

                Type gameViewType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null)
                {
                    return "(Failed to capture GameView: UnityEditor.GameView type could not be resolved)";
                }

                EditorWindow prevFocused = null;
                try { prevFocused = EditorWindow.focusedWindow; } catch { }

                EditorWindow gameView = null;
                bool createdGameView = false;

                try
                {
                    UnityEngine.Object[] existingViews = null;
                    try { existingViews = Resources.FindObjectsOfTypeAll(gameViewType); } catch { }
                    if (existingViews != null)
                    {
                        for (int i = 0; i < existingViews.Length; i++)
                        {
                            if (existingViews[i] is EditorWindow ew && ew != null)
                            {
                                gameView = ew;
                                break;
                            }
                        }
                    }

                    if (gameView == null)
                    {
                        gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                        createdGameView = gameView != null;
                    }

                    if (gameView == null)
                    {
                        return "(Failed to capture GameView: could not locate or open a GameView window)";
                    }

                    Canvas.ForceUpdateCanvases();
                    gameView.Focus();
                    ScreenCapture.CaptureScreenshot(tempPath, superSize);
                    EditorApplication.QueuePlayerLoopUpdate();
                    gameView.Repaint();

                    while (!IsCompletePngFile(tempPath))
                    {
                        await Task.Delay(50, effectiveToken);
                        EditorApplication.QueuePlayerLoopUpdate();
                        gameView.Repaint();
                    }

                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    File.Move(tempPath, fullPath);

                    long fileSizeBytes = new FileInfo(fullPath).Length;
                    return $"Captured GameView (superSize: {superSize}, {fileSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes) to {normalizedPath}";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return $"(Failed to capture GameView: {ex.Message})";
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch { }

                    try
                    {
                        if (createdGameView && gameView != null)
                        {
                            gameView.Close();
                        }
                    }
                    catch { }

                    try
                    {
                        if (prevFocused != null)
                        {
                            prevFocused.Focus();
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        private static string FormatOverlayUiWarning()
        {
            int overlayCanvasCount = 0;
            try
            {
#if UNITY_2023_1_OR_NEWER
                var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
#endif
                if (canvases != null)
                {
                    for (int i = 0; i < canvases.Length; i++)
                    {
                        var c = canvases[i];
                        if (c != null && c.isActiveAndEnabled && c.renderMode == RenderMode.ScreenSpaceOverlay)
                        {
                            overlayCanvasCount++;
                        }
                    }
                }
            }
            catch { }

            int uiDocCount = 0;
            try
            {
#if UNITY_2023_1_OR_NEWER
                var uiDocs = UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
                var uiDocs = UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>();
#endif
                if (uiDocs != null)
                {
                    for (int i = 0; i < uiDocs.Length; i++)
                    {
                        var d = uiDocs[i];
                        if (d != null && d.isActiveAndEnabled)
                        {
                            uiDocCount++;
                        }
                    }
                }
            }
            catch { }

            if (overlayCanvasCount == 0 && uiDocCount == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(2);
            if (overlayCanvasCount > 0)
            {
                parts.Add(overlayCanvasCount == 1
                    ? "1 active ScreenSpaceOverlay Canvas"
                    : $"{overlayCanvasCount} active ScreenSpaceOverlay Canvases");
            }
            if (uiDocCount > 0)
            {
                parts.Add(uiDocCount == 1
                    ? "1 active UIDocument"
                    : $"{uiDocCount} active UIDocuments");
            }

            string summary = string.Join(" and ", parts);
            return $"Warning: {summary} not included in Camera.targetTexture render. Use `await Inspect.CaptureGameView(...)` to capture what the player sees, or `Inspect.Hierarchy(includeUiDetails: true)` / `Inspect.UIDocument()` to inspect UI layout.";
        }

        private static bool IsCompletePngFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long len = fs.Length;
                    if (len < s_PngHeader.Length + s_PngIendTrailer.Length)
                    {
                        return false;
                    }

                    byte[] header = new byte[s_PngHeader.Length];
                    if (fs.Read(header, 0, header.Length) != header.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < s_PngHeader.Length; i++)
                    {
                        if (header[i] != s_PngHeader[i])
                        {
                            return false;
                        }
                    }

                    byte[] trailer = new byte[s_PngIendTrailer.Length];
                    fs.Seek(-s_PngIendTrailer.Length, SeekOrigin.End);
                    if (fs.Read(trailer, 0, trailer.Length) != trailer.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < s_PngIendTrailer.Length; i++)
                    {
                        if (trailer[i] != s_PngIendTrailer[i])
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Captures the active SceneView camera and writes it to disk as a PNG.
        /// </summary>
        public static string CaptureSceneView(
            string outputPath = "Temp/unity_sceneview.png",
            int width = 1024,
            int height = 768)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Temp/unity_sceneview.png";
            }

            SceneView sceneView = null;
            try
            {
                sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null && SceneView.sceneViews.Count > 0)
                {
                    sceneView = SceneView.sceneViews[0] as SceneView;
                }
            }
            catch { }

            if (sceneView == null)
            {
                return "(No active SceneView found in Editor to capture)";
            }

            Camera cam = null;
            try { cam = sceneView.camera; } catch { }

            if (cam == null)
            {
                return "(SceneView does not have an active Camera to capture)";
            }

            return CaptureCameraToPng(cam, outputPath, width, height, "SceneView");
        }

        /// <summary>
        /// Captures an auto-framed off-screen view of a GameObject and its active children without modifying scene cameras or dirty state.
        /// By default, frames from a 3/4 angle in front of the target (+forward, +right, +up); pass viewDirection to specify a world-space direction from target to camera.
        /// </summary>
        public static string CaptureTarget(
            GameObject target,
            string outputPath = "Temp/unity_target.png",
            int width = 1024,
            int height = 768,
            Vector3? viewDirection = null)
        {
            if (ReferenceEquals(target, null))
            {
                return "(GameObject is null)";
            }

            if (target == null)
            {
                return "(GameObject is destroyed)";
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Temp/unity_target.png";
            }

            string targetName = "GameObject";
            try
            {
                if (!string.IsNullOrEmpty(target.name))
                {
                    targetName = target.name;
                }
            }
            catch { }

            Bounds bounds = ComputeTargetBounds(target);

            Vector3 localDefaultDirection = new Vector3(0.6f, 0.45f, 1f);
            Vector3 direction;
            if (viewDirection.HasValue && viewDirection.Value.sqrMagnitude > 1e-6f)
            {
                direction = viewDirection.Value.normalized;
            }
            else
            {
                direction = target.transform != null
                    ? target.transform.TransformDirection(localDefaultDirection)
                    : localDefaultDirection;
                if (direction.sqrMagnitude <= 1e-6f)
                {
                    direction = localDefaultDirection;
                }
                direction = direction.normalized;
            }

            int clampedWidth = Mathf.Clamp(width, 16, 4096);
            int clampedHeight = Mathf.Clamp(height, 16, 4096);
            const float fieldOfView = 60f;

            float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
            float aspect = (float)clampedWidth / clampedHeight;
            float vHalfFov = fieldOfView * Mathf.Deg2Rad * 0.5f;
            float hHalfFov = Mathf.Atan(Mathf.Tan(vHalfFov) * aspect);
            float minHalfFov = Mathf.Min(vHalfFov, hHalfFov);
            float distance = (radius / Mathf.Sin(minHalfFov)) * 1.15f;

            GameObject tempGo = null;
            try
            {
                tempGo = EditorUtility.CreateGameObjectWithHideFlags(
                    "__UnityLeanMcp_CaptureTargetCam",
                    HideFlags.HideAndDontSave,
                    typeof(Camera));

                var tempCam = tempGo != null ? tempGo.GetComponent<Camera>() : null;
                if (tempCam == null)
                {
                    return $"(Failed to capture target \"{targetName}\": could not create temporary Camera)";
                }

                if (target.scene.IsValid())
                {
                    tempCam.scene = target.scene;
                }

                tempCam.fieldOfView = fieldOfView;
                tempCam.nearClipPlane = Mathf.Max(0.01f, (distance - radius) * 0.1f);
                tempCam.farClipPlane = Mathf.Max(1000f, distance + radius * 4f);
                tempCam.clearFlags = CameraClearFlags.SolidColor;
                tempCam.backgroundColor = new Color(0.19f, 0.30f, 0.47f, 1f);

                Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
                tempCam.transform.position = bounds.center + direction * distance;
                tempCam.transform.LookAt(bounds.center, up);

                return CaptureCameraToPng(tempCam, outputPath, width, height, $"target \"{targetName}\"");
            }
            catch (Exception ex)
            {
                return $"(Failed to capture target \"{targetName}\": {ex.Message})";
            }
            finally
            {
                if (tempGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
                }
            }
        }

        private static Bounds ComputeTargetBounds(GameObject target)
        {
            bool hasBounds = false;
            Bounds bounds = default;

            var renderers = target.GetComponentsInChildren<Renderer>(false);
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r != null && r.enabled)
                    {
                        if (!hasBounds)
                        {
                            bounds = r.bounds;
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(r.bounds);
                        }
                    }
                }
            }

            if (!hasBounds)
            {
                var colliders = target.GetComponentsInChildren<Collider>(false);
                if (colliders != null)
                {
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        var c = colliders[i];
                        if (c != null && c.enabled)
                        {
                            if (!hasBounds)
                            {
                                bounds = c.bounds;
                                hasBounds = true;
                            }
                            else
                            {
                                bounds.Encapsulate(c.bounds);
                            }
                        }
                    }
                }
            }

            if (!hasBounds)
            {
                Vector3 center = target.transform != null ? target.transform.position : Vector3.zero;
                bounds = new Bounds(center, Vector3.one);
            }

            return bounds;
        }

        private static string CaptureCameraToPng(
            Camera camera,
            string outputPath,
            int width,
            int height,
            string targetName)
        {
            width = Mathf.Clamp(width, 16, 4096);
            height = Mathf.Clamp(height, 16, 4096);

            string normalizedPath = outputPath.Replace('\\', '/');
            string fullPath = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture prevTarget = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
                camera.targetTexture = rt;

                bool renderedViaRequest = false;
                try
                {
                    if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
                    {
                        var req = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = rt };
                        if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, req))
                        {
                            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, req);
                            renderedViaRequest = true;
                        }
                    }
                }
                catch
                {
                    renderedViaRequest = false;
                }

                if (!renderedViaRequest)
                {
                    camera.Render();
                }

                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);

                byte[] pngBytes = tex.EncodeToPNG();
                File.WriteAllBytes(fullPath, pngBytes);

                long fileSizeBytes = pngBytes.LongLength;
                return $"Captured {targetName} ({width}x{height}, {fileSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes) to {normalizedPath}";
            }
            catch (Exception ex)
            {
                return $"(Failed to capture {targetName}: {ex.Message})";
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;

                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }

                if (tex != null)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(tex);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(tex, false);
                    }
                }
            }
        }

        #endregion
    }
}
