using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Build;
using UnityEngine;

public static class MonacoDesktopCrossBuildSmokeTemp
{
  public static void Build()
  {
    if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) != ScriptingImplementation.Mono2x)
    {
      throw new InvalidOperationException("UnityWebBrowser CEF builds require the Mono scripting backend.");
    }

    string root = Path.Combine(Path.GetTempPath(), "GameBuilder-Monaco-CrossBuild-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
    BuildOne(BuildTarget.StandaloneLinux64, Path.Combine(root, "Linux", "GameBuilder.x86_64"), scenes);

    const string architectureKey = "Architecture";
    string originalArchitecture = EditorUserBuildSettings.GetPlatformSettings("Standalone", "OSXUniversal", architectureKey);
    try
    {
      EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", architectureKey, "x64");
      BuildOne(BuildTarget.StandaloneOSX, Path.Combine(root, "Mac-x64", "GameBuilder.app"), scenes);

      EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", architectureKey, "ARM64");
      BuildOne(BuildTarget.StandaloneOSX, Path.Combine(root, "Mac-arm64", "GameBuilder.app"), scenes);
    }
    finally
    {
      EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", architectureKey, originalArchitecture);
    }

    Debug.Log("MONACO_CROSS_BUILD_RESULT|Succeeded|" + root);
  }

  private static void BuildOne(BuildTarget target, string playerPath, string[] scenes)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(playerPath));
    BuildPlayerOptions options = new BuildPlayerOptions
    {
      scenes = scenes,
      locationPathName = playerPath,
      target = target,
      options = BuildOptions.ForceEnableAssertions | BuildOptions.StrictMode
    };

    BuildReport report = BuildPipeline.BuildPlayer(options);
    if (report.summary.result != BuildResult.Succeeded)
    {
      throw new InvalidOperationException($"{target} build failed: {report.summary.result} ({report.summary.totalErrors} errors).");
    }

    string dataDirectory = target == BuildTarget.StandaloneOSX
      ? Path.Combine(playerPath, "Contents", "Resources", "Data")
      : Path.Combine(Path.GetDirectoryName(playerPath), Path.GetFileNameWithoutExtension(playerPath) + "_Data");
    string editorPage = Path.Combine(dataDirectory, "StreamingAssets", "third_party", "js-editor", "js-editor.html");
    if (!File.Exists(editorPage)) throw new FileNotFoundException($"{target} Monaco assets were not packaged.", editorPage);

    string[] cefExecutables = Directory.GetFiles(Path.GetDirectoryName(playerPath), "UnityWebBrowser.Engine.Cef", SearchOption.AllDirectories);
    if (cefExecutables.Length == 0) throw new FileNotFoundException($"{target} CEF engine executable was not packaged.");
    string[] licenses = Directory.GetFiles(Path.GetDirectoryName(playerPath), "LICENSE.CEF.txt", SearchOption.AllDirectories);
    if (licenses.Length == 0) throw new FileNotFoundException($"{target} CEF license was not packaged.");

    Debug.Log($"MONACO_CROSS_BUILD_RESULT|{target}|Succeeded|cef={cefExecutables[0]}|monaco={editorPage}");
  }
}
