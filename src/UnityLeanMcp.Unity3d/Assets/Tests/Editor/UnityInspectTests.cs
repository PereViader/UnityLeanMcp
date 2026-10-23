using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityLeanMcp;

namespace UnityLeanMcpTests
{
    public class UnityInspectTests
    {
        private readonly List<GameObject> m_CreatedObjects = new List<GameObject>();
        private readonly List<string> m_CreatedFiles = new List<string>();

        [TearDown]
        public void Teardown()
        {
            foreach (var go in m_CreatedObjects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            m_CreatedObjects.Clear();
            Selection.activeObject = null;

            foreach (var file in m_CreatedFiles)
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
            m_CreatedFiles.Clear();
        }

        private GameObject CreateTestGameObject(string name)
        {
            var go = new GameObject(name);
            m_CreatedObjects.Add(go);
            return go;
        }

        [Test]
        public void Hierarchy_ActiveScene_ReturnsFormattedHierarchy()
        {
            var root = CreateTestGameObject("TestRoot_Player");
            var child = CreateTestGameObject("TestChild_Weapon");
            child.transform.SetParent(root.transform);

            string hierarchy = Inspect.Hierarchy(root);

            StringAssert.Contains("TestRoot_Player [active] [Transform]", hierarchy);
            StringAssert.Contains("  TestChild_Weapon [active] [Transform]", hierarchy);
        }

        [Test]
        public void Hierarchy_DeeplyNestedHierarchy_TruncatesAtMaxDepthWithoutThrowing()
        {
            var root = CreateTestGameObject("Deep_0");
            var current = root;
            for (int i = 1; i <= 20; i++)
            {
                var next = CreateTestGameObject($"Deep_{i}");
                next.transform.SetParent(current.transform);
                current = next;
            }

            string hierarchy = Inspect.Hierarchy(root, maxDepth: 3);

            StringAssert.Contains("Deep_0", hierarchy);
            StringAssert.Contains("Deep_3", hierarchy);
            StringAssert.Contains("... (truncated: maximum depth reached)", hierarchy);
            Assert.IsFalse(hierarchy.Contains("Deep_5"));
        }

        [Test]
        public void Hierarchy_ItemLimitExceeded_AppendsItemMarker()
        {
            var root = CreateTestGameObject("MultiRoot");
            for (int i = 1; i <= 10; i++)
            {
                var child = CreateTestGameObject($"Child_{i}");
                child.transform.SetParent(root.transform);
            }

            string hierarchy = Inspect.Hierarchy(root, maxDepth: 32, maxItems: 4);

            StringAssert.Contains("MultiRoot", hierarchy);
            StringAssert.Contains("... (truncated: maximum item count reached)", hierarchy);
        }

        [Test]
        public void Object_GameObject_ListsPathAndComponents()
        {
            var go = CreateTestGameObject("HeroObject");
            go.tag = "Untagged";
            go.AddComponent<BoxCollider>();

            string result = Inspect.GameObject(go);

            StringAssert.Contains("GameObject: \"HeroObject\"", result);
            StringAssert.Contains("Active: true", result);
            StringAssert.Contains("BoxCollider", result);
            StringAssert.Contains("Transform", result);
        }

        [Test]
        public void Object_NullTarget_ReturnsNull()
        {
            string result = Inspect.Object(null);
            Assert.AreEqual("null", result);
        }

        [Test]
        public void Object_Component_ListsPropertiesAndGameObject()
        {
            var go = CreateTestGameObject("TestCompOwner");
            var collider = go.AddComponent<BoxCollider>();

            string result = Inspect.Component(collider);

            StringAssert.Contains("Component: BoxCollider", result);
            StringAssert.Contains("GameObject: \"TestCompOwner\"", result);
            StringAssert.Contains("Enabled: true", result);
        }

        [Test]
        public void FindAssets_ByNameOrType_ReturnsNormalizedPaths()
        {
            string result = Inspect.FindAssets(type: "Script", maxItems: 5);

            Assert.IsNotNull(result);
            // Must contain normalized forward-slash paths
            Assert.IsFalse(result.Contains("\\"));
        }

        [Test]
        public void Selection_WhenNothingSelected_ReturnsEmptyNotification()
        {
            Selection.activeObject = null;
            Selection.objects = new UnityEngine.Object[0];

            string result = Inspect.Selection();

            Assert.AreEqual("(No active selection in Editor)", result);
        }

        [Test]
        public void Selection_WithActiveGameObject_ReturnsDetails()
        {
            var go = CreateTestGameObject("SelectedObject");
            Selection.activeGameObject = go;

            string result = Inspect.Selection();

            StringAssert.Contains("Active Selection (GameObject): \"SelectedObject\"", result);
            StringAssert.Contains("Components", result);
        }

        [Test]
        public void MainCamera_WhenCameraPresent_ReturnsCameraDetails()
        {
            var camGo = CreateTestGameObject("InspectTestCam");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";

            string result = Inspect.MainCamera();

            Assert.IsNotNull(result);
            StringAssert.Contains("Main Camera", result);
            StringAssert.Contains("Clipping Planes", result);
            StringAssert.Contains("Projection", result);
        }

        [Test]
        public void Inspect_GameObject_ReturnsConsistentOutput()
        {
            var go = CreateTestGameObject("AliasGo");

            string r1 = Inspect.GameObject(go);
            string r2 = Inspect.GameObject(go);

            Assert.AreEqual(r1, r2);
        }

        [Test]
        public void Hierarchy_TransformRoot_ReturnsFormattedHierarchy()
        {
            var root = CreateTestGameObject("TransformRoot");
            var child = CreateTestGameObject("TransformChild");
            child.transform.SetParent(root.transform);

            string result = Inspect.Hierarchy(root.transform);
            StringAssert.Contains("TransformRoot [active]", result);
            StringAssert.Contains("  TransformChild [active]", result);
        }

        [Test]
        public void Object_Collections_FormatsItems()
        {
            var list = new List<string> { "First", "Second" };
            string result = Inspect.Object(list);
            StringAssert.Contains("List`1 (2 items):", result);
            StringAssert.Contains("[0]: \"First\"", result);
            StringAssert.Contains("[1]: \"Second\"", result);
        }

        [Test]
        public void Hierarchy_NullAndDestroyedTransform_ReturnsNotification()
        {
            Transform nullTr = null;
            string nullResult = Inspect.Hierarchy(nullTr);
            Assert.AreEqual("(Transform is null)", nullResult);

            var go = CreateTestGameObject("ToDestroy");
            var tr = go.transform;
            UnityEngine.Object.DestroyImmediate(go);
            string destroyedResult = Inspect.Hierarchy(tr);
            Assert.AreEqual("(Transform is destroyed)", destroyedResult);
        }

        [Test]
        public void Object_LeafStruct_FormatsDirectly()
        {
            var vec = new Vector3(1.23f, 4.56f, 7.89f);
            string result = Inspect.Object(vec);
            StringAssert.Contains("1.23", result);
            StringAssert.Contains("4.56", result);
            StringAssert.Contains("7.89", result);
        }

        [Test]
        public void Selection_WithMultipleDestroyedObjects_ReturnsCleanNotification()
        {
            var go1 = CreateTestGameObject("DestroyedSelection1");
            var go2 = CreateTestGameObject("DestroyedSelection2");
            UnityEngine.Object.DestroyImmediate(go1);
            UnityEngine.Object.DestroyImmediate(go2);

            Selection.objects = new UnityEngine.Object[] { go1, go2 };
            string result = Inspect.Selection();
            Assert.AreEqual("(No active selection in Editor)", result);
        }

        [Test]
        public void Hierarchy_ActiveScenePath_MatchesLoadedScene()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(activeScene.path))
            {
                string result = Inspect.Hierarchy(activeScene.path);
                Assert.IsFalse(result.Contains("is not currently loaded"));
            }
        }

        [Test]
        public void PrefabStage_WhenNoPrefabOpen_ReturnsCleanNotification()
        {
            string result = Inspect.PrefabStage();
            Assert.AreEqual("(No active Prefab Stage open in Editor)", result);
        }

        [Test]
        public void SceneState_ReturnsFormattedSceneState()
        {
            string result = Inspect.SceneState();
            Assert.IsNotNull(result);
            StringAssert.Contains("Active Scene:", result);
            StringAssert.Contains("Loaded Scenes", result);
        }

        [Test]
        public void CaptureCamera_DefaultCamera_SavesPngAndReturnsSummary()
        {
            string outputPath = "Temp/test_inspect_cam_default.png";
            m_CreatedFiles.Add(outputPath);

            string result = Inspect.CaptureCamera(outputPath, width: 256, height: 192);

            StringAssert.Contains("Captured camera", result);
            StringAssert.Contains("(256x192,", result);
            StringAssert.Contains("Temp/test_inspect_cam_default.png", result);
            Assert.IsTrue(System.IO.File.Exists(outputPath));
            Assert.Greater(new System.IO.FileInfo(outputPath).Length, 0);
        }

        [Test]
        public void CaptureCamera_CustomCameraAndOutputPath_SavesPngAtCustomPath()
        {
            string outputPath = "Temp/SubFolder/test_inspect_cam_custom.png";
            m_CreatedFiles.Add(outputPath);

            var camGo = CreateTestGameObject("SpecialAngleCam");
            var cam = camGo.AddComponent<Camera>();

            string result = Inspect.CaptureCamera(outputPath, width: 320, height: 240, camera: cam);

            StringAssert.Contains("Captured camera \"SpecialAngleCam\"", result);
            StringAssert.Contains("(320x240,", result);
            StringAssert.Contains("Temp/SubFolder/test_inspect_cam_custom.png", result);
            Assert.IsTrue(System.IO.File.Exists(outputPath));
            Assert.Greater(new System.IO.FileInfo(outputPath).Length, 0);
        }

        [Test]
        public void CaptureCamera_ClampsInvalidDimensions_SavesValidPng()
        {
            string outputPath = "Temp/test_inspect_cam_clamped.png";
            m_CreatedFiles.Add(outputPath);

            string result = Inspect.CaptureCamera(outputPath, width: 5, height: 10000);

            StringAssert.Contains("(16x4096,", result);
            Assert.IsTrue(System.IO.File.Exists(outputPath));
        }

        [Test]
        public void CaptureCamera_CleansUpTextures_DoesNotLeakRenderTextureOrTexture2D()
        {
            string outputPath = "Temp/test_inspect_cam_cleanup.png";
            m_CreatedFiles.Add(outputPath);

            var camGo = CreateTestGameObject("CleanupCam");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";

            var initialActive = RenderTexture.active;
            var initialTarget = cam.targetTexture;

            string result = Inspect.CaptureCamera(outputPath, width: 128, height: 128, camera: cam);

            Assert.AreEqual(initialActive, RenderTexture.active);
            Assert.AreEqual(initialTarget, cam.targetTexture);
            StringAssert.Contains("Captured camera \"CleanupCam\"", result);
        }

        [Test]
        public void CaptureSceneView_WhenSceneViewPresent_SavesPngAndReturnsSummary()
        {
            string outputPath = "Temp/test_inspect_sceneview.png";
            m_CreatedFiles.Add(outputPath);

            string result = Inspect.CaptureSceneView(outputPath, width: 256, height: 192);

            Assert.IsNotNull(result);
            if (result.Contains("(No active SceneView"))
            {
                // In headless or batchmode environments without SceneView window
                Assert.Pass("No SceneView available in this test environment.");
            }
            else
            {
                StringAssert.Contains("Captured SceneView (256x192,", result);
                StringAssert.Contains("Temp/test_inspect_sceneview.png", result);
                Assert.IsTrue(System.IO.File.Exists(outputPath));
                Assert.Greater(new System.IO.FileInfo(outputPath).Length, 0);
            }
        }

        [Test]
        public void Inspect_Help_IncludesVisualCaptureCategoryAndMethods()
        {
            string help = Inspect.Help();

            StringAssert.Contains("Visual Capture:", help);
            StringAssert.Contains("CaptureCamera", help);
            StringAssert.Contains("CaptureSceneView", help);
        }

        [Test]
        public void Dependencies_PathNormalization_AndTypeAnnotations()
        {
            string matPath = "Assets/TestInspectDepsMat.mat";
            string texPath = "Assets/TestInspectDepsTex.asset";

            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, matPath);
            var tex = new Texture2D(16, 16);
            AssetDatabase.CreateAsset(tex, texPath);
            mat.mainTexture = tex;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            try
            {
                // Test backslash normalization
                string result = Inspect.Dependencies("Assets\\TestInspectDepsMat.mat");
                StringAssert.Contains("Dependencies found (1):", result);
                StringAssert.Contains("Assets/TestInspectDepsTex.asset [Texture2D]", result);
                Assert.IsFalse(result.Contains("\\"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.DeleteAsset(texPath);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void Dependencies_MissingAndNonExistent_ReturnsDiagnostic()
        {
            Assert.AreEqual("(Missing asset path)", Inspect.Dependencies(""));
            Assert.AreEqual("(Missing asset path)", Inspect.Dependencies("   "));
            Assert.AreEqual("(Missing asset path)", Inspect.Dependencies(null));

            string nonExistentPath = Inspect.Dependencies("Assets/NonExistentAsset_123.prefab");
            Assert.AreEqual("(Asset not found at path: \"Assets/NonExistentAsset_123.prefab\")", nonExistentPath);

            string nonExistentName = Inspect.Dependencies("NonExistentPattern_ZXY_987");
            Assert.AreEqual("(Asset not found matching: \"NonExistentPattern_ZXY_987\")", nonExistentName);
        }

        [Test]
        public void Dependencies_ItemTruncation_LimitsResults()
        {
            string matPath = "Assets/TestInspectTruncMat.mat";
            string tex1Path = "Assets/TestInspectTruncTex1.asset";
            string tex2Path = "Assets/TestInspectTruncTex2.asset";

            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, matPath);
            var tex1 = new Texture2D(16, 16);
            AssetDatabase.CreateAsset(tex1, tex1Path);
            var tex2 = new Texture2D(16, 16);
            AssetDatabase.CreateAsset(tex2, tex2Path);
            mat.mainTexture = tex1;
            mat.SetTexture("_EmissionMap", tex2);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            try
            {
                string result = Inspect.Dependencies(matPath, maxItems: 1);
                StringAssert.Contains("Dependencies found (2):", result);
                StringAssert.Contains("... (truncated: maximum item count reached)", result);
            }
            finally
            {
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.DeleteAsset(tex1Path);
                AssetDatabase.DeleteAsset(tex2Path);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void Referencers_FindsReferencingAssets_WithNormalizedPaths()
        {
            string matPath = "Assets/TestInspectRefMat.mat";
            string texPath = "Assets/TestInspectRefTex.asset";

            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, matPath);
            var tex = new Texture2D(16, 16);
            AssetDatabase.CreateAsset(tex, texPath);
            mat.mainTexture = tex;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            try
            {
                // Test backslash normalization
                string result = Inspect.Referencers("Assets\\TestInspectRefTex.asset", searchFolder: "Assets");
                StringAssert.Contains("Referencers found (1):", result);
                StringAssert.Contains("Assets/TestInspectRefMat.mat [Material]", result);
                Assert.IsFalse(result.Contains("\\"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.DeleteAsset(texPath);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void Referencers_MissingAndNonExistent_ReturnsDiagnostic()
        {
            Assert.AreEqual("(Missing target asset path)", Inspect.Referencers(""));
            Assert.AreEqual("(Missing target asset path)", Inspect.Referencers("   "));
            Assert.AreEqual("(Missing target asset path)", Inspect.Referencers(null));

            string nonExistentPath = Inspect.Referencers("Assets/NonExistentAsset_123.prefab");
            Assert.AreEqual("(Asset not found at path: \"Assets/NonExistentAsset_123.prefab\")", nonExistentPath);

            string nonExistentFolder = Inspect.Referencers("Assets/Scenes/SampleScene.unity", "Assets/NonExistentFolder_123");
            Assert.AreEqual("(Folder not found: \"Assets/NonExistentFolder_123\")", nonExistentFolder);
        }

        [Test]
        public void Inspect_Help_IncludesDependenciesAndReferencersUnderAssetsAndPrefabs()
        {
            string help = Inspect.Help();

            StringAssert.Contains("Assets & Prefabs:", help);
            StringAssert.Contains("Dependencies", help);
            StringAssert.Contains("Referencers", help);
        }
    }
}
