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

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SelectionFeedback : MonoBehaviour
{
  [SerializeField] Renderer feedbackRenderer;

  [SerializeField] Color defaultColor = Color.blue;
  [SerializeField] protected Color selectColor = Color.green;

  Vector3 prevScale = Vector3.zero;
  public float scaleMod = 1;
  Vector3 offset = Vector3.zero;

  protected VoosActor currentActor;
  readonly Dictionary<Renderer, Material> ownedMaterialInstances =
    new Dictionary<Renderer, Material>();
  Material feedbackMaterialInstance;

  public void SetVisiblity(bool on)
  {
    gameObject.SetActive(on);
  }

  protected virtual void Awake()
  {
    if (feedbackRenderer == null) return;
    feedbackMaterialInstance = GetOrCreateOwnedMaterial(feedbackRenderer);
    if (feedbackMaterialInstance != null) feedbackMaterialInstance.color = defaultColor;
  }

  protected Material GetOrCreateOwnedMaterial(Renderer renderer)
  {
    if (renderer == null) return null;
    Material instance;
    if (ownedMaterialInstances.TryGetValue(renderer, out instance)) return instance;

    Material source = renderer.sharedMaterial;
    if (source == null) return null;
    instance = new Material(source);
    renderer.sharedMaterial = instance;
    ownedMaterialInstances.Add(renderer, instance);
    return instance;
  }

  protected virtual void OnDestroy()
  {
    foreach (Material material in ownedMaterialInstances.Values)
    {
      RuntimeModelResourceSet.DestroyResource(material);
    }
    ownedMaterialInstances.Clear();
  }

  public virtual void SetSelected(bool on)
  {
    if (feedbackMaterialInstance != null)
      feedbackMaterialInstance.color = on ? selectColor : defaultColor;
  }

  public void SetColor(Color color)
  {
    if (feedbackMaterialInstance != null) feedbackMaterialInstance.color = color;
  }

  public VoosActor GetActor()
  {
    return currentActor;
  }

  public virtual void SetActor(VoosActor _actor)
  {
    // SetSelected(false);
    if (_actor == null) currentActor = null;
    else
    {
      currentActor = _actor;
    }

    if (currentActor != null)
    {
      SetVisiblity(true);
      UpdatePosition();
    }
    else
    {
      SetVisiblity(false);
    }
  }

  public virtual void UpdatePosition()
  {
    if (currentActor != null && gameObject.activeSelf)
    {
      transform.position = currentActor.ComputeWorldRenderBounds().center;
      transform.localScale = currentActor.ComputeWorldRenderBounds().size * scaleMod;
    }
    else SetVisiblity(false);
  }

  internal void RequestDestroy()
  {
    Destroy(gameObject);
  }
}
