using System;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public static class MonacoAssetsBuildPostprocessor
{
  [PostProcessBuild(500)]
  public static void CopyMonacoAssets(BuildTarget target, string playerPath)
  {
    if (target != BuildTarget.StandaloneWindows64) return;

    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
    string sourceRoot = Path.Combine(projectRoot, "third_party");
    string playerDirectory = Path.GetDirectoryName(playerPath);
    string playerDataDirectory = Path.Combine(
      playerDirectory,
      Path.GetFileNameWithoutExtension(playerPath) + "_Data");
    string destinationRoot = Path.Combine(playerDataDirectory, "StreamingAssets", "third_party");

    CopyDirectory(
      Path.Combine(sourceRoot, "monaco-editor-0.14.3"),
      Path.Combine(destinationRoot, "monaco-editor-0.14.3"));
    CopyDirectory(
      Path.Combine(sourceRoot, "js-editor"),
      Path.Combine(destinationRoot, "js-editor"));

    string es5Root = Path.Combine(sourceRoot, "es5");
    File.Copy(
      Path.Combine(es5Root, "es5.d.ts.js"),
      Path.Combine(destinationRoot, "js-editor", "es5.d.ts.js"),
      true);
    File.Copy(
      Path.Combine(es5Root, "LICENSE"),
      Path.Combine(destinationRoot, "js-editor", "es5.LICENSE"),
      true);
  }

  private static void CopyDirectory(string source, string destination)
  {
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
    Directory.CreateDirectory(destination);

    foreach (string file in Directory.GetFiles(source))
    {
      File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    }

    foreach (string directory in Directory.GetDirectories(source))
    {
      CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
  }
}
