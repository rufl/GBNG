using System.Collections.Generic;
using UnityEngine;

internal sealed class RuntimeModelResourceSet
{
  readonly List<Object> resources = new List<Object>();
  readonly HashSet<Object> resourceLookup = new HashSet<Object>();
  int referenceCount = 1;
  bool ownerReleased;
  bool disposed;

  public static RuntimeModelResourceSet Capture(GameObject root)
  {
    var resourceSet = new RuntimeModelResourceSet();
    if (root == null) return resourceSet;

    foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
    {
      resourceSet.Add(filter.sharedMesh);
    }
    foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
    {
      resourceSet.Add(renderer.sharedMesh);
    }
    foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
    {
      resourceSet.Add(collider.sharedMesh);
    }
    foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
    {
      foreach (Material material in renderer.sharedMaterials)
      {
        resourceSet.AddMaterialAndTextures(material);
      }
    }

    return resourceSet;
  }

  public void Add(UnityEngine.Object resource)
  {
    if (resource == null) return;
    if (disposed) throw new System.ObjectDisposedException(nameof(RuntimeModelResourceSet));
    if (resourceLookup.Add(resource)) resources.Add(resource);
  }

  void AddMaterialAndTextures(Material material)
  {
    if (material == null) return;
    Add(material);
    foreach (string propertyName in material.GetTexturePropertyNames())
    {
      Add(material.GetTexture(propertyName));
    }
  }

  public GameObject Clone(GameObject source)
  {
    if (source == null) return null;

    RuntimeModelResourceLease sourceLease = source.GetComponent<RuntimeModelResourceLease>();
    RuntimeModelResourceSet sharedResources =
      sourceLease != null && sourceLease.Resources != null ? sourceLease.Resources : this;
    return CloneWithResources(source, sharedResources);
  }

  internal static GameObject CloneFrom(GameObject source)
  {
    if (source == null) return null;
    RuntimeModelResourceLease lease = source.GetComponent<RuntimeModelResourceLease>();
    if (lease == null || lease.Resources == null) return Object.Instantiate(source);
    return CloneWithResources(source, lease.Resources);
  }

  static GameObject CloneWithResources(GameObject source, RuntimeModelResourceSet resourceSet)
  {
    GameObject clone = Object.Instantiate(source);
    try
    {
      RuntimeModelResourceLease lease = clone.GetComponent<RuntimeModelResourceLease>();
      if (lease == null) lease = clone.AddComponent<RuntimeModelResourceLease>();
      lease.ResetForClone();
      lease.Bind(resourceSet);
      return clone;
    }
    catch
    {
      DestroyResource(clone);
      throw;
    }
  }

  internal void AcquireReference()
  {
    if (disposed) throw new System.ObjectDisposedException(nameof(RuntimeModelResourceSet));
    referenceCount++;
  }

  internal void ReleaseReference()
  {
    if (referenceCount <= 0) return;
    referenceCount--;
    if (referenceCount != 0) return;

    disposed = true;
    for (int i = resources.Count - 1; i >= 0; i--)
    {
      DestroyResource(resources[i]);
    }
    resources.Clear();
    resourceLookup.Clear();
  }

  public void ReleaseOwner()
  {
    if (ownerReleased) return;
    ownerReleased = true;
    ReleaseReference();
  }

  internal static void DestroyResource(UnityEngine.Object resource)
  {
    if (resource == null) return;
    if (Application.isPlaying) Object.Destroy(resource);
    else Object.DestroyImmediate(resource);
  }
}

[DisallowMultipleComponent]
internal sealed class RuntimeModelResourceLease : MonoBehaviour
{
  [System.NonSerialized] RuntimeModelResourceSet resources;
  [System.NonSerialized] bool ownsReference;

  internal RuntimeModelResourceSet Resources { get { return resources; } }

  internal void ResetForClone()
  {
    resources = null;
    ownsReference = false;
  }

  internal void Bind(RuntimeModelResourceSet resourceSet)
  {
    resources = resourceSet;
    resources.AcquireReference();
    ownsReference = true;
  }

  internal void Release()
  {
    if (!ownsReference) return;
    RuntimeModelResourceSet current = resources;
    resources = null;
    ownsReference = false;
    current.ReleaseReference();
  }

  void OnDestroy()
  {
    Release();
  }

  internal static GameObject Clone(GameObject source)
  {
    return RuntimeModelResourceSet.CloneFrom(source);
  }
}
