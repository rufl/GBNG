using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class FullProductSmokeBuildTemp
{
  public static void Build()
  {
    string outputDirectory = Path.Combine(Path.GetTempPath(), "GameBuilder-Full-Product-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDirectory);
    string playerPath = Path.Combine(outputDirectory, "GameBuilder.exe");
    string[] scenes = EditorBuildSettings.scenes
      .Where(scene => scene.enabled)
      .Select(scene => scene.path)
      .ToArray();
    BuildPlayerOptions options = new BuildPlayerOptions
    {
      scenes = scenes,
      locationPathName = playerPath,
      target = BuildTarget.StandaloneWindows64,
      options = BuildOptions.ForceEnableAssertions | BuildOptions.StrictMode
    };

    BuildReport report = BuildPipeline.BuildPlayer(options);
    if (report.summary.result != BuildResult.Succeeded)
    {
      throw new InvalidOperationException($"Full product smoke build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
    }

    File.WriteAllText(Path.Combine(outputDirectory, "built-commit"), "4a56c8fc-smoke");
    string executable = Path.GetFullPath(playerPath);
    string[] criticalFiles = Directory.GetFiles(outputDirectory, "*.dll", SearchOption.AllDirectories)
      .Concat(Directory.GetFiles(outputDirectory, "*.exe", SearchOption.AllDirectories))
      .Where(path => !string.Equals(Path.GetFullPath(path), executable, StringComparison.OrdinalIgnoreCase))
      .Select(path => Path.GetRelativePath(outputDirectory, path))
      .ToArray();
    File.WriteAllLines(Path.Combine(outputDirectory, "critical-files.txt"), criticalFiles);
    Debug.Log($"FULL_PRODUCT_BUILD_RESULT|Succeeded|{playerPath}|critical-files={criticalFiles.Length}");
  }
}
