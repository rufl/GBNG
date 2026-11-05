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

using System.Collections.Generic;
#if USE_STEAMWORKS
using GameBuilder.Workshop;
#endif
using UnityEngine;

namespace GameBuilder
{
  public static class SteamUtil
  {
    public enum GameBuilderTags
    {
      Project,
      Asset,
      WAV,
      FBX,
      AssetBundle,
      CardPack,
      Actor,
      Image,
      BlockStyle
    }

    public const string WorkshopItemInfoFileName = "WorkshopItemInfo.xml";

#if USE_STEAMWORKS

    public delegate void WorkShopItemHandler(Util.Maybe<WorkshopItem> item);

    public static ulong GetId(this WorkshopItem item)
    {
      return item.PublishedFileId;
    }

    public static void GetWorkshopItem(ulong publishedFileId, WorkShopItemHandler handleItem)
    {
      SteamWorkshopService.Instance.GetItemsByIds(new[] { publishedFileId }, args =>
      {
        if (args.IsError)
        {
          Util.LogError(args.ErrorMessage);
          handleItem(Util.Maybe<WorkshopItem>.CreateEmpty());
          return;
        }

        WorkshopItem item = args.ItemList.Items.Find(candidate => candidate.PublishedFileId == publishedFileId);
        if (item == null) handleItem(Util.Maybe<WorkshopItem>.CreateEmpty());
        else handleItem(Util.Maybe<WorkshopItem>.CreateWith(item));
      });
    }

    public static void QueryWorkShopItemVisibility(ulong publishedFileId, System.Action<Util.Maybe<bool>> handleResult)
    {
      SteamWorkshopService.Instance.QueryVisibility(publishedFileId, handleResult);
    }
#endif
  }
}