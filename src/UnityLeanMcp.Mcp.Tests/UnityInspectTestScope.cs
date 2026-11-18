using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityLeanMcp.Mcp.Tests;

internal sealed class UnityInspectTestScope : IDisposable
{
    private readonly List<string> _createdFiles = new();

    public UnityInspectTestScope()
    {
        ResetStubs();
    }

    public void TrackFile(string path)
    {
        _createdFiles.Add(path);
    }

    public static void ResetStubs()
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
        UnityEngine.Object.FindObjectsOfTypeFunc = null;
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
        UnityEngine.UIElements.UIDocument.AllUIDocuments.Clear();
    }

    public void Dispose()
    {
        ResetStubs();

        foreach (var file in _createdFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }
        _createdFiles.Clear();
    }
}
