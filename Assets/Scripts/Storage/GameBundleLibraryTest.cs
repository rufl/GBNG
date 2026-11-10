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

#if UNITY_EDITOR

using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using GameBuilder;

public class GameBundleLibraryTest
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void CopyBundlePreservesNestedAssets()
  {
    string relativeRoot = "GameBuilderBundleCopyTest-" + Guid.NewGuid().ToString("N");
    string absoluteRoot = Path.Combine(Util.AbstractLocation.PersistentData.GetConcretePath(), relativeRoot);
    string sourceId = "source-" + Guid.NewGuid().ToString("N");
    string destinationId = "destination-" + Guid.NewGuid().ToString("N");
    GameObject owner = new GameObject("Game bundle copy test");
    owner.SetActive(false);
    GameBundleLibrary library = owner.AddComponent<GameBundleLibrary>();
    FieldInfo locationField = typeof(GameBundleLibrary).GetField("location", PrivateInstance);
    Assert.IsNotNull(locationField);
    locationField.SetValue(library, new Util.AbstractPath
    {
      root = Util.AbstractLocation.PersistentData,
      relativePath = relativeRoot
    });

    try
    {
      string sourceDirectory = Path.Combine(absoluteRoot, sourceId);
      string nestedAsset = Path.Combine(sourceDirectory, "assets", "items", "model.bin");
      Directory.CreateDirectory(Path.GetDirectoryName(nestedAsset));
      File.WriteAllText(Path.Combine(sourceDirectory, "scene.voos"), "scene");
      File.WriteAllText(nestedAsset, "asset");

      library.CopyBundle(sourceId, destinationId);

      string destinationDirectory = Path.Combine(absoluteRoot, destinationId);
      Assert.AreEqual("scene", File.ReadAllText(Path.Combine(destinationDirectory, "scene.voos")));
      Assert.AreEqual("asset", File.ReadAllText(Path.Combine(destinationDirectory, "assets", "items", "model.bin")));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(owner);
      if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, true);
    }
  }
}

#endif
