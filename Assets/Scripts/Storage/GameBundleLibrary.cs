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
using System.IO;
using UnityEngine;

namespace GameBuilder
{
  // Represents a bundle on disk, that may or may not exist. Read-only.
  public class GameBundle
  {
    [System.Serializable]
    public class Metadata
    {
      public string name;
      public string description;
    }

    string directory;

    Metadata metadata = null;
    GameBundleLibrary thumbnailOwner;

    public Metadata GetMetadata()
    {
      if (metadata == null)
      {
        if (File.Exists(GetMetadataPath()))
        {
          metadata = Util.ReadFromJson<GameBundle.Metadata>(GetMetadataPath());
        }
        else
        {
          metadata = new Metadata();
          metadata.name = Path.GetFileName(directory);
          metadata.description = "";
        }
      }

      return metadata;
    }

    public Texture2D GetThumbnail()
    {
      return thumbnailOwner.GetThumbnailForPath(GetThumbnailPath());
    }

    internal GameBundle(string directory, GameBundleLibrary thumbnailOwner)
    {
      this.directory = directory;
      this.thumbnailOwner = thumbnailOwner;
    }

    public string GetThumbnailPath()
    {
      return Path.Combine(directory, "thumbnail.png");
    }

    public string GetVoosPath()
    {
      return Path.Combine(directory, "scene.voos");
    }

    public string GetMetadataPath()
    {
      return Path.Combine(directory, "metadata.json");
    }

    public string GetAssetsPath()
    {
      return Path.Combine(directory, "assets");
    }
  }

  public class GameBundleLibrary : MonoBehaviour
  {
    public delegate void SaveDoneCallback(string bundleId);

    public struct Entry
    {
      public string id;
      public GameBundle bundle;

      public bool IsAutosave()
      {
        return AutoSaveController.IsAutosave(id);
      }
    }

    [SerializeField] Util.AbstractPath location;

    WorkshopAssetSource workshop;
    readonly Dictionary<string, Texture2D> thumbnailsByPath = new Dictionary<string, Texture2D>();
    readonly List<Texture2D> retiredThumbnails = new List<Texture2D>();


    public string GetLibraryAbsolutePath()
    {
      return location.GetAbsolute();
    }

    void Awake()
    {
      Util.UpgradeUserDataDir();

      Directory.CreateDirectory(location.GetAbsolute());

      Util.FindIfNotSet(this, ref workshop);
    }

    void OnDestroy()
    {
      ReleaseThumbnailTextures();
    }

    internal Texture2D GetThumbnailForPath(string thumbnailPath)
    {
      Texture2D thumbnail;
      if (thumbnailsByPath.TryGetValue(thumbnailPath, out thumbnail))
      {
        return thumbnail;
      }

      thumbnail = Util.ReadPngToTexture(thumbnailPath);
      if (thumbnail != null) thumbnailsByPath[thumbnailPath] = thumbnail;
      return thumbnail;
    }

    void InvalidateBundleThumbnail(string bundleId)
    {
      string thumbnailPath = Path.Combine(GetBundleDirectory(bundleId), "thumbnail.png");
      Texture2D thumbnail;
      if (!thumbnailsByPath.TryGetValue(thumbnailPath, out thumbnail)) return;
      thumbnailsByPath.Remove(thumbnailPath);
      if (thumbnail != null) retiredThumbnails.Add(thumbnail);
    }

    void ReleaseThumbnailTextures()
    {
      foreach (Texture2D thumbnail in thumbnailsByPath.Values)
      {
        DestroyThumbnail(thumbnail);
      }
      foreach (Texture2D thumbnail in retiredThumbnails)
      {
        DestroyThumbnail(thumbnail);
      }
      thumbnailsByPath.Clear();
      retiredThumbnails.Clear();
    }

    static void DestroyThumbnail(Texture2D thumbnail)
    {
      if (thumbnail == null) return;
      if (Application.isPlaying) UnityEngine.Object.Destroy(thumbnail);
      else UnityEngine.Object.DestroyImmediate(thumbnail);
    }

    public string GetBundleDirectory(string bundleId)
    {
      if (bundleId.IsNullOrEmpty())
      {
        throw new System.Exception("No bundleId given");
      }
      return Path.Combine(location.GetAbsolute(), bundleId);
    }

    // Returns an ID for the bundle. The current VOOS state will be saved as the scene.
    public string SaveNew(SaveLoadController saveLoad, GameBundle.Metadata metadata, Texture2D thumbnail, System.Action onSaveComplete)
    {
      string id = GenerateBundleId();
      SaveMaybeOverwrite(saveLoad, id, metadata, thumbnail, onSaveComplete);
      return id;
    }

    public void SaveMaybeOverwrite(SaveLoadController saveLoad, string id, GameBundle.Metadata metadata, Texture2D thumbnail, System.Action onSaveComplete)
    {
      string dir = GetBundleDirectory(id);
      Util.SetNormalFileAttributes(dir);
      Directory.CreateDirectory(dir);

      if (thumbnail != null)
      {
        string thumbnailPath = Path.Combine(dir, "thumbnail.png");
        Util.SetNormalFileAttributes(thumbnailPath);
        InvalidateBundleThumbnail(id);
        Util.SaveTextureToPng(thumbnail, thumbnailPath);
      }

      string metadataPath = Path.Combine(dir, "metadata.json");
      Util.SetNormalFileAttributes(metadataPath);
      File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata));

      string voosPath = Path.Combine(dir, "scene.voos");
      Util.SetNormalFileAttributes(voosPath);
      saveLoad.RequestSave(voosPath, onSaveComplete);

#if !USE_STEAMWORKS
      workshop.Save(Path.Combine(dir, "assets"));
#endif
    }

    public GameBundle GetBundle(string bundleId)
    {
      if (bundleId.IsNullOrEmpty())
      {
        return null;
      }
      return new GameBundle(GetBundleDirectory(bundleId), this);
    }

    public Entry GetBundleEntry(string bundleId)
    {
      return new Entry
      {
        id = bundleId,
        bundle = GetBundle(bundleId)
      };
    }

    public IEnumerable<Entry> Enumerate()
    {
      foreach (string absolutePath in Directory.EnumerateDirectories(location.GetAbsolute()))
      {
        string id = Path.GetFileName(absolutePath);
        GameBundle bundle = GetBundle(id);
        // Assume that if a scene is there, that's all we really care about.
        // That's the most important data, after all.
        if (!File.Exists(bundle.GetVoosPath()))
        {
          Debug.LogWarning($"A directory '{id}' inside '{location.GetAbsolute()}' seems like an invalid bundle directory. File is missing: {bundle.GetVoosPath()}. Skipping it.");
          continue;
        }

        yield return new Entry { id = id, bundle = bundle };
      }
    }

    public bool BundleExists(string bundleId)
    {
      if (bundleId.IsNullOrEmpty())
      {
        throw new System.Exception("No bundleId given");
      }
      return Directory.Exists(GetBundleDirectory(bundleId));
    }

    public void DeletePermanently(string bundleId)
    {
      if (bundleId.IsNullOrEmpty())
      {
        throw new System.Exception("No bundleId given");
      }

      string dir = GetBundleDirectory(bundleId);

      // Be extra careful...
      if (!dir.Contains(bundleId))
      {
        throw new System.Exception($"Suspicious bundle directory - not going to delete anything: {dir}");
      }

      if (!Directory.Exists(dir))
      {
        InvalidateBundleThumbnail(bundleId);
        return;
      }

      // Protect against random read-only...
      Util.SetNormalFileAttributes(dir);
      foreach (string file in Directory.GetFiles(dir))
      {
        Util.SetNormalFileAttributes(file);
      }
      InvalidateBundleThumbnail(bundleId);
      Directory.Delete(dir, true);
    }

    public void CopyBundle(string srcId, string destId)
    {
      string srcDir = GetBundleDirectory(srcId);
      string destDir = GetBundleDirectory(destId);

      if (!Directory.Exists(srcDir))
      {
        throw new System.Exception($"Source directory {srcDir} DNE");
      }

      if (!Directory.Exists(destDir))
      {
        Directory.CreateDirectory(destDir);
      }

      Util.CopyDirectoryRecursive(srcDir, destDir);
    }

    static string GenerateBundleId()
    {
      return System.Guid.NewGuid().ToString("N");
    }
  }
}