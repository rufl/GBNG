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
#if USE_STEAMWORKS
using GameBuilder.Workshop;
#endif
using UnityEngine;

public class FeaturedWorkshopItems : MonoBehaviour
{
#if USE_STEAMWORKS
  // https://steamcommunity.com/sharedfiles/filedetails/?id=1569305489
  public static readonly ulong[] FeaturedPublishedFileIds = {
    1790543051,
    1790375733,
    1809912386
  };

  readonly List<WorkshopItem> items = new List<WorkshopItem>();
  bool waitingOnQuery = true;

  GameBuilderSceneController scenes;

  void Awake()
  {
    Util.FindIfNotSet(this, ref scenes);
  }

  IEnumerator Start()
  {
    while (!SteamRuntime.InitializationAttempted)
    {
      yield return null;
    }

    if (!SteamRuntime.Initialized)
    {
      waitingOnQuery = false;
      yield break;
    }

    SteamWorkshopService.Instance.GetItemsByIds(FeaturedPublishedFileIds, OnItemsLoaded);
  }

  void OnItemsLoaded(WorkshopItemListEventArgs args)
  {
    waitingOnQuery = false;
    items.Clear();
    if (args.IsError)
    {
      Util.LogError($"Failed to query featured Workshop items: {args.ErrorMessage}");
      return;
    }

    items.AddRange(args.ItemList.Items);
  }

  public ICollection<WorkshopItem> GetItems()
  {
    return items;
  }

  public bool IsWaitingOnQuery()
  {
    return waitingOnQuery;
  }
#endif
}
