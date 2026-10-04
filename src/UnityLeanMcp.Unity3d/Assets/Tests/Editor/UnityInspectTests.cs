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

            string hierarchy = UnityInspect.Hierarchy(root);

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

            string hierarchy = UnityInspect.Hierarchy(root, maxDepth: 3);

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

            string hierarchy = UnityInspect.Hierarchy(root, maxDepth: 32, maxItems: 4);

            StringAssert.Contains("MultiRoot", hierarchy);
            StringAssert.Contains("... (truncated: maximum item count reached)", hierarchy);
        }

        [Test]
        public void Object_GameObject_ListsPathAndComponents()
        {
            var go = CreateTestGameObject("HeroObject");
            go.tag = "Untagged";
            go.AddComponent<BoxCollider>();

            string result = UnityInspect.GameObject(go);

            StringAssert.Contains("GameObject: \"HeroObject\"", result);
            StringAssert.Contains("Active: true", result);
            StringAssert.Contains("BoxCollider", result);
            StringAssert.Contains("Transform", result);
        }

        [Test]
        public void Object_NullTarget_ReturnsNull()
        {
            string result = UnityInspect.Object(null);
            Assert.AreEqual("null", result);
        }

        [Test]
        public void Object_Component_ListsPropertiesAndGameObject()
        {
            var go = CreateTestGameObject("TestCompOwner");
            var collider = go.AddComponent<BoxCollider>();

            string result = UnityInspect.Component(collider);

            StringAssert.Contains("Component: BoxCollider", result);
            StringAssert.Contains("GameObject: \"TestCompOwner\"", result);
            StringAssert.Contains("Enabled: true", result);
        }

        [Test]
        public void FindAssets_ByNameOrType_ReturnsNormalizedPaths()
        {
            string result = UnityInspect.FindAssets(type: "Script", maxItems: 5);

            Assert.IsNotNull(result);
            // Must contain normalized forward-slash paths
            Assert.IsFalse(result.Contains("\\"));
        }

        [Test]
        public void Selection_WhenNothingSelected_ReturnsEmptyNotification()
        {
            Selection.activeObject = null;
            Selection.objects = new UnityEngine.Object[0];

            string result = UnityInspect.Selection();

            Assert.AreEqual("(No active selection in Editor)", result);
        }

        [Test]
        public void Selection_WithActiveGameObject_ReturnsDetails()
        {
            var go = CreateTestGameObject("SelectedObject");
            Selection.activeGameObject = go;

            string result = UnityInspect.Selection();

            StringAssert.Contains("Active Selection (GameObject): \"SelectedObject\"", result);
            StringAssert.Contains("Components", result);
        }

        [Test]
        public void MainCamera_WhenCameraPresent_ReturnsCameraDetails()
        {
            var camGo = CreateTestGameObject("InspectTestCam");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";

            string result = UnityInspect.MainCamera();

            Assert.IsNotNull(result);
            StringAssert.Contains("Main Camera", result);
            StringAssert.Contains("Clipping Planes", result);
            StringAssert.Contains("Projection", result);
        }

        [Test]
        public void InspectAlias_ForwardsCorrectly()
        {
            var go = CreateTestGameObject("AliasGo");

            string r1 = Inspect.GameObject(go);
            string r2 = UnityInspect.GameObject(go);

            Assert.AreEqual(r1, r2);
        }

        [Test]
        public void Hierarchy_TransformRoot_ReturnsFormattedHierarchy()
        {
            var root = CreateTestGameObject("TransformRoot");
            var child = CreateTestGameObject("TransformChild");
            child.transform.SetParent(root.transform);

            string result = UnityInspect.Hierarchy(root.transform);
            StringAssert.Contains("TransformRoot [active]", result);
            StringAssert.Contains("  TransformChild [active]", result);
        }

        [Test]
        public void Object_Collections_FormatsItems()
        {
            var list = new List<string> { "First", "Second" };
            string result = UnityInspect.Object(list);
            StringAssert.Contains("List`1 (2 items):", result);
            StringAssert.Contains("[0]: \"First\"", result);
            StringAssert.Contains("[1]: \"Second\"", result);
        }

        [Test]
        public void Hierarchy_NullAndDestroyedTransform_ReturnsNotification()
        {
            Transform nullTr = null;
            string nullResult = UnityInspect.Hierarchy(nullTr);
            Assert.AreEqual("(Transform is null)", nullResult);

            var go = CreateTestGameObject("ToDestroy");
            var tr = go.transform;
            UnityEngine.Object.DestroyImmediate(go);
            string destroyedResult = UnityInspect.Hierarchy(tr);
            Assert.AreEqual("(Transform is destroyed)", destroyedResult);
        }

        [Test]
        public void Object_LeafStruct_FormatsDirectly()
        {
            var vec = new Vector3(1.23f, 4.56f, 7.89f);
            string result = UnityInspect.Object(vec);
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
            string result = UnityInspect.Selection();
            Assert.AreEqual("(No active selection in Editor)", result);
        }

        [Test]
        public void Hierarchy_ActiveScenePath_MatchesLoadedScene()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(activeScene.path))
            {
                string result = UnityInspect.Hierarchy(activeScene.path);
                Assert.IsFalse(result.Contains("is not currently loaded"));
            }
        }
    }
}
