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
using System.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System;

// Asset cache for built-in, local, image, and legacy online assets.
public class AssetCache : MonoBehaviour
{
  public delegate void ProcessAsset(Value entry);

  public GameObject imageRenderablePrefab;
  public Texture2D playerThumbnail;

  private Dictionary<string, ValueImpl> cacheByUri = new Dictionary<string, ValueImpl>();

  private Dictionary<string, List<ProcessAsset>> activeDownloadWaiters = new Dictionary<string, List<ProcessAsset>>();


  private Transform cacheRoot;

  private List<string> downloadedImageUrls = new List<string>();

  BuiltinPrefabLibrary builtinPrefabLibrary;
  WorkshopAssetSource steamWorkshop;

  public int GetNumActiveDownloads()
  {
    return activeDownloadWaiters.Count;
  }

  public void Get(string uri, ProcessAsset process)
  {
    Debug.Assert(uri != null);
    Debug.Assert(uri.Length > 0);
    Get(VoosAssetUtil.AssetFromUri(uri), process);
  }

  public void Get(VoosAsset asset, ProcessAsset process)
  {
    string uri = asset.GetUri();

    if (cacheByUri.ContainsKey(uri))
    {
      // We could call process immediately, but that makes this inconsistent.
      // We should maintain the invariant that process is ALWAYS called asynchronously.
      StartCoroutine(DeferredProcess(asset, process));
    }
    else
    {
      if (activeDownloadWaiters.ContainsKey(uri))
      {
        // Someone else already requested this, so just add ourselves to the wait list.
        activeDownloadWaiters[uri].Add(process);
      }
      else
      {
        // Start a new wait list.
        List<ProcessAsset> waitList = new List<ProcessAsset>();
        waitList.Add(process);
        activeDownloadWaiters[uri] = waitList;

        // Kick off the download.
        asset.Accept(new DownloadVisitor(this));
      }
    }
  }

  private void Awake()
  {
    Util.FindIfNotSet(this, ref builtinPrefabLibrary);
    Util.FindIfNotSet(this, ref steamWorkshop);

    cacheRoot = new GameObject("CachedAssets").transform;
    cacheRoot.parent = transform;
  }

  public class DownloadVisitor : VoosAssetVisitor
  {
    AssetCache cache;

    public DownloadVisitor(AssetCache cache)
    {
      this.cache = cache;
    }

#if USE_TRILIB
    static TriLib.AssetLoaderOptions triLibOpts = null;

    static TriLib.AssetLoaderOptions GetTriLibOpts()
    {
      if (triLibOpts == null)
      {
        triLibOpts = TriLib.AssetLoaderOptions.CreateInstance();
      }
      var opts = triLibOpts;
      opts.DontLoadCameras = true;
      opts.DontLoadLights = true;
      opts.UseLegacyAnimations = true;
      opts.GenerateMeshColliders = false; // We'll do this ourselves later.

      // Hmm support for alpha isn't great..the robot comes in clear if we do
      // this. Deal with it later, possibly manually.
      opts.DisableAlphaMaterials = true;

      return opts;
    }
#endif

    public void Visit(PolyVoosAsset asset)
    {
      cache.StartCoroutine(CacheMissingAsset(
        asset.GetUri(),
        $"Cannot load legacy Poly asset '{asset.assetId}': Poly is retired. Replace it with a local OBJ model."));
    }
    private static void PrepareImportedModel(GameObject assetObject)
    {
      FitImportedModel(assetObject);

      // Add colliders - important to do this after fitting.
      MeshFilter[] meshFilters = assetObject.GetComponentsInChildren<MeshFilter>();
      SkinnedMeshRenderer[] skinned = assetObject.GetComponentsInChildren<SkinnedMeshRenderer>();
      CombineInstance[] combine = new CombineInstance[meshFilters.Length + skinned.Length];
      int i = 0;
      while (i < meshFilters.Length)
      {
        combine[i].mesh = meshFilters[i].sharedMesh;
        combine[i].transform = assetObject.transform.worldToLocalMatrix * meshFilters[i].transform.localToWorldMatrix;
        i++;
      }
      foreach (var s in skinned)
      {
        combine[i].mesh = s.sharedMesh;
        combine[i].transform = assetObject.transform.worldToLocalMatrix * s.transform.localToWorldMatrix;
        i++;
      }
      MeshFilter mf = assetObject.AddComponent<MeshFilter>();
      mf.mesh = new Mesh();
      mf.mesh.CombineMeshes(combine);
      MeshCollider mc = assetObject.AddComponent<MeshCollider>();
      mc.convex = true;
      Destroy(mf);

      // Give the materials textures if they don't have them.
      MeshRenderer[] meshRenderers = assetObject.GetComponentsInChildren<MeshRenderer>();
      for (i = 0; i < meshRenderers.Length; i++)
      {
        MeshRenderer meshRenderer = meshRenderers[i];
        BakeBaseColorIntoTexture(meshRenderer.material);
      }

      // Imported local models use the default texture filtering mode.

      // Also enable GPU instancing!
      // foreach (var r in meshRenderers)
      // {
      //   foreach (var mat in r.sharedMaterials)
      //   {
      //     mat.enableInstancing = true;
      //   }
      // }
    }

    /** 
     * Color the texture with the material's base color and set the base color to white
     * so that we can apply further tints to the base color as needed.
     */
    static void BakeBaseColorIntoTexture(Material mat)
    {
      int baseColorFactorId = Shader.PropertyToID("_BaseColorFactor");
      if (!mat.HasProperty(baseColorFactorId))
      {
        return;
      }

      Color baseColor = mat.GetColor(baseColorFactorId);
      if (Color.white.Equals(baseColor))
      {
        return;
      }

      // If it doesn't have a texture, give it a default white one.
      Texture2D texture = (Texture2D)mat.GetTexture(Shader.PropertyToID("_BaseColorTex"));
      if (!texture)
      {
        texture = new Texture2D(1, 1, TextureFormat.RGBA32, true, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
      }

      Color[] pixels = texture.GetPixels();
      for (int i = 0; i < pixels.Length; i++)
      {
        pixels[i] = (pixels[i].linear * baseColor).gamma;
      }
      texture.SetPixels(pixels);
      texture.Apply();

      mat.SetTexture(Shader.PropertyToID("_BaseColorTex"), texture);
      mat.SetColor(baseColorFactorId, Color.white);
    }

    public static void FitImportedModel(GameObject importedObject)
    {
      Bounds origBounds = Util.ComputeWorldRenderBounds(importedObject);
      float maxExtent = origBounds.size.MaxAbsComponent();
      float scale = maxExtent > Mathf.Epsilon ? 1f / maxExtent : 1f;
      float yOffset = origBounds.size.y / 2f * scale;

      bool hadChild = false;
      foreach (Transform child in importedObject.transform)
      {
        child.localScale *= scale;
        child.localPosition *= scale;
        child.localPosition -= origBounds.center * scale;
        child.localPosition += yOffset * Vector3.up;
        hadChild = true;
      }

      Debug.Assert(hadChild, "Imported model had no children - not expected.");
    }

    private IEnumerator CacheMissingAsset(string uri, string reason)
    {
      yield return null;
      Util.LogError(reason);
      cache.CreateCacheEntry(uri, CreateMissingAssetPlaceholder(), null);
    }
    public void Visit(ImageVoosAsset asset)
    {
      cache.StartCoroutine(LoadAndCacheImage(asset));
    }

    public void Visit(BuiltinVoosAsset asset)
    {
      cache.StartCoroutine(LoadAndCacheBuiltin(asset));
    }

    public void Visit(LocalFbxAsset asset)
    {
      string extension = Path.GetExtension(asset.absoluteFilePath);
      if (string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase))
      {
        cache.StartCoroutine(LoadLocalObjAsset(asset));
        return;
      }

#if USE_TRILIB
      LoadLocalFbx(asset.absoluteFilePath, gameObject =>
      {
        cache.CreateCacheEntry(asset.GetUri(), gameObject, null);

        var watcher = new FileSystemWatcher();
        watcher.Path = Path.GetDirectoryName(asset.absoluteFilePath);
        watcher.Filter = Path.GetFileName(asset.absoluteFilePath);
        watcher.NotifyFilter = NotifyFilters.LastWrite;
        watcher.EnableRaisingEvents = true;
        watcher.Changed += (_, e) =>
        {
          LoadLocalFbx(asset.absoluteFilePath, newGameObject =>
          {
            cache.UpdateCacheEntry(asset.GetUri(), newGameObject);
          });
        };
      });
#else
      cache.StartCoroutine(CacheMissingAsset(
        asset.GetUri(),
        $"This build supports local OBJ models, not '{extension}' files (local path: {asset.absoluteFilePath})."));
#endif
    }

    private IEnumerator LoadLocalObjAsset(LocalFbxAsset asset)
    {
      yield return null;
      GameObject imported = null;
      try
      {
        imported = ObjModelLoader.Load(asset.absoluteFilePath);
        PrepareImportedModel(imported);
      }
      catch (Exception e)
      {
        Util.LogError($"Could not load local OBJ '{asset.absoluteFilePath}': {e.Message}");
        if (imported != null) Destroy(imported);
        imported = CreateMissingAssetPlaceholder();
      }
      cache.CreateCacheEntry(asset.GetUri(), imported, null);
    }

    IEnumerator LoadAndCacheImage(ImageVoosAsset asset)
    {
      WWW request = new WWW(asset.url);
      yield return request;
      Debug.Assert(request.isDone);
      GameObject renderable = Instantiate(cache.imageRenderablePrefab);
      renderable.GetComponentInChildren<Renderer>().material.mainTexture = request.texture;

      string uri = asset.GetUri();
      cache.CreateCacheEntry(uri, renderable, request.texture);

      cache.downloadedImageUrls.Add(asset.url);
    }

    IEnumerator LoadAndCacheBuiltin(BuiltinVoosAsset asset)
    {
      ResourceRequest request = Resources.LoadAsync<GameObject>(asset.resourcePath);
      yield return request;
      Debug.Assert(request.isDone);
      Debug.Assert(request.asset != null, $"Failed to load builtin asset resource {asset.resourcePath}");
      GameObject loadedAsset = GameObject.Instantiate((GameObject)request.asset);

      string uri = asset.GetUri();
      Texture2D thumbnail;
      const string BUILTIN_ASSETS_PREFIX = "builtin:BuiltinAssets/";
      // We should find a less hacky way to do this.
      if (asset.GetUri() == VoosActor.AVATAR_EXPLORER_URI)
      {
        thumbnail = cache.playerThumbnail;
      }
      else if (uri.ToLowerInvariant().StartsWith(BUILTIN_ASSETS_PREFIX.ToLowerInvariant()))
      {
        // It's a built-in asset.
        string builtinId = uri.Substring(BUILTIN_ASSETS_PREFIX.Length);
        if (cache.builtinPrefabLibrary.PrefabExists(builtinId))
        {
          // Corresponds to an existing prefab, so use the prefab's thumbnail.
          thumbnail = cache.builtinPrefabLibrary.Get(builtinId).GetThumbnail();
        }
        else
        {
          // Some built-in assets don't correspond to prefabs. In that case don't use a thumbnail.
          // Warn anyway...
          Debug.LogWarning($"No thumbnail for built-in asset: {uri}. It does not correspond to an actor prefab.");
          thumbnail = null;
        }
      }
      else
      {
        Debug.LogError("No idea where to get thumbnail for " + uri);
        thumbnail = null;
      }
      cache.CreateCacheEntry(uri, loadedAsset, thumbnail);
    }

#if USE_TRILIB
    static void LoadLocalFbx(string path, System.Action<GameObject> process)
    {
      var loader = new TriLib.AssetLoaderAsync();
      TriLib.AssetLoaderOptions opts = GetTriLibOpts();
      loader.LoadFromFileWithTextures(path, opts, null, gameObject =>
      {
        PrepareImportedModel(gameObject);
        process(gameObject);
      });
    }
#endif

    public void Visit(SteamWorkshopAsset asset)
    {
#if USE_TRILIB
      cache.steamWorkshop.Get(asset.publishedId, maybePath =>
      {
        if (maybePath.IsEmpty())
        {
          Util.LogError($"Error trying to download workshop item {asset.publishedId}: {maybePath.GetErrorMessage()}");
          cache.CreateCacheEntry(asset.GetUri(), CreateMissingAssetPlaceholder(), null);
          return;
        }

        foreach (string file in Directory.EnumerateFiles(maybePath.Value))
        {
          if (Path.GetExtension(file).ToLowerInvariant() == ".fbx")
          {
            Util.Log($"OK loading FBX downloaded from workshop at {file}");
            LoadLocalFbx(file, imported => cache.CreateCacheEntry(asset.GetUri(), imported, null));
            return;
          }
        }
        cache.CreateCacheEntry(asset.GetUri(), CreateMissingAssetPlaceholder(), null);
      });
#else
      cache.StartCoroutine(CacheMissingAsset(
        asset.GetUri(),
        $"This build does not support loading FBX assets (for workshop ID {asset.publishedId})."));
#endif
    }
    private static Material missingAssetMaterial;

    private static GameObject CreateMissingAssetPlaceholder()
    {
      var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
      placeholder.name = "Missing asset";
      if (missingAssetMaterial == null)
      {
        Shader shader = Shader.Find("Standard");
        if (shader != null)
        {
          missingAssetMaterial = new Material(shader) { name = "Missing asset material", hideFlags = HideFlags.HideAndDontSave };
          missingAssetMaterial.color = Color.magenta;
        }
      }
      MeshRenderer renderer = placeholder.GetComponent<MeshRenderer>();
      if (renderer != null && missingAssetMaterial != null) renderer.sharedMaterial = missingAssetMaterial;
      return placeholder;
    }
  }

  IEnumerator DeferredProcess(VoosAsset asset, ProcessAsset process)
  {
    yield return null;
    process(cacheByUri[asset.GetUri()]);
  }

  // Returns true if it is an error and it was logged.
  void PrepareCachedAsset(string uri, GameObject assetObject)
  {
    assetObject.name = uri;
    assetObject.SetActive(false);
    assetObject.transform.parent = cacheRoot;
  }

  Value CreateCacheEntry(string uri, GameObject assetObject, Texture2D thumbnail)
  {
    PrepareCachedAsset(uri, assetObject);
    ValueImpl entry = new ValueImpl(assetObject, thumbnail);

    if (cacheByUri.ContainsKey(uri))
    {
      Debug.LogError($"AssetCache is overriding uri {uri}. Is this a redundant download?");
    }
    cacheByUri[uri] = entry;

    // Flush the waitlist
    if (activeDownloadWaiters.ContainsKey(uri))
    {
      foreach (ProcessAsset process in activeDownloadWaiters[uri])
      {
        process(entry);
      }
      activeDownloadWaiters.Remove(uri);
    }

    return entry;
  }

  void UpdateCacheEntry(string uri, GameObject newAssetObject)
  {
    PrepareCachedAsset(uri, newAssetObject);
    ValueImpl cached = cacheByUri[uri];
    cached.UpdateAssetObject(newAssetObject);
  }

  public bool AnyAttributionsToShow()
  {
    return downloadedImageUrls.Count > 0;
  }

  public string GetAttributionsString()
  {
    return downloadedImageUrls.Count > 0 ? GenerateImageAttributions() : "";
  }

  private string GenerateImageAttributions()
  {
    return $"URLs of images used:\n" + String.Join("\n", downloadedImageUrls);
  }

  public interface Value
  {
    // NOTE: The asset gameobject given to you will be IN-ACTIVE. If we were to
    // activate it for you, that could incur a lot of unnecessary costs, so just
    // activate it yourself once it's set up. For example, if you're going to
    // parent it under something else, do that first, then activate it,
    // otherwise you incur extra mesh collider costs.
    GameObject GetAssetClone();

    // Triggered whenever the underlying asset has changed. This is mainly for
    // local file system assets, which the user may change for rapid iteration.
    event System.Action onAssetChanged;

    Texture2D GetThumbnail();
  }

  private class ValueImpl : Value
  {
    GameObject assetObject;
    Texture2D thumbnail;

    public event System.Action onAssetChanged;

    public ValueImpl(GameObject assetObject, Texture2D thumbnail)
    {
      this.assetObject = assetObject;
      this.thumbnail = thumbnail;
    }

    public void UpdateAssetObject(GameObject newAssetObject)
    {
      GameObject.Destroy(this.assetObject);
      this.assetObject = newAssetObject;
      this.onAssetChanged?.Invoke();
    }

    public GameObject GetAssetClone()
    {
      return GameObject.Instantiate(assetObject);
    }

    public Texture2D GetThumbnail()
    {
      return thumbnail;
    }
  }
}
