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
using UnityEngine;

public class JointControl : MonoBehaviour
{
  [SerializeField] LineRenderer lineRenderer;
  [SerializeField] CapsuleCollider capsuleCollider;
  [SerializeField] Transform startTransform;
  [SerializeField] Transform endTransform;

  [SerializeField] Color color;

  Material[] tintMaterialInstances;

  void Awake()
  {
    Renderer[] renderers = GetComponentsInChildren<Renderer>();
    bool lineRendererIsChild = false;
    for (int i = 0; i < renderers.Length; i++)
    {
      if (renderers[i] == lineRenderer)
      {
        lineRendererIsChild = true;
        break;
      }
    }
    if (lineRenderer != null && !lineRendererIsChild)
    {
      System.Array.Resize(ref renderers, renderers.Length + 1);
      renderers[renderers.Length - 1] = lineRenderer;
    }

    tintMaterialInstances = new Material[renderers.Length];
    for (int i = 0; i < renderers.Length; i++)
    {
      Renderer rend = renderers[i];
      Material source = rend.sharedMaterial;
      if (source == null) continue;

      Material instance = new Material(source);
      instance.color = color;
      rend.sharedMaterial = instance;
      tintMaterialInstances[i] = instance;
    }
  }

  void OnDestroy()
  {
    if (tintMaterialInstances == null) return;
    foreach (Material material in tintMaterialInstances)
    {
      RuntimeModelResourceSet.DestroyResource(material);
    }
  }

  VoosActor actorA;
  VoosActor actorB;

  Vector3 offsetA;
  Vector3 offsetB;

  public void SetActorsWithOffsets(VoosActor _actorA, Vector3 _localOffsetA, VoosActor _actorB, Vector3 _localOffsetB)
  {
    actorA = _actorA;
    actorB = _actorB;

    offsetA = _localOffsetA;
    offsetB = _localOffsetB;
  }

  void LateUpdate()
  {
    Vector3[] posArray = new Vector3[]{
      actorA.transform.TransformPoint(offsetA),
      actorB.transform.TransformPoint(offsetB)
    };

    transform.position = Vector3.Lerp(posArray[0], posArray[1], 0.5f);
    transform.LookAt(posArray[0]);
    capsuleCollider.height = Vector3.Distance(posArray[0], posArray[1]);

    startTransform.position = posArray[0];
    endTransform.position = posArray[1];

    startTransform.LookAt(posArray[1]);
    endTransform.LookAt(posArray[0]);

    lineRenderer.SetPositions(posArray);
  }

  public void Destruct()
  {
    Destroy(gameObject);
  }
}
