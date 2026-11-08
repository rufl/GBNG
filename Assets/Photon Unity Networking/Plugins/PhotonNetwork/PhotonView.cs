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

// This lightweight PhotonView replacement bridges the existing game protocol
// to the free Unity Transport backend when PUN is not installed.
#if !USE_PUN

using System;
using System.Collections.Generic;
using UnityEngine;

public class PhotonView : MonoBehaviour
{
  static readonly List<PhotonView> views = new List<PhotonView>();
  static readonly HashSet<int> viewIdsInUse = new HashSet<int>();
  bool registeredViewId;

  public int viewID = 0;
  public int ownerId = 1;
  internal PhotonPlayer owner = PhotonNetwork.masterClient;

  public bool isMine
  {
    get { return PhotonNetwork.offlineMode || ownerId == PhotonNetwork.player.ID; }
  }

  void Awake()
  {
    if (!views.Contains(this)) views.Add(this);
    RegisterViewId();
  }

  void OnDestroy()
  {
    if (registeredViewId) UnregisterViewId(viewID);
    registeredViewId = false;
    views.Remove(this);
  }

  internal void SetViewId(int id)
  {
    if (viewID == id)
    {
      RegisterViewId();
      return;
    }
    if (registeredViewId) UnregisterViewId(viewID);
    viewID = id;
    RegisterViewId();
  }

  void RegisterViewId()
  {
    if (registeredViewId) return;
    if (viewID != 0) viewIdsInUse.Add(viewID);
    registeredViewId = true;
  }

  static void UnregisterViewId(int id)
  {
    if (id != 0) viewIdsInUse.Remove(id);
  }

  public void RPC(string methodName, PhotonTargets target, params object[] rpcParams)
  {
    DirectHostRpcRouter.SendRpc(this, methodName, target, rpcParams);
  }

  public void RPC(string methodName, PhotonPlayer target, params object[] rest)
  {
    DirectHostRpcRouter.SendRpc(this, methodName, target, rest);
  }

  public static PhotonView Get(UnityEngine.MonoBehaviour behaviour)
  {
    return behaviour.gameObject.GetComponent<PhotonView>();
  }

  public static PhotonView Find(int id)
  {
    PhotonView[] all = GetAllViews();
    for (int i = 0; i < all.Length; i++)
    {
      if (all[i] != null && all[i].viewID == id) return all[i];
    }
    return null;
  }

  internal static PhotonView[] GetAllViews()
  {
    PhotonView[] found = UnityEngine.Object.FindObjectsOfType<PhotonView>(true);
    for (int i = 0; i < found.Length; i++)
    {
      PhotonView view = found[i];
      if (view == null) continue;
      if (!views.Contains(view)) views.Add(view);
      view.RegisterViewId();
    }
    views.RemoveAll(view => view == null);
    return views.ToArray();
  }
  internal static bool IsViewIdInUse(int id)
  {
    return viewIdsInUse.Contains(id);
  }


  internal static void InitializeSceneViews()
  {
    PhotonView[] sceneViews = UnityEngine.Object.FindObjectsOfType<PhotonView>(true);
    Array.Sort(sceneViews, (a, b) => string.CompareOrdinal(GetStablePath(a.transform), GetStablePath(b.transform)));
    int id = -1;
    foreach (PhotonView view in sceneViews)
    {
      if (view == null) continue;
      if (!views.Contains(view)) views.Add(view);
      view.RegisterViewId();
      if (view.viewID != 0) continue;
      view.SetViewId(id--);
      view.ApplyOwnership(1);
    }
  }

  static string GetStablePath(Transform transform)
  {
    string path = transform.gameObject.scene.name;
    while (transform != null)
    {
      path = transform.GetSiblingIndex() + "/" + transform.name + "/" + path;
      transform = transform.parent;
    }
    return path;
  }

  internal void ApplyOwnership(int newOwnerId)
  {
    ownerId = newOwnerId;
    owner = PhotonNetwork.FindPlayer(newOwnerId);
  }

  internal void ApplyOwnershipAndNotify(int newOwnerId)
  {
    bool ownershipChanged = ownerId != newOwnerId;
    ApplyOwnership(newOwnerId);
    if (!ownershipChanged) return;
    VoosActor actor = GetComponent<VoosActor>();
    if (actor != null) actor.OnOwnershipTransfered();
  }

  internal void TransferOwnership(int newOwnerId)
  {
    ApplyOwnershipAndNotify(newOwnerId);
    DirectHostRpcRouter.SendOwnership(viewID, newOwnerId);
  }
}
#endif
