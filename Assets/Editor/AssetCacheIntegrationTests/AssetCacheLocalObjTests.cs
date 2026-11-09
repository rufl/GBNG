using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class AssetCacheLocalObjTests
{
  static readonly Type AssetCacheType = Type.GetType("AssetCache, Assembly-CSharp", true);
  static readonly Type ProcessAssetType = AssetCacheType.GetNestedType("ProcessAsset", BindingFlags.Public);
  static readonly Type ValueType = AssetCacheType.GetNestedType("Value", BindingFlags.Public);
  static readonly Type BuiltinPrefabLibraryType = Type.GetType("BuiltinPrefabLibrary, Assembly-CSharp", true);
  static readonly Type WorkshopAssetSourceType = Type.GetType("WorkshopAssetSource, Assembly-CSharp", true);
  static readonly MethodInfo GetMethod = AssetCacheType.GetMethod(
    "Get", new[] { typeof(string), ProcessAssetType });

  [UnityTest]
  public IEnumerator LocalObjGetIsDeferredCoalescedAndCached()
  {
    string directory = Path.Combine(Path.GetTempPath(), "GameBuilderAssetCache-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string objPath = Path.Combine(directory, "triangle.obj");
    GameObject dependencies = null;
    GameObject cacheObject = null;
    Mesh importedColliderMesh = null;
    GameObject clone = null;

    try
    {
      File.WriteAllText(objPath,
        "o triangle\n" +
        "v 0 0 0\n" +
        "v 1 0 0\n" +
        "v 0 1 0\n" +
        "f 1 2 3\n");

      Component cache = CreateTestCache(out cacheObject, out dependencies);

      string uri = "localfbx:" + objPath;
      var firstRequest = new List<object>();
      var secondRequest = new List<object>();
      InvokeGet(cache, uri, CreateCallback(firstRequest));
      InvokeGet(cache, uri, CreateCallback(secondRequest));

      Assert.AreEqual(0, firstRequest.Count, "AssetCache.Get must not invoke callbacks inline.");
      Assert.AreEqual(0, secondRequest.Count);

      for (int frame = 0; frame < 120 && firstRequest.Count == 0; frame++)
      {
        yield return null;
      }

      Assert.AreEqual(1, firstRequest.Count);
      Assert.AreEqual(1, secondRequest.Count);
      Assert.AreSame(firstRequest[0], secondRequest[0], "Concurrent requests should share one cache entry.");
      Assert.AreEqual(0, (int)AssetCacheType.GetMethod("GetNumActiveDownloads").Invoke(cache, null));

      clone = (GameObject)ValueType.GetMethod("GetAssetClone").Invoke(firstRequest[0], null);
      MeshCollider collider = clone.GetComponent<MeshCollider>();
      Assert.IsNotNull(collider);
      Assert.IsNotNull(collider.sharedMesh, "Imported local models must keep their generated collision mesh.");
      Assert.AreEqual(3, collider.sharedMesh.triangles.Length);
      importedColliderMesh = collider.sharedMesh;

      var cachedRequest = new List<object>();
      InvokeGet(cache, uri, CreateCallback(cachedRequest));
      Assert.AreEqual(0, cachedRequest.Count, "Cache hits must preserve asynchronous callback delivery.");
      yield return null;
      Assert.AreEqual(1, cachedRequest.Count);
      Assert.AreEqual(0, (int)AssetCacheType.GetMethod("GetNumActiveDownloads").Invoke(cache, null));
    }
    finally
    {
      if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
      if (cacheObject != null) UnityEngine.Object.DestroyImmediate(cacheObject);
      if (dependencies != null) UnityEngine.Object.DestroyImmediate(dependencies);
      if (importedColliderMesh != null) UnityEngine.Object.DestroyImmediate(importedColliderMesh);
      if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
  }

  [UnityTest]
  public IEnumerator LocalFbxGetUsesAssimpAndCreatesCollider()
  {
    string fbxPath = Path.GetFullPath(Path.Combine(
      Application.dataPath, "GameAssets/Models/NPCs/Fox/FoxNPC_RunWalkLEFT.fbx"));
    Assert.IsTrue(File.Exists(fbxPath), "Fox FBX fixture not found: " + fbxPath);

    GameObject dependencies = null;
    GameObject cacheObject = null;
    GameObject clone = null;
    Mesh importedColliderMesh = null;

    try
    {
      Component cache = CreateTestCache(out cacheObject, out dependencies);
      string uri = "localfbx:" + fbxPath;
      var result = new List<object>();
      InvokeGet(cache, uri, CreateCallback(result));

      Assert.AreEqual(0, result.Count, "AssetCache.Get must not invoke callbacks inline.");
      for (int frame = 0; frame < 240 && result.Count == 0; frame++)
      {
        yield return null;
      }

      Assert.AreEqual(1, result.Count, "Assimp import did not complete through AssetCache.Get.");
      Assert.AreEqual(0, (int)AssetCacheType.GetMethod("GetNumActiveDownloads").Invoke(cache, null));

      clone = (GameObject)ValueType.GetMethod("GetAssetClone").Invoke(result[0], null);
      SkinnedMeshRenderer[] skinnedMeshes = clone.GetComponentsInChildren<SkinnedMeshRenderer>();
      Assert.IsNotEmpty(skinnedMeshes, "The Fox FBX should preserve its skinned mesh.");
      Assert.Greater(skinnedMeshes[0].sharedMesh.vertexCount, 0);
      var loadedTextures = new HashSet<string>();
      foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
      {
        Material material = renderer.sharedMaterial;
        if (material != null && material.mainTexture != null)
        {
          loadedTextures.Add(material.mainTexture.name);
        }
      }
      CollectionAssert.Contains(loadedTextures, "Fox_TEX.png");
      CollectionAssert.Contains(loadedTextures, "Fox_Eyes_TEX.png");

      MeshCollider collider = clone.GetComponent<MeshCollider>();
      Assert.IsNotNull(collider);
      Assert.IsNotNull(collider.sharedMesh, "The imported model should have collision geometry.");
      Assert.Greater(collider.sharedMesh.triangles.Length, 3);
      importedColliderMesh = collider.sharedMesh;
    }
    finally
    {
      if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
      if (cacheObject != null) UnityEngine.Object.DestroyImmediate(cacheObject);
      if (dependencies != null) UnityEngine.Object.DestroyImmediate(dependencies);
      if (importedColliderMesh != null) UnityEngine.Object.DestroyImmediate(importedColliderMesh);
    }
  }

  static Component CreateTestCache(out GameObject cacheObject, out GameObject dependencies)
  {
    dependencies = new GameObject("AssetCache test dependencies");
    dependencies.SetActive(false);
    Component builtinPrefabLibrary = dependencies.AddComponent(BuiltinPrefabLibraryType);
    Component workshopAssetSource = dependencies.AddComponent(WorkshopAssetSourceType);

    cacheObject = new GameObject("AssetCache test");
    cacheObject.SetActive(false);
    Component cache = cacheObject.AddComponent(AssetCacheType);
    SetDependency(cache, "builtinPrefabLibrary", builtinPrefabLibrary);
    SetDependency(cache, "steamWorkshop", workshopAssetSource);
    cacheObject.SetActive(true);
    return cache;
  }

  static void SetDependency(Component cache, string fieldName, Component dependency)
  {
    FieldInfo field = AssetCacheType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(field, "AssetCache dependency field was renamed: " + fieldName);
    field.SetValue(cache, dependency);
  }

  static Delegate CreateCallback(List<object> results)
  {
    Type entryType = ProcessAssetType.GetMethod("Invoke").GetParameters()[0].ParameterType;
    ParameterExpression entry = Expression.Parameter(entryType, "entry");
    MethodInfo add = typeof(List<object>).GetMethod("Add");
    Expression body = Expression.Call(Expression.Constant(results), add, Expression.Convert(entry, typeof(object)));
    return Expression.Lambda(ProcessAssetType, body, entry).Compile();
  }

  static void InvokeGet(Component cache, string uri, Delegate callback)
  {
    GetMethod.Invoke(cache, new object[] { uri, callback });
  }
}
