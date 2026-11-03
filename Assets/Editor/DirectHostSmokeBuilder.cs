using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DirectHostSmokeBuilder
{
  public static void Build()
  {
    string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
    string splashScene = scenes.First(path => Path.GetFileNameWithoutExtension(path).Equals("splash", System.StringComparison.OrdinalIgnoreCase));
    string smokeScene = "Assets/DirectHostSmokeScene.unity";
    string outputDirectory = Path.Combine(Path.GetTempPath(), "GameBuilderDirectHostSmoke");
    Directory.CreateDirectory(outputDirectory);

    try
    {
      EditorSceneManager.OpenScene(splashScene, OpenSceneMode.Single);
      new GameObject("DirectHostSmokeBootstrap").AddComponent<DirectHostSmokeBootstrap>();
      if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), smokeScene))
      {
        throw new System.Exception("Could not save the temporary direct-host smoke scene.");
      }

      string[] smokeScenes = scenes.Select(path => path == splashScene ? smokeScene : path).ToArray();
      BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
      {
        scenes = smokeScenes,
        locationPathName = Path.Combine(outputDirectory, "GameBuilder.exe"),
        target = BuildTarget.StandaloneWindows64,
        options = BuildOptions.Development
      });
      if (report == null || report.summary.result != BuildResult.Succeeded)
      {
        throw new System.Exception("Direct-host smoke player build failed.");
      }

      string executable = Path.GetFullPath(report.summary.outputPath);
      string[] criticalFiles = Directory.GetFiles(outputDirectory, "*.dll", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(outputDirectory, "*.exe", SearchOption.AllDirectories))
        .Where(path => !string.Equals(Path.GetFullPath(path), executable, System.StringComparison.OrdinalIgnoreCase))
        .Select(path => Path.GetRelativePath(outputDirectory, path))
        .ToArray();
      File.WriteAllLines(Path.Combine(outputDirectory, "critical-files.txt"), criticalFiles);
      Debug.Log($"Direct-host smoke player built at {report.summary.outputPath}");
    }
    finally
    {
      if (EditorSceneManager.GetActiveScene().path == smokeScene)
      {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
      }
      AssetDatabase.DeleteAsset(smokeScene);
      AssetDatabase.Refresh();
    }
  }
}
