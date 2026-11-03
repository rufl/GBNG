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
  }

  void OnDestroy()
  {
    views.Remove(this);
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
      if (found[i] != null && !views.Contains(found[i])) views.Add(found[i]);
    }
    views.RemoveAll(view => view == null);
    return views.ToArray();
  }

  internal static void InitializeSceneViews()
  {
    PhotonView[] sceneViews = UnityEngine.Object.FindObjectsOfType<PhotonView>(true);
    Array.Sort(sceneViews, (a, b) => string.CompareOrdinal(GetStablePath(a.transform), GetStablePath(b.transform)));
    int id = -1;
    foreach (PhotonView view in sceneViews)
    {
      if (view == null || view.viewID != 0) continue;
      view.viewID = id--;
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

  internal void TransferOwnership(int newOwnerId)
  {
    ApplyOwnership(newOwnerId);
    DirectHostRpcRouter.SendOwnership(viewID, newOwnerId);
  }
}
#endif
