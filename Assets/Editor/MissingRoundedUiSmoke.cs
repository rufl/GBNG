using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI.ProceduralImage;

public static class MissingRoundedUiSmoke
{
  private static readonly string[] Prefabs =
  {
    "Assets/CardInterface/Prefabs/PanelNote.prefab",
    "Assets/Prefabs/CommandConsoleUI.prefab",
    "Assets/UIAssets/Prefabs/PlayHeaderAsset.prefab",
    "Assets/UIAssets/Prefabs/RenderableLibraryAsset.prefab",
    "Assets/UIAssets/Prefabs/WindowHeaderAsset.prefab",
    "Assets/UIAssets/Prefabs/Menu/ToolPanels/TerrainToolSettingsAsset.prefab",
  };

  public static void BuildMacOSX64()
  {
    int modifierCount = 0;
    foreach (string path in Prefabs)
    {
      GameObject root = PrefabUtility.LoadPrefabContents(path);
      if (!root) throw new InvalidOperationException($"Could not load prefab: {path}");
      try
      {
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
          GameObject gameObject = transform.gameObject;
          int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
          if (missingCount != 0)
          {
            throw new InvalidOperationException($"{path}: {gameObject.name} has {missingCount} missing MonoBehaviour(s).");
          }

          foreach (FreeModifier modifier in gameObject.GetComponents<FreeModifier>())
          {
            if (!gameObject.GetComponent<ProceduralImage>())
            {
              throw new InvalidOperationException($"{path}: {gameObject.name} has a FreeModifier without ProceduralImage.");
            }
            modifierCount++;
          }
        }
      }
      finally
      {
        PrefabUtility.UnloadPrefabContents(root);
      }
    }

    if (modifierCount != 11)
    {
      throw new InvalidOperationException($"Expected 11 restored rounded-image modifiers; found {modifierCount}.");
    }

    Debug.Log($"Rounded UI prefab smoke passed: {modifierCount} modifier/image pairs; building macOS x64 player.");
    GameBuilderMenu.BuildMacOSIntelForRosetta();
  }
}
