/*
 * Copyright 2026.
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

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using UnityEngine;
using UnityEngine.Rendering;
using AssimpAnimation = Assimp.Animation;
using AssimpMaterial = Assimp.Material;
using AssimpMesh = Assimp.Mesh;
using AssimpNode = Assimp.Node;
using AssimpScene = Assimp.Scene;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;
using Quaternion = UnityEngine.Quaternion;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;
using UnityAnimation = UnityEngine.Animation;

internal static class AssimpModelLoader
{
  private sealed class PendingSkin
  {
    public AssimpMesh source;
    public Mesh mesh;
    public SkinnedMeshRenderer renderer;
  }

  public static GameObject Load(string path)
  {
    if (!File.Exists(path))
    {
      throw new FileNotFoundException("Model file was not found.", path);
    }

    using (var importer = new AssimpContext())
    {
      AssimpScene scene = importer.ImportFile(path);
      if (scene == null || scene.RootNode == null)
      {
        throw new InvalidDataException("Assimp did not return a model scene.");
      }

      return CreateUnityHierarchy(scene, Path.GetDirectoryName(path));
    }
  }

  private static GameObject CreateUnityHierarchy(AssimpScene scene, string modelDirectory)
  {
    var materials = new List<Material>(scene.Materials.Count);
    foreach (AssimpMaterial source in scene.Materials)
    {
      materials.Add(CreateMaterial(source, modelDirectory));
    }
    if (materials.Count == 0)
    {
      materials.Add(CreateMaterial(null, modelDirectory));
    }

    var meshes = new List<Mesh>(scene.Meshes.Count);
    foreach (AssimpMesh source in scene.Meshes)
    {
      meshes.Add(CreateMesh(source));
    }

    var nodesByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
    var pathsByName = new Dictionary<string, string>(StringComparer.Ordinal);
    var pendingSkins = new List<PendingSkin>();
    GameObject root = null;
    try
    {
      root = CreateNode(
        scene.RootNode,
        scene,
        meshes,
        materials,
        nodesByName,
        pathsByName,
        pendingSkins,
        string.Empty,
        true);
      ConfigureSkins(root, nodesByName, pendingSkins);
      ConfigureAnimations(root, scene, pathsByName);
      return root;
    }
    catch
    {
      if (root != null)
      {
        UnityEngine.Object.Destroy(root);
      }
      throw;
    }
  }

  private static Material CreateMaterial(AssimpMaterial source, string modelDirectory)
  {
    Shader shader = Shader.Find("Standard");
    if (shader == null)
    {
      throw new InvalidOperationException("The Unity Standard shader is unavailable.");
    }

    var material = new Material(shader);
    if (source == null)
    {
      return material;
    }

    if (source.HasColorDiffuse)
    {
      material.color = new Color(
        source.ColorDiffuse.R,
        source.ColorDiffuse.G,
        source.ColorDiffuse.B,
        source.ColorDiffuse.A);
    }

    if (source.HasColorEmissive)
    {
      var emissive = new Color(
        source.ColorEmissive.R,
        source.ColorEmissive.G,
        source.ColorEmissive.B,
        source.ColorEmissive.A);
      material.SetColor("_EmissionColor", emissive);
      material.EnableKeyword("_EMISSION");
    }

    if (source.HasReflectivity)
    {
      material.SetFloat("_Glossiness", source.Reflectivity);
    }

    if (source.HasTextureDiffuse)
    {
      string textureReference = source.TextureDiffuse.FilePath;
      string normalizedReference = textureReference.Replace('\\', Path.DirectorySeparatorChar);
      string texturePath = Path.IsPathRooted(normalizedReference)
        ? normalizedReference
        : Path.Combine(modelDirectory ?? string.Empty, normalizedReference);
      if (!File.Exists(texturePath))
      {
        // FBX files often retain an absolute Windows source path; use the local sibling texture on other OSes.
        string siblingTexturePath = Path.Combine(modelDirectory ?? string.Empty, Path.GetFileName(normalizedReference));
        if (File.Exists(siblingTexturePath)) texturePath = siblingTexturePath;
      }
      if (File.Exists(texturePath))
      {
        try
        {
          byte[] bytes = File.ReadAllBytes(texturePath);
          var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
          if (texture.LoadImage(bytes))
          {
            texture.name = Path.GetFileName(texturePath);
            material.SetTexture("_MainTex", texture);
          }
          else
          {
            UnityEngine.Object.Destroy(texture);
            Debug.LogWarning($"Could not decode diffuse texture '{texturePath}'. Using the material color.");
          }
        }
        catch (Exception exception)
        {
          Debug.LogWarning($"Could not read diffuse texture '{texturePath}': {exception.Message}. Using the material color.");
        }
      }
      else
      {
        Debug.LogWarning($"Diffuse texture '{texturePath}' was not found. Using the material color.");
      }
    }

    return material;
  }

  private static Mesh CreateMesh(AssimpMesh source)
  {
    var mesh = new Mesh { name = source.Name };
    if (source.VertexCount > ushort.MaxValue)
    {
      mesh.indexFormat = IndexFormat.UInt32;
    }

    var vertices = new Vector3[source.VertexCount];
    for (int i = 0; i < source.VertexCount; i++)
    {
      Assimp.Vector3D vertex = source.Vertices[i];
      vertices[i] = new Vector3(-vertex.X, vertex.Y, vertex.Z);
    }
    mesh.vertices = vertices;

    bool hasNormals = false;
    if (source.HasNormals && source.Normals.Count == vertices.Length)
    {
      var normals = new Vector3[source.Normals.Count];
      for (int i = 0; i < normals.Length; i++)
      {
        Assimp.Vector3D normal = source.Normals[i];
        normals[i] = new Vector3(-normal.X, normal.Y, normal.Z);
      }
      mesh.normals = normals;
      hasNormals = true;
    }

    if (source.HasTextureCoords(0))
    {
      var uv = new Vector2[source.TextureCoordinateChannels[0].Count];
      for (int i = 0; i < uv.Length; i++)
      {
        Assimp.Vector3D coordinate = source.TextureCoordinateChannels[0][i];
        uv[i] = new Vector2(coordinate.X, coordinate.Y);
      }
      mesh.uv = uv;
    }

    var triangles = new List<int>();
    foreach (Assimp.Face face in source.Faces)
    {
      if (face.IndexCount < 3)
      {
        continue;
      }
      for (int i = 0; i < face.IndexCount - 2; i++)
      {
        triangles.Add(face.Indices[i + 2]);
        triangles.Add(face.Indices[i + 1]);
        triangles.Add(face.Indices[0]);
      }
    }
    mesh.SetTriangles(triangles, 0);
    if (!hasNormals)
    {
      mesh.RecalculateNormals();
    }
    mesh.RecalculateBounds();
    return mesh;
  }

  private static GameObject CreateNode(
    AssimpNode source,
    AssimpScene scene,
    List<Mesh> meshes,
    List<Material> materials,
    Dictionary<string, Transform> nodesByName,
    Dictionary<string, string> pathsByName,
    List<PendingSkin> pendingSkins,
    string parentPath,
    bool isRoot)
  {
    string nodeName = string.IsNullOrEmpty(source.Name) ? "Model node" : source.Name;
    var node = new GameObject(nodeName);
    string nodePath = isRoot ? string.Empty :
      (string.IsNullOrEmpty(parentPath) ? nodeName : parentPath + "/" + nodeName);
    if (!string.IsNullOrEmpty(source.Name))
    {
      if (!nodesByName.ContainsKey(source.Name))
      {
        nodesByName.Add(source.Name, node.transform);
        pathsByName.Add(source.Name, nodePath);
      }
      else
      {
        Debug.LogWarning($"Model contains duplicate node name '{source.Name}'; animation and skinning use the first one.");
      }
    }

    if (source.HasMeshes)
    {
      foreach (int meshIndex in source.MeshIndices)
      {
        AssimpMesh sourceMesh = scene.Meshes[meshIndex];
        var meshObject = new GameObject(string.IsNullOrEmpty(sourceMesh.Name) ? "Mesh" : sourceMesh.Name);
        meshObject.transform.SetParent(node.transform, false);
        Material material = sourceMesh.MaterialIndex >= 0 && sourceMesh.MaterialIndex < materials.Count
          ? materials[sourceMesh.MaterialIndex]
          : materials[0];

        if (sourceMesh.HasBones)
        {
          var renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
          renderer.sharedMesh = meshes[meshIndex];
          renderer.sharedMaterial = material;
          pendingSkins.Add(new PendingSkin
          {
            source = sourceMesh,
            mesh = meshes[meshIndex],
            renderer = renderer
          });
        }
        else
        {
          meshObject.AddComponent<MeshFilter>().sharedMesh = meshes[meshIndex];
          meshObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
      }
    }

    var scale = new Assimp.Vector3D();
    var rotation = new Assimp.Quaternion();
    var translation = new Assimp.Vector3D();
    source.Transform.Decompose(out scale, out rotation, out translation);
    node.transform.localScale = new Vector3(scale.X, scale.Y, scale.Z);
    node.transform.localPosition = new Vector3(translation.X, translation.Y, translation.Z);
    node.transform.localRotation = ConvertRotation(rotation);

    if (source.HasChildren)
    {
      foreach (AssimpNode child in source.Children)
      {
        CreateNode(
          child,
          scene,
          meshes,
          materials,
          nodesByName,
          pathsByName,
          pendingSkins,
          nodePath,
          false).transform.SetParent(node.transform, false);
      }
    }

    return node;
  }

  private static Quaternion ConvertRotation(Assimp.Quaternion source)
  {
    var rotation = new Quaternion(source.X, source.Y, source.Z, source.W);
    Vector3 euler = rotation.eulerAngles;
    return Quaternion.Euler(euler.x, -euler.y, euler.z);
  }

  private static void ConfigureSkins(
    GameObject root,
    Dictionary<string, Transform> nodesByName,
    List<PendingSkin> pendingSkins)
  {
    foreach (PendingSkin pending in pendingSkins)
    {
      var bones = new Transform[pending.source.Bones.Count];
      bool allBonesFound = bones.Length > 0;
      for (int i = 0; i < bones.Length; i++)
      {
        if (!nodesByName.TryGetValue(pending.source.Bones[i].Name, out bones[i]))
        {
          allBonesFound = false;
          break;
        }
      }

      if (!allBonesFound)
      {
        Debug.LogWarning($"Could not bind every bone for mesh '{pending.source.Name}'. Keeping its mesh static.");
        pending.renderer.sharedMesh = null;
        pending.renderer.enabled = false;
        pending.renderer.gameObject.AddComponent<MeshFilter>().sharedMesh = pending.mesh;
        pending.renderer.gameObject.AddComponent<MeshRenderer>().sharedMaterial = pending.renderer.sharedMaterial;
        continue;
      }

      var weights = new BoneWeight[pending.source.VertexCount];
      for (int boneIndex = 0; boneIndex < pending.source.Bones.Count; boneIndex++)
      {
        foreach (Assimp.VertexWeight influence in pending.source.Bones[boneIndex].VertexWeights)
        {
          int vertex = influence.VertexID;
          if (vertex >= 0 && vertex < weights.Length && influence.Weight > 0f)
          {
            AddInfluence(ref weights[vertex], boneIndex, influence.Weight);
          }
        }
      }

      for (int i = 0; i < weights.Length; i++)
      {
        BoneWeight weight = weights[i];
        float total = weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;
        if (total > 0f)
        {
          weight.weight0 /= total;
          weight.weight1 /= total;
          weight.weight2 /= total;
          weight.weight3 /= total;
        }
        else
        {
          weight.boneIndex0 = 0;
          weight.weight0 = 1f;
        }
        weights[i] = weight;
      }

      pending.mesh.boneWeights = weights;
      var bindposes = new UnityEngine.Matrix4x4[bones.Length];
      UnityEngine.Matrix4x4 meshLocalToWorld = pending.renderer.transform.localToWorldMatrix;
      for (int i = 0; i < bones.Length; i++)
      {
        bindposes[i] = bones[i].worldToLocalMatrix * meshLocalToWorld;
      }
      pending.mesh.bindposes = bindposes;
      pending.renderer.bones = bones;
      pending.renderer.rootBone = root.transform;
    }
  }

  private static void AddInfluence(ref BoneWeight weights, int boneIndex, float value)
  {
    if (value > weights.weight0)
    {
      weights.weight3 = weights.weight2;
      weights.boneIndex3 = weights.boneIndex2;
      weights.weight2 = weights.weight1;
      weights.boneIndex2 = weights.boneIndex1;
      weights.weight1 = weights.weight0;
      weights.boneIndex1 = weights.boneIndex0;
      weights.weight0 = value;
      weights.boneIndex0 = boneIndex;
    }
    else if (value > weights.weight1)
    {
      weights.weight3 = weights.weight2;
      weights.boneIndex3 = weights.boneIndex2;
      weights.weight2 = weights.weight1;
      weights.boneIndex2 = weights.boneIndex1;
      weights.weight1 = value;
      weights.boneIndex1 = boneIndex;
    }
    else if (value > weights.weight2)
    {
      weights.weight3 = weights.weight2;
      weights.boneIndex3 = weights.boneIndex2;
      weights.weight2 = value;
      weights.boneIndex2 = boneIndex;
    }
    else if (value > weights.weight3)
    {
      weights.weight3 = value;
      weights.boneIndex3 = boneIndex;
    }
  }

  private static void ConfigureAnimations(
    GameObject root,
    AssimpScene scene,
    Dictionary<string, string> pathsByName)
  {
    if (scene.Animations.Count == 0)
    {
      return;
    }

    UnityAnimation animation = root.AddComponent<UnityAnimation>();
    var clipNames = new HashSet<string>(StringComparer.Ordinal);
    string firstClipName = null;
    for (int animationIndex = 0; animationIndex < scene.Animations.Count; animationIndex++)
    {
      AssimpAnimation source = scene.Animations[animationIndex];
      string baseName = string.IsNullOrEmpty(source.Name) ? $"Animation_{animationIndex + 1}" : source.Name;
      string clipName = baseName;
      int suffix = 2;
      while (!clipNames.Add(clipName))
      {
        clipName = baseName + "_" + suffix++;
      }

      float ticksPerSecond = source.TicksPerSecond > 0d ? (float)source.TicksPerSecond : 25f;
      var clip = new AnimationClip { name = clipName, legacy = true, frameRate = 30f, wrapMode = WrapMode.Loop };
      bool hasCurves = false;
      foreach (NodeAnimationChannel channel in source.NodeAnimationChannels)
      {
        if (!pathsByName.TryGetValue(channel.NodeName, out string path))
        {
          Debug.LogWarning($"Animation '{clipName}' references unknown node '{channel.NodeName}'.");
          continue;
        }

        hasCurves |= SetVectorCurves(clip, path, "localPosition", channel.PositionKeys, ticksPerSecond);
        hasCurves |= SetVectorCurves(clip, path, "localScale", channel.ScalingKeys, ticksPerSecond);
        hasCurves |= SetRotationCurves(clip, path, channel.RotationKeys, ticksPerSecond);
      }

      if (!hasCurves)
      {
        UnityEngine.Object.Destroy(clip);
        continue;
      }

      animation.AddClip(clip, clipName);
      if (firstClipName == null)
      {
        firstClipName = clipName;
      }
    }

    if (firstClipName == null)
    {
      UnityEngine.Object.Destroy(animation);
      return;
    }

    animation.clip = animation[firstClipName].clip;
    animation.wrapMode = WrapMode.Loop;
    animation.Play(firstClipName);
  }

  private static bool SetVectorCurves(
    AnimationClip clip,
    string path,
    string property,
    IList<VectorKey> keys,
    float ticksPerSecond)
  {
    if (keys.Count == 0)
    {
      return false;
    }

    var x = new Keyframe[keys.Count];
    var y = new Keyframe[keys.Count];
    var z = new Keyframe[keys.Count];
    for (int i = 0; i < keys.Count; i++)
    {
      VectorKey key = keys[i];
      float time = (float)(key.Time / ticksPerSecond);
      x[i] = new Keyframe(time, key.Value.X);
      y[i] = new Keyframe(time, key.Value.Y);
      z[i] = new Keyframe(time, key.Value.Z);
    }
    clip.SetCurve(path, typeof(Transform), property + ".x", new AnimationCurve(x));
    clip.SetCurve(path, typeof(Transform), property + ".y", new AnimationCurve(y));
    clip.SetCurve(path, typeof(Transform), property + ".z", new AnimationCurve(z));
    return true;
  }

  private static bool SetRotationCurves(
    AnimationClip clip,
    string path,
    IList<QuaternionKey> keys,
    float ticksPerSecond)
  {
    if (keys.Count == 0)
    {
      return false;
    }

    var x = new Keyframe[keys.Count];
    var y = new Keyframe[keys.Count];
    var z = new Keyframe[keys.Count];
    Vector3 previousEuler = Vector3.zero;
    for (int i = 0; i < keys.Count; i++)
    {
      QuaternionKey key = keys[i];
      Vector3 euler = ConvertRotation(key.Value).eulerAngles;
      euler.y = -euler.y;
      if (i > 0)
      {
        euler.x = previousEuler.x + Mathf.DeltaAngle(previousEuler.x, euler.x);
        euler.y = previousEuler.y + Mathf.DeltaAngle(previousEuler.y, euler.y);
        euler.z = previousEuler.z + Mathf.DeltaAngle(previousEuler.z, euler.z);
      }
      previousEuler = euler;
      float time = (float)(key.Time / ticksPerSecond);
      x[i] = new Keyframe(time, euler.x);
      y[i] = new Keyframe(time, euler.y);
      z[i] = new Keyframe(time, euler.z);
    }

    clip.SetCurve(path, typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(x));
    clip.SetCurve(path, typeof(Transform), "localEulerAnglesRaw.y", new AnimationCurve(y));
    clip.SetCurve(path, typeof(Transform), "localEulerAnglesRaw.z", new AnimationCurve(z));
    return true;
  }
}
