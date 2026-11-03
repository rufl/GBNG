using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public static class PlatformNamesProbe
{
    public static void Run()
    {
        const string scenePath = "Assets/MonacoSmokeTemp.unity";
        GameObject fallbackPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/Inspector/BasicCodeBrowser.prefab");
        if (fallbackPrefab == null) throw new System.InvalidOperationException("BasicCodeBrowser prefab not found.");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject canvasObject = new GameObject("SmokeCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.anchorMin = Vector2.zero;
        canvasRect.anchorMax = Vector2.one;
        canvasRect.offsetMin = Vector2.zero;
        canvasRect.offsetMax = Vector2.zero;

        GameObject surfaceObject = new GameObject("CodeBrowser", typeof(RectTransform), typeof(RawImage), typeof(CodeBrowserWrapper));
        surfaceObject.transform.SetParent(canvasObject.transform, false);
        RectTransform surfaceRect = surfaceObject.GetComponent<RectTransform>();
        surfaceRect.anchorMin = Vector2.zero;
        surfaceRect.anchorMax = Vector2.one;
        surfaceRect.offsetMin = Vector2.zero;
        surfaceRect.offsetMax = Vector2.zero;

        GameObject fallbackObject = PrefabUtility.InstantiatePrefab(fallbackPrefab) as GameObject;
        if (fallbackObject == null) throw new System.InvalidOperationException("Could not instantiate BasicCodeBrowser prefab.");
        fallbackObject.transform.SetParent(surfaceObject.transform, false);
        BasicCodeBrowser basicBrowser = fallbackObject.GetComponentInChildren<BasicCodeBrowser>(true);
        if (basicBrowser == null) throw new System.InvalidOperationException("BasicCodeBrowser component not found in prefab.");
        fallbackObject.SetActive(false);

        CodeBrowserWrapper wrapper = surfaceObject.GetComponent<CodeBrowserWrapper>();
        SerializedObject wrapperProperties = new SerializedObject(wrapper);
        wrapperProperties.FindProperty("basicCodeBrowser").objectReferenceValue = basicBrowser;
        wrapperProperties.ApplyModifiedPropertiesWithoutUndo();

        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        canvasObject.AddComponent<MonacoPlayerSmokeRunner>();

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
