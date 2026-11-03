using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class PlatformNamesProbe
{
    public static void Run()
    {
        const string scenePath = "Assets/MonacoSmokeTemp.unity";
        GameObject codeCanvasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/Inspector/CodeCanvas.prefab");
        if (codeCanvasPrefab == null) throw new System.InvalidOperationException("CodeCanvas prefab not found.");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject codeCanvas = PrefabUtility.InstantiatePrefab(codeCanvasPrefab) as GameObject;
        if (codeCanvas == null) throw new System.InvalidOperationException("Could not instantiate CodeCanvas prefab.");

        RectTransform canvasRect = codeCanvas.GetComponent<RectTransform>();
        canvasRect.localScale = Vector3.one;
        RectTransform browserRect = codeCanvas.transform.Find("CodeBrowser").GetComponent<RectTransform>();
        browserRect.anchorMin = Vector2.zero;
        browserRect.anchorMax = Vector2.one;
        browserRect.offsetMin = Vector2.zero;
        browserRect.offsetMax = Vector2.zero;
        codeCanvas.AddComponent<MonacoPlayerSmokeRunner>();

        if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new System.InvalidOperationException("Could not save Monaco smoke scene.");
        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = @"C:\Users\rafael\AppData\Local\Temp\GameBuilderMonacoSmoke-20261103\MonacoSmoke.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"MONACO_SMOKE_BUILD_RESULT|{report.summary.result}|{report.summary.totalErrors}");
    }
}
