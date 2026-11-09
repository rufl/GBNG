#if UNITY_EDITOR
using System;
using GameBuilder;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class TerrainNativeResourceLifecycleTests
{
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [Test]
  public void ReconfiguringTerrainReleasesGeneratedChunkMeshes()
  {
    GameObject terrainObject = new GameObject("Terrain mesh owner test");
    terrainObject.SetActive(false);
    TerrainSystem terrain = terrainObject.AddComponent<TerrainSystem>();
    Mesh renderMesh = null;
    Mesh physicsMesh = null;
    MeshFilter filter = null;
    MeshCollider collider = null;
    bool dimensionsInitialized = false;
    try
    {
      terrain.SetWorldDimensions(new Int3(1, 1, 1));
      dimensionsInitialized = true;
      Invoke(terrain, "UpdateUnityMesh", Int3.zero());

      Type chunkType = typeof(TerrainSystem).GetNestedType("Chunk", BindingFlags.NonPublic);
      Assert.IsNotNull(chunkType);
      object chunk = ((Array)typeof(TerrainSystem)
        .GetField("chunks", PrivateInstance).GetValue(terrain)).GetValue(0, 0, 0);
      renderMesh = (Mesh)chunkType.GetField("mesh").GetValue(chunk);
      physicsMesh = (Mesh)chunkType.GetField("phys_mesh").GetValue(chunk);
      GameObject chunkObject = (GameObject)chunkType.GetField("obj").GetValue(chunk);
      filter = chunkObject.GetComponent<MeshFilter>();
      collider = chunkObject.GetComponent<MeshCollider>();
      Assert.IsNotNull(renderMesh);
      Assert.IsNotNull(physicsMesh);

      terrain.SetWorldDimensions(new Int3(2, 1, 1));

      Assert.IsTrue(renderMesh == null, "Reconfiguring dimensions must destroy the old render mesh.");
      Assert.IsTrue(physicsMesh == null, "Reconfiguring dimensions must destroy the old collider mesh.");
      if (filter != null) Assert.IsNull(filter.sharedMesh);
      if (collider != null) Assert.IsNull(collider.sharedMesh);
    }
    finally
    {
      if (terrain != null)
      {
        Invoke(terrain, "ReleaseOwnedUnityResources");
        if (dimensionsInitialized) Invoke(terrain, "DisposeNativeArrays");
      }
      if (terrainObject != null) UnityEngine.Object.DestroyImmediate(terrainObject);
      if (renderMesh != null) UnityEngine.Object.DestroyImmediate(renderMesh);
      if (physicsMesh != null) UnityEngine.Object.DestroyImmediate(physicsMesh);
    }
  }

  [Test]
  public void TerrainReleasesRebuiltArraysAndOwnedMaterialWithoutClearingOtherGlobals()
  {
    Texture previousMain = Shader.GetGlobalTexture("_tex_array");
    Texture previousBorder = Shader.GetGlobalTexture("_border_tex_array");
    Texture2DArray foreignArray = null;
    Material sourceMaterial = null;
    GameObject terrainObject = new GameObject("Terrain texture owner test");
    terrainObject.SetActive(false);
    TerrainSystem terrain = terrainObject.AddComponent<TerrainSystem>();
    FieldInfo mainArrayField = typeof(TerrainSystem).GetField("tex_array", PrivateInstance);
    FieldInfo borderArrayField = typeof(TerrainSystem).GetField("border_tex_array", PrivateInstance);
    FieldInfo ownedMaterialField = typeof(TerrainSystem).GetField("ownedMaterial", PrivateInstance);
    Texture2DArray firstMainArray = null;
    Texture2DArray currentMainArray = null;
    Texture2DArray borderArray = null;
    Material ownedMaterial = null;

    try
    {
      Shader shader = Shader.Find("Standard");
      Assert.IsNotNull(shader);
      sourceMaterial = new Material(shader);
      terrain.material = sourceMaterial;
      terrain.CreateTextureArrays(false, 2);
      firstMainArray = (Texture2DArray)mainArrayField.GetValue(terrain);

      terrain.CreateTextureArrays(true, 4);
      currentMainArray = (Texture2DArray)mainArrayField.GetValue(terrain);
      borderArray = (Texture2DArray)borderArrayField.GetValue(terrain);
      Assert.IsTrue(firstMainArray == null, "Forcing a rebuild must release the replaced main texture array.");
      Assert.AreEqual(4, currentMainArray.width);

      ownedMaterial = new Material(sourceMaterial);
      terrain.material = ownedMaterial;
      ownedMaterialField.SetValue(terrain, ownedMaterial);
      foreignArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
      Shader.SetGlobalTexture("_tex_array", foreignArray);
      Shader.SetGlobalTexture("_border_tex_array", borderArray);

      Invoke(terrain, "ReleaseOwnedUnityResources");

      Assert.IsTrue(currentMainArray == null, "Terrain teardown must release its current main texture array.");
      Assert.IsTrue(borderArray == null, "Terrain teardown must release its border texture array.");
      Assert.AreSame(foreignArray, Shader.GetGlobalTexture("_tex_array"),
        "A terrain instance must not clear a global texture installed by another owner.");
      Assert.IsTrue(ownedMaterial == null, "Terrain teardown must release its material clone.");
      Assert.IsNotNull(sourceMaterial, "The serialized source material remains caller-owned.");
    }
    finally
    {
      if (terrain != null) Invoke(terrain, "ReleaseOwnedUnityResources");
      if (terrainObject != null) UnityEngine.Object.DestroyImmediate(terrainObject);
      Shader.SetGlobalTexture("_tex_array", previousMain);
      Shader.SetGlobalTexture("_border_tex_array", previousBorder);
      if (foreignArray != null) UnityEngine.Object.DestroyImmediate(foreignArray);
      if (firstMainArray != null) UnityEngine.Object.DestroyImmediate(firstMainArray);
      if (currentMainArray != null) UnityEngine.Object.DestroyImmediate(currentMainArray);
      if (borderArray != null) UnityEngine.Object.DestroyImmediate(borderArray);
      if (ownedMaterial != null) UnityEngine.Object.DestroyImmediate(ownedMaterial);
      if (sourceMaterial != null) UnityEngine.Object.DestroyImmediate(sourceMaterial);
    }
  }

  static object Invoke(object target, string methodName, params object[] arguments)
  {
    MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
    Assert.IsNotNull(method, "Missing lifecycle method " + methodName);
    return method.Invoke(target, arguments);
  }
}
#endif
