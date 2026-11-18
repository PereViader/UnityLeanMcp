#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

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
                int limit = Math.Min(validObjects.Count, 10);
                for (int i = 0; i < limit; i++)
                {
                    var o = validObjects[i];
                    string oName = "Unnamed";
                    string oType = "Object";
                    try { oName = o.name; } catch { }
                    try { oType = o.GetType().Name; } catch { }
                    sb.AppendLine($"  - \"{oName}\" ({oType})");
                }
                if (validObjects.Count > 10)
                {
                    sb.AppendLine($"  ... and {validObjects.Count - 10} more");
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

            return CaptureCameraToPng(camera, outputPath, width, height, $"camera \"{camName}\"");
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
                RenderTexture.active = rt;

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
