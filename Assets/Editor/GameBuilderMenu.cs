/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.IO;
using SD = System.Diagnostics;
using System.Linq;

public class GameBuilderMenu : UnityEditor.Build.IPreprocessBuildWithReport
{
  const string MacV8ArchitectureError =
    "The bundled macOS V8 plugin supports x86_64 only. Use Game Builder > Build macOS Intel (Rosetta) for behavior scripts.";

  public int callbackOrder { get { return int.MinValue; } }

  public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
  {
    if (!SupportsBundledMacOSV8(report.summary.platform))
    {
      throw new UnityEditor.Build.BuildFailedException(MacV8ArchitectureError);
    }
  }

  static bool SupportsBundledMacOSV8(BuildTarget target)
  {
    return target != BuildTarget.StandaloneOSX ||
      UnityEditor.OSXStandalone.UserBuildSettings.architecture == UnityEditor.Build.OSArchitecture.x64;
  }

  [MenuItem("Game Builder/Stop %#y")]
  public static void StopPlayMode()
  {
    EditorApplication.isPlaying = false;
  }

  [MenuItem("Game Builder/Build")]
  public static void Build()
  {
    BuildInternal(Path.GetFullPath("./build_output"), 0);
  }

  [MenuItem("Game Builder/Build macOS Intel (Rosetta)")]
  public static void BuildMacOSIntelForRosetta()
  {
    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
    {
      throw new System.InvalidOperationException("Select the macOS Standalone build target before building for Intel (Rosetta).");
    }

    UnityEditor.Build.OSArchitecture previousArchitecture = UnityEditor.OSXStandalone.UserBuildSettings.architecture;
    try
    {
      UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.x64;
      BuildInternal(Path.GetFullPath("./build_output_macos_intel_rosetta"), 0);
    }
    finally
    {
      UnityEditor.OSXStandalone.UserBuildSettings.architecture = previousArchitecture;
    }
  }

  [MenuItem("Game Builder/Build macOS Intel (Rosetta)", true)]
  static bool BuildMacOSIntelForRosettaValidate()
  {
    return EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneOSX;
  }

  static void BuildInternal(string outDir, BuildOptions extraBuildOpts)
  {
    if (!SupportsBundledMacOSV8(EditorUserBuildSettings.activeBuildTarget))
    {
      throw new System.InvalidOperationException(MacV8ArchitectureError);
    }

    if (Directory.Exists(outDir))
    {
      if (Application.isBatchMode)
      {
        throw new IOException($"Build output directory already exists: {outDir}");
      }
      bool delete = EditorUtility.DisplayDialog("Delete previous build?", $"If '{outDir}' is a previous build, it must be deleted before proceeding (merely overwriting a previous build can lead to issues).", "Delete it", "Cancel build");
      if (!delete) return;
      Debug.Log($"Deleting {outDir}...");
      Directory.Delete(outDir, true);
    }

    var target = EditorUserBuildSettings.activeBuildTarget;

    string exeName = null;
    switch (target)
    {
      case BuildTarget.StandaloneWindows64:
        exeName = "Game Builder.exe";
        break;
      case BuildTarget.StandaloneOSX:
        exeName = "Game Builder.app";
        break;
      case BuildTarget.StandaloneLinux64:
        exeName = "Game Builder.x86_64";
        break;
      default:
        throw new System.Exception($"Unsupported build target: {target}");
    }

    string buildTargetPath = Path.Combine(outDir, exeName);
    var report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes, buildTargetPath, target, BuildOptions.ForceEnableAssertions | BuildOptions.StrictMode | extraBuildOpts);

    if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
    {
      throw new System.Exception($"Build failed for {target}: {report.summary.result}");
    }
    if (target == BuildTarget.StandaloneWindows64)
    {
      string bootConfigPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(buildTargetPath) + "_Data", "boot.config");
      File.AppendAllText(bootConfigPath, System.Environment.NewLine + "force-gfx-direct=1" + System.Environment.NewLine);
    }

    if (target == BuildTarget.StandaloneLinux64)
    {
      string managedDirectory = Path.Combine(outDir, Path.GetFileNameWithoutExtension(buildTargetPath) + "_Data", "Managed");
      PatchLinuxAssimpImports(Path.Combine(managedDirectory, "AssimpNet.dll"));
    }


    // Get the GIT commit version and write it to a text file
    SD.Process gitCall = new SD.Process();
    gitCall.StartInfo.FileName = "git";
    gitCall.StartInfo.Arguments = "rev-parse HEAD";
    gitCall.StartInfo.UseShellExecute = false;
    gitCall.StartInfo.RedirectStandardOutput = true;
    gitCall.StartInfo.RedirectStandardError = true;
    gitCall.Start();
    string gitCommit = gitCall.StandardOutput.ReadToEnd().Trim();
    string stdErr = gitCall.StandardError.ReadToEnd();
    gitCall.WaitForExit();
    if (gitCall.ExitCode != 0 || gitCommit.IsNullOrEmpty())
    {
      Debug.LogError($"Failed to run git. Code: {gitCall.ExitCode}. STDERR: {stdErr}");
    }
    else
    {
      File.WriteAllText(Path.Combine(outDir, "built-commit"), gitCommit);
    }

    // Create the critical files list
    using (StreamWriter writer = new StreamWriter(Path.Combine(outDir, "critical-files.txt")))
    {
      foreach (string filePath in
        Directory.GetFiles(outDir, "*.dll", SearchOption.AllDirectories).
        Concat(Directory.GetFiles(outDir, "*.exe", SearchOption.AllDirectories)).
        Concat(Directory.GetFiles(outDir, "*.so", SearchOption.AllDirectories)))
      {
        // NOTE: It doesn't work if you pass outDir to MakeRelativePath..you end
        // up getting "content/blah.exe"
        string relPath = filePath.MakeRelativePath(buildTargetPath);
        if (relPath.IsNullOrEmpty())
        {
          continue;
        }
        // UnityEngine.Debug.Log(relPath);
        writer.WriteLine(relPath);
      }
    }

    if (!Application.isBatchMode)
    {
      bool show = EditorUtility.DisplayDialog("Build done!", $@"
{System.DateTime.Now}

Build is located at: {outDir}
".Trim(), "Show build location", "OK");

      if (show)
      {
        EditorUtility.RevealInFinder(outDir);
      }
    }
  }

  static void PatchLinuxAssimpImports(string assemblyPath)
  {
    if (!File.Exists(assemblyPath))
    {
      throw new FileNotFoundException("The Linux player is missing AssimpNet.dll.", assemblyPath);
    }

    string patchedPath = assemblyPath + ".patched";
    int legacyImports = 0;
    int supportedImports = 0;
    using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(assemblyPath))
    {
      foreach (Mono.Cecil.TypeDefinition type in module.Types)
      {
        PatchLinuxAssimpImports(type, ref legacyImports, ref supportedImports);
      }

      if (legacyImports + supportedImports != 4)
      {
        throw new InvalidDataException($"Expected four Assimp libdl imports, found {legacyImports + supportedImports}.");
      }
      if (legacyImports > 0)
      {
        module.Write(patchedPath);
      }
    }

    if (legacyImports > 0)
    {
      File.Copy(patchedPath, assemblyPath, true);
      File.Delete(patchedPath);
      Debug.Log("Updated Assimp's libdl binding to libdl.so.2 for Linux.");
    }
  }

  static void PatchLinuxAssimpImports(Mono.Cecil.TypeDefinition type, ref int legacyImports, ref int supportedImports)
  {
    foreach (Mono.Cecil.MethodDefinition method in type.Methods)
    {
      Mono.Cecil.PInvokeInfo import = method.PInvokeInfo;
      if (import == null) continue;
      if (import.Module.Name == "libdl.so")
      {
        import.Module.Name = "libdl.so.2";
        legacyImports++;
      }
      else if (import.Module.Name == "libdl.so.2")
      {
        supportedImports++;
      }
    }

    foreach (Mono.Cecil.TypeDefinition nestedType in type.NestedTypes)
    {
      PatchLinuxAssimpImports(nestedType, ref legacyImports, ref supportedImports);
    }
  }



  // [MenuItem("Game Builder/Gen Simple-Data Terrain")]
  public static void GenSimpleDataTerrain()
  {
    using (var ms = new System.IO.MemoryStream())
    {
      using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress))
      using (var bw = new System.IO.BinaryWriter(gz))
      {
        const ushort version = 0;
        uint numBlocks = 5;

        bw.Write(version);

        bw.Write(numBlocks);
        for (int y = 0; y < numBlocks; y++)
        {
          bw.Write((short)0);
          bw.Write((short)y);
          bw.Write((short)0);

          bw.Write((byte)1);
          bw.Write((byte)0);
          bw.Write((ushort)20); // Grass
        }
      }

      // Must do this here - not inside any of the inner 'using' scopes above.
      byte[] zippedBytes = ms.ToArray();
      Util.Log($"simpleData string: {System.Convert.ToBase64String(zippedBytes)}");
    }
  }
}
