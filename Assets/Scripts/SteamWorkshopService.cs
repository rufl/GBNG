#if USE_STEAMWORKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Steamworks;
using UnityEngine;

namespace GameBuilder.Workshop
{
  public sealed class WorkshopItem
  {
    public ulong PublishedFileId;
    public string Name;
    public string Description;
    public string PreviewImageURL;
    public string InstalledLocalFolder;
    public string TagsString;
    public readonly List<string> Tags = new List<string>();
    public int VotesUp;
    public int VotesDown;
    public int Subscriptions;
    public bool IsSubscribed;
    public bool IsDownloading;
    public bool IsInstalled;
    public bool IsVotedUp;
    public bool IsVotedDown;
    public DateTime InstalledTimestamp;
  }

  public sealed class WorkshopItemUpdate
  {
    public ulong? PublishedFileId;
    public string Name;
    public string Description;
    public string ContentPath;
    public string IconPath;
    public readonly List<string> Tags = new List<string>();
    internal bool IsNewItem;
  }

  public sealed class WorkshopItemEventArgs : EventArgs
  {
    public WorkshopItem Item { get; private set; }
    public bool IsError { get; private set; }
    public string ErrorMessage { get; private set; }

    public WorkshopItemEventArgs(WorkshopItem item, bool isError = false, string errorMessage = null)
    {
      Item = item;
      IsError = isError;
      ErrorMessage = errorMessage;
    }
  }

  public sealed class WorkshopItemList
  {
    public List<WorkshopItem> Items { get; private set; }
    public uint PagesItems { get; private set; }

    public WorkshopItemList(List<WorkshopItem> items, uint pagesItems)
    {
      Items = items;
      PagesItems = pagesItems;
    }
  }

  public sealed class WorkshopItemListEventArgs : EventArgs
  {
    public WorkshopItemList ItemList { get; private set; }
    public bool IsError { get; private set; }
    public string ErrorMessage { get; private set; }

    public WorkshopItemListEventArgs(WorkshopItemList itemList, bool isError = false, string errorMessage = null)
    {
      ItemList = itemList;
      IsError = isError;
      ErrorMessage = errorMessage;
    }
  }

  public sealed class WorkshopItemUpdateEventArgs : EventArgs
  {
    public WorkshopItem Item { get; private set; }
    public bool IsError { get; private set; }
    public string ErrorMessage { get; private set; }

    public WorkshopItemUpdateEventArgs(WorkshopItem item, bool isError = false, string errorMessage = null)
    {
      Item = item;
      IsError = isError;
      ErrorMessage = errorMessage;
    }
  }

  public enum EWorkshopSource
  {
    SUBSCRIBED
  }

  public sealed class WorkshopSortMode
  {
    public EUGCQuery Query { get; private set; }
    public bool IsSubscribedList { get; private set; }

    public WorkshopSortMode(EUGCQuery query)
    {
      Query = query;
    }

    public WorkshopSortMode(EWorkshopSource source)
    {
      Query = EUGCQuery.k_EUGCQuery_RankedByPublicationDate;
      IsSubscribedList = source == EWorkshopSource.SUBSCRIBED;
    }
  }

  public sealed class SteamRuntime : MonoBehaviour
  {
    static SteamRuntime instance;
    static bool shutdown;

    public static bool Initialized { get; private set; }
    public static bool InitializationAttempted { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
      if (instance != null) return;
      var root = new GameObject("Steam Workshop Runtime");
      DontDestroyOnLoad(root);
      root.AddComponent<SteamRuntime>();
      root.AddComponent<SteamWorkshopService>();
    }

    void Awake()
    {
      if (instance != null && instance != this)
      {
        Destroy(gameObject);
        return;
      }

      instance = this;
      DontDestroyOnLoad(gameObject);
      InitializationAttempted = true;
      try
      {
        Initialized = SteamAPI.Init();
        shutdown = false;
        if (!Initialized) Util.Log("Steam API initialization failed; Steam Workshop is unavailable.");
      }
      catch (Exception e)
      {
        Initialized = false;
        Util.LogError($"Steam API initialization failed: {e.Message}");
      }
    }

    void Update()
    {
      if (!Initialized) return;
      try { SteamAPI.RunCallbacks(); }
      catch (Exception e) { Util.LogError($"Steam callback pump failed: {e.Message}"); }
    }

    void OnApplicationQuit() { Shutdown(); }
    void OnDestroy() { if (instance == this) Shutdown(); }

    static void Shutdown()
    {
      if (!Initialized || shutdown) return;
      shutdown = true;
      try { SteamAPI.Shutdown(); }
      catch (Exception e) { Util.LogError($"Steam API shutdown failed: {e.Message}"); }
      Initialized = false;
      instance = null;
    }
  }

  public sealed class SteamWorkshopService : MonoBehaviour
  {
    const uint QueryPageSize = 50;
    const uint InstallPathBufferSize = 4096;
    const uint PreviewUrlBufferSize = 4096;

    static SteamWorkshopService instance;
    readonly List<IDisposable> callResults = new List<IDisposable>();
    readonly Dictionary<ulong, WorkshopItem> itemsById = new Dictionary<ulong, WorkshopItem>();
    readonly Dictionary<WorkshopItemUpdate, UGCUpdateHandle_t> updates = new Dictionary<WorkshopItemUpdate, UGCUpdateHandle_t>();
    float nextStateRefresh;

    public static SteamWorkshopService Instance { get { return instance; } }
    public string SearchText { get; set; }
    public readonly List<string> SearchTags = new List<string>();
    public readonly List<string> ExcludedTags = new List<string>();
    public bool SearchMatchAnyTag { get; set; } = true;
    public bool IsSteamCacheEnabled { get; set; }
    public WorkshopSortMode Sorting { get; set; } = new WorkshopSortMode(EUGCQuery.k_EUGCQuery_RankedByTrend);

    void Awake()
    {
      if (instance != null && instance != this)
      {
        Destroy(this);
        return;
      }
      instance = this;
    }

    void Update()
    {
      if (!SteamRuntime.Initialized || Time.unscaledTime < nextStateRefresh) return;
      nextStateRefresh = Time.unscaledTime + 0.5f;
      foreach (WorkshopItem item in itemsById.Values) RefreshItemState(item);
    }

    void OnDestroy()
    {
      if (instance == this) instance = null;
      foreach (IDisposable result in callResults) result.Dispose();
      callResults.Clear();
    }

    public void ResetItemSearch()
    {
      SearchText = string.Empty;
      SearchMatchAnyTag = true;
      SearchTags.Clear();
      ExcludedTags.Clear();
      Sorting = new WorkshopSortMode(EUGCQuery.k_EUGCQuery_RankedByTrend);
    }

    public void GetItemList(uint page, Action<WorkshopItemListEventArgs> callback)
    {
      if (!CanCallSteam(callback, "Steam Workshop is unavailable.")) return;
      page = Math.Max(1u, page);
      AppId_t appId = SteamUtils.GetAppID();
      UGCQueryHandle_t query;
      if (Sorting != null && Sorting.IsSubscribedList)
      {
        query = SteamUGC.CreateQueryUserUGCRequest(
          SteamUser.GetSteamID().GetAccountID(), EUserUGCList.k_EUserUGCList_Subscribed,
          EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items,
          EUserUGCListSortOrder.k_EUserUGCListSortOrder_SubscriptionDateDesc,
          appId, appId, page);
      }
      else
      {
        EUGCQuery queryType = Sorting == null ? EUGCQuery.k_EUGCQuery_RankedByTrend : Sorting.Query;
        query = SteamUGC.CreateQueryAllUGCRequest(queryType, EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items, appId, appId, page);
        if (!string.IsNullOrWhiteSpace(SearchText)) SteamUGC.SetSearchText(query, SearchText);
        if (SearchTags.Count > 0) SteamUGC.SetMatchAnyTag(query, SearchMatchAnyTag);
        foreach (string tag in SearchTags)
          if (!string.IsNullOrWhiteSpace(tag)) SteamUGC.AddRequiredTag(query, tag);
        foreach (string tag in ExcludedTags)
          if (!string.IsNullOrWhiteSpace(tag)) SteamUGC.AddExcludedTag(query, tag);
      }

      if (query == UGCQueryHandle_t.Invalid)
      {
        callback?.Invoke(ErrorList("Steam rejected the Workshop query."));
        return;
      }
      if (!IsSteamCacheEnabled) SteamUGC.SetAllowCachedResponse(query, 0);
      SteamUGC.SetReturnLongDescription(query, true);
      SteamUGC.SetReturnMetadata(query, true);
      Track<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query), (result, ioFailure) =>
      {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
          SteamUGC.ReleaseQueryUGCRequest(query);
          callback?.Invoke(ErrorList($"Workshop query failed: {result.m_eResult}"));
          return;
        }
        callback?.Invoke(ReadQueryResults(result, result.m_unTotalMatchingResults));
      });
    }

    public void GetItemsByIds(ulong[] ids, Action<WorkshopItemListEventArgs> callback)
    {
      if (!CanCallSteam(callback, "Steam Workshop is unavailable.")) return;
      if (ids == null || ids.Length == 0)
      {
        callback?.Invoke(new WorkshopItemListEventArgs(new WorkshopItemList(new List<WorkshopItem>(), 0)));
        return;
      }

      PublishedFileId_t[] publishedIds = ids.Select(id => new PublishedFileId_t(id)).ToArray();
      UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest(publishedIds, (uint)publishedIds.Length);
      if (query == UGCQueryHandle_t.Invalid)
      {
        callback?.Invoke(ErrorList("Steam rejected the Workshop details query."));
        return;
      }
      if (!IsSteamCacheEnabled) SteamUGC.SetAllowCachedResponse(query, 0);
      Track<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query), (result, ioFailure) =>
      {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
          SteamUGC.ReleaseQueryUGCRequest(query);
          callback?.Invoke(ErrorList($"Workshop details query failed: {result.m_eResult}"));
          return;
        }
        callback?.Invoke(ReadQueryResults(result, result.m_unTotalMatchingResults));
      });
    }

    WorkshopItemListEventArgs ReadQueryResults(SteamUGCQueryCompleted_t result, uint totalMatches)
    {
      var items = new List<WorkshopItem>((int)result.m_unNumResultsReturned);
      try
      {
        for (uint i = 0; i < result.m_unNumResultsReturned; i++)
        {
          SteamUGCDetails_t details;
          if (!SteamUGC.GetQueryUGCResult(result.m_handle, i, out details) || details.m_eResult != EResult.k_EResultOK) continue;
          string preview;
          SteamUGC.GetQueryUGCPreviewURL(result.m_handle, i, out preview, PreviewUrlBufferSize);
          string tags = details.m_rgchTags ?? string.Empty;
          var item = new WorkshopItem
          {
            PublishedFileId = details.m_nPublishedFileId.m_PublishedFileId,
            Name = details.m_rgchTitle,
            Description = details.m_rgchDescription,
            PreviewImageURL = preview,
            TagsString = tags,
            VotesUp = (int)details.m_unVotesUp,
            VotesDown = (int)details.m_unVotesDown,
            InstalledTimestamp = DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated).UtcDateTime
          };
          item.Tags.AddRange(tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(tag => tag.Trim()));
          ulong subscriptions;
          if (SteamUGC.GetQueryUGCStatistic(result.m_handle, i, EItemStatistic.k_EItemStatistic_NumSubscriptions, out subscriptions))
            item.Subscriptions = subscriptions > int.MaxValue ? int.MaxValue : (int)subscriptions;
          RefreshItemState(item);
          itemsById[item.PublishedFileId] = item;
          items.Add(item);
          GetUserVote(item);
        }
      }
      finally { SteamUGC.ReleaseQueryUGCRequest(result.m_handle); }

      uint pages = totalMatches == 0 ? 1 : (uint)Math.Ceiling(totalMatches / (double)QueryPageSize);
      return new WorkshopItemListEventArgs(new WorkshopItemList(items, pages));
    }

    void GetUserVote(WorkshopItem item)
    {
      Track<GetUserItemVoteResult_t>(SteamUGC.GetUserItemVote(new PublishedFileId_t(item.PublishedFileId)), (result, ioFailure) =>
      {
        if (!ioFailure && result.m_eResult == EResult.k_EResultOK)
        {
          item.IsVotedUp = result.m_bVotedUp;
          item.IsVotedDown = result.m_bVotedDown;
        }
      });
    }

    void RefreshItemState(WorkshopItem item)
    {
      if (!SteamRuntime.Initialized) return;
      PublishedFileId_t id = new PublishedFileId_t(item.PublishedFileId);
      uint state = SteamUGC.GetItemState(id);
      item.IsSubscribed = (state & (uint)EItemState.k_EItemStateSubscribed) != 0;
      item.IsInstalled = (state & (uint)EItemState.k_EItemStateInstalled) != 0;
      item.IsDownloading = (state & (uint)EItemState.k_EItemStateDownloading) != 0 ||
        (state & (uint)EItemState.k_EItemStateDownloadPending) != 0;
      if (item.IsInstalled && SteamUGC.GetItemInstallInfo(id, out _, out string folder, InstallPathBufferSize, out uint timestamp))
      {
        item.InstalledLocalFolder = folder;
        item.InstalledTimestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
      }
      else if (!item.IsInstalled) item.InstalledLocalFolder = null;
    }

    public float GetDownloadProgress(WorkshopItem item)
    {
      if (item == null || !SteamRuntime.Initialized) return 0;
      if (item.IsInstalled && !item.IsDownloading) return 1;
      ulong downloaded, total;
      if (!SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(item.PublishedFileId), out downloaded, out total) || total == 0) return 0;
      return Mathf.Clamp01((float)((double)downloaded / total));
    }

    public void Subscribe(WorkshopItem item, Action<WorkshopItemEventArgs> callback)
    {
      if (item == null || !SteamRuntime.Initialized)
      {
        callback?.Invoke(ErrorItem(item, "Steam Workshop is unavailable."));
        return;
      }
      Track<RemoteStorageSubscribePublishedFileResult_t>(SteamUGC.SubscribeItem(new PublishedFileId_t(item.PublishedFileId)), (result, ioFailure) =>
      {
        bool error = ioFailure || result.m_eResult != EResult.k_EResultOK;
        RefreshItemState(item);
        callback?.Invoke(new WorkshopItemEventArgs(item, error, error ? $"Subscribe failed: {result.m_eResult}" : null));
      });
    }

    public void Unsubscribe(WorkshopItem item, Action<WorkshopItemEventArgs> callback)
    {
      if (item == null || !SteamRuntime.Initialized)
      {
        callback?.Invoke(ErrorItem(item, "Steam Workshop is unavailable."));
        return;
      }
      Track<RemoteStorageUnsubscribePublishedFileResult_t>(SteamUGC.UnsubscribeItem(new PublishedFileId_t(item.PublishedFileId)), (result, ioFailure) =>
      {
        bool error = ioFailure || result.m_eResult != EResult.k_EResultOK;
        RefreshItemState(item);
        callback?.Invoke(new WorkshopItemEventArgs(item, error, error ? $"Unsubscribe failed: {result.m_eResult}" : null));
      });
    }

    public void Vote(WorkshopItem item, bool up, Action<WorkshopItemEventArgs> callback)
    {
      if (item == null || !SteamRuntime.Initialized)
      {
        callback?.Invoke(ErrorItem(item, "Steam Workshop is unavailable."));
        return;
      }
      Track<SetUserItemVoteResult_t>(SteamUGC.SetUserItemVote(new PublishedFileId_t(item.PublishedFileId), up), (result, ioFailure) =>
      {
        bool error = ioFailure || result.m_eResult != EResult.k_EResultOK;
        if (!error)
        {
          item.IsVotedUp = result.m_bVoteUp;
          item.IsVotedDown = !result.m_bVoteUp;
        }
        callback?.Invoke(new WorkshopItemEventArgs(item, error, error ? $"Vote failed: {result.m_eResult}" : null));
      });
    }

    public WorkshopItemUpdate GetItemUpdateFromFolder(string folder)
    {
      if (string.IsNullOrWhiteSpace(folder)) return null;
      string metadataPath = Path.Combine(folder, GameBuilder.SteamUtil.WorkshopItemInfoFileName);
      if (!File.Exists(metadataPath)) return null;
      try
      {
        XDocument document = XDocument.Load(metadataPath);
        if (document.Root == null) return null;
        List<XElement> nodes = document.Root.DescendantsAndSelf().ToList();
        string[] idNames = { "publishedfileid", "m_npublishedfileid", "m_publishedfileid", "steamworkshopid" };
        XElement idNode = nodes.FirstOrDefault(node => idNames.Contains(node.Name.LocalName, StringComparer.OrdinalIgnoreCase));
        if (idNode == null || !ulong.TryParse(idNode.Value, out ulong id) || id == 0) return null;
        var update = new WorkshopItemUpdate { PublishedFileId = id, ContentPath = folder };
        string[] nameNames = { "name", "title", "m_rgchtitle" };
        string[] descriptionNames = { "description", "m_rgchdescription" };
        update.Name = nodes.FirstOrDefault(node => nameNames.Contains(node.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value;
        update.Description = nodes.FirstOrDefault(node => descriptionNames.Contains(node.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value;
        string iconFileName = nodes.FirstOrDefault(node => string.Equals(node.Name.LocalName, "IconFileName", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(iconFileName))
        {
          string iconPath = Path.Combine(folder, Path.GetFileName(iconFileName));
          if (File.Exists(iconPath)) update.IconPath = iconPath;
        }
        List<XElement> tagNodes = nodes.Where(node => string.Equals(node.Name.LocalName, "Tag", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tagNodes.Count > 0)
        {
          foreach (XElement tag in tagNodes) AddTags(update, tag.Value);
        }
        else
        {
          string[] tagContainerNames = { "tags", "m_tags", "tagsstring", "m_rgchtags" };
          XElement tagContainer = nodes.FirstOrDefault(node => tagContainerNames.Contains(node.Name.LocalName, StringComparer.OrdinalIgnoreCase));
          if (tagContainer != null)
            foreach (XElement tag in tagContainer.DescendantsAndSelf().Where(node => !node.HasElements)) AddTags(update, tag.Value);
        }
        return update;
      }
      catch (Exception e)
      {
        Util.LogError($"Could not read Workshop metadata from '{metadataPath}': {e.Message}");
        return null;
      }
    }
    static void AddTags(WorkshopItemUpdate update, string value)
    {
      if (string.IsNullOrWhiteSpace(value)) return;
      foreach (string part in value.Split(','))
      {
        string tag = part.Trim();
        if (tag.Length > 0 && !update.Tags.Contains(tag)) update.Tags.Add(tag);
      }
    }

    public void Upload(WorkshopItemUpdate update, Action<WorkshopItemUpdateEventArgs> callback)
    {
      if (update == null || string.IsNullOrWhiteSpace(update.ContentPath) || !Directory.Exists(update.ContentPath))
      {
        callback?.Invoke(ErrorUpload(update, "Workshop content directory was not found."));
        return;
      }
      if (!SteamRuntime.Initialized)
      {
        callback?.Invoke(ErrorUpload(update, "Steam Workshop is unavailable."));
        return;
      }
      if (update.Tags.Count == 0)
      {
        callback?.Invoke(ErrorUpload(update, "Workshop item must have at least one tag."));
        return;
      }
      if (update.PublishedFileId.HasValue)
      {
        BeginUpdate(update, update.PublishedFileId.Value, callback);
        return;
      }

      Track<CreateItemResult_t>(SteamUGC.CreateItem(SteamUtils.GetAppID(), EWorkshopFileType.k_EWorkshopFileTypeCommunity), (result, ioFailure) =>
      {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_nPublishedFileId.m_PublishedFileId == 0)
        {
          callback?.Invoke(ErrorUpload(update, $"Creating Workshop item failed: {result.m_eResult}"));
          return;
        }
        update.PublishedFileId = result.m_nPublishedFileId.m_PublishedFileId;
        update.IsNewItem = true;
        BeginUpdate(update, update.PublishedFileId.Value, callback);
      });
    }

    void BeginUpdate(WorkshopItemUpdate update, ulong id, Action<WorkshopItemUpdateEventArgs> callback)
    {
      UGCUpdateHandle_t handle = SteamUGC.StartItemUpdate(SteamUtils.GetAppID(), new PublishedFileId_t(id));
      if (handle == UGCUpdateHandle_t.Invalid)
      {
        callback?.Invoke(ErrorUpload(update, "Steam could not start the Workshop item update."));
        return;
      }
      bool ok = SteamUGC.SetItemTitle(handle, update.Name ?? string.Empty) &&
        SteamUGC.SetItemDescription(handle, update.Description ?? string.Empty) &&
        SteamUGC.SetItemTags(handle, update.Tags);
      if (ok)
      {
        update.PublishedFileId = id;
        if (!WriteWorkshopMetadata(update))
        {
          callback?.Invoke(ErrorUpload(update, "Could not save Workshop metadata."));
          return;
        }
        ok = SteamUGC.SetItemContent(handle, Path.GetFullPath(update.ContentPath));
      }
      if (ok && !string.IsNullOrWhiteSpace(update.IconPath) && File.Exists(update.IconPath))
        ok = SteamUGC.SetItemPreview(handle, Path.GetFullPath(update.IconPath));
      if (ok && update.IsNewItem)
        ok = SteamUGC.SetItemVisibility(handle, ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic);
      if (!ok)
      {
        callback?.Invoke(ErrorUpload(update, "Steam rejected one or more Workshop item fields."));
        return;
      }

      updates[update] = handle;
      Track<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(handle, string.Empty), (result, ioFailure) =>
      {
        updates.Remove(update);
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
          callback?.Invoke(ErrorUpload(update, $"Uploading Workshop item failed: {result.m_eResult}"));
          return;
        }
        update.PublishedFileId = result.m_nPublishedFileId.m_PublishedFileId;
        update.IsNewItem = false;
        var item = new WorkshopItem
        {
          PublishedFileId = result.m_nPublishedFileId.m_PublishedFileId,
          Name = update.Name,
          Description = update.Description,
          TagsString = string.Join(",", update.Tags)
        };
        item.Tags.AddRange(update.Tags);
        itemsById[item.PublishedFileId] = item;
        RefreshItemState(item);
        callback?.Invoke(new WorkshopItemUpdateEventArgs(item));
      });
    }

    bool WriteWorkshopMetadata(WorkshopItemUpdate update)
    {
      try
      {
        string iconFileName = string.IsNullOrWhiteSpace(update.IconPath) ? string.Empty : Path.GetFileName(update.IconPath);
        var document = new XDocument(new XElement("WorkshopItemInfo",
          new XElement("PublishedFileId", update.PublishedFileId.Value),
          new XElement("Name", update.Name ?? string.Empty),
          new XElement("Description", update.Description ?? string.Empty),
          new XElement("IconFileName", iconFileName),
          new XElement("Tags", update.Tags.Select(tag => new XElement("string", tag)))));
        document.Save(Path.Combine(update.ContentPath, GameBuilder.SteamUtil.WorkshopItemInfoFileName));
        return true;
      }
      catch (Exception e)
      {
        Util.LogError($"Could not save Workshop metadata: {e.Message}");
        return false;
      }
    }

    public float GetUploadProgress(WorkshopItemUpdate update)
    {
      if (update == null || !updates.TryGetValue(update, out UGCUpdateHandle_t handle) || !SteamRuntime.Initialized) return 0;
      ulong processed, total;
      EItemUpdateStatus state = SteamUGC.GetItemUpdateProgress(handle, out processed, out total);
      if (state == EItemUpdateStatus.k_EItemUpdateStatusInvalid) return 0;
      if (state == EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges) return 0.99f;
      return total == 0 ? 0 : Mathf.Clamp01((float)((double)processed / total));
    }

    public void QueryVisibility(ulong id, Action<Util.Maybe<bool>> callback)
    {
      if (!SteamRuntime.Initialized)
      {
        callback?.Invoke(Util.Maybe<bool>.CreateEmpty());
        return;
      }
      UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(id) }, 1);
      if (query == UGCQueryHandle_t.Invalid)
      {
        callback?.Invoke(Util.Maybe<bool>.CreateEmpty());
        return;
      }
      Track<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(query), (result, ioFailure) =>
      {
        try
        {
          if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned == 0)
          {
            callback?.Invoke(Util.Maybe<bool>.CreateEmpty());
            return;
          }
          SteamUGCDetails_t details;
          if (!SteamUGC.GetQueryUGCResult(query, 0, out details) || details.m_eResult != EResult.k_EResultOK)
            callback?.Invoke(Util.Maybe<bool>.CreateEmpty());
          else
            callback?.Invoke(Util.Maybe<bool>.CreateWith(details.m_eVisibility == ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic));
        }
        finally { SteamUGC.ReleaseQueryUGCRequest(query); }
      });
    }

    public void SetVisibilityPrivate(ulong id, Action<bool> callback)
    {
      if (!SteamRuntime.Initialized)
      {
        callback?.Invoke(false);
        return;
      }
      UGCUpdateHandle_t handle = SteamUGC.StartItemUpdate(SteamUtils.GetAppID(), new PublishedFileId_t(id));
      if (handle == UGCUpdateHandle_t.Invalid || !SteamUGC.SetItemVisibility(handle, ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate))
      {
        callback?.Invoke(false);
        return;
      }
      Track<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(handle, "hide asset"), (result, ioFailure) =>
        callback?.Invoke(!ioFailure && result.m_eResult == EResult.k_EResultOK));
    }

    void Track<T>(SteamAPICall_t call, Action<T, bool> callback) where T : struct
    {
      if (call == SteamAPICall_t.Invalid)
      {
        callback?.Invoke(default(T), true);
        return;
      }
      CallResult<T> result = null;
      result = CallResult<T>.Create((data, ioFailure) =>
      {
        callResults.Remove(result);
        result.Dispose();
        callback?.Invoke(data, ioFailure);
      });
      callResults.Add(result);
      result.Set(call);
    }

    bool CanCallSteam(Action<WorkshopItemListEventArgs> callback, string message)
    {
      if (SteamRuntime.Initialized) return true;
      callback?.Invoke(ErrorList(message));
      return false;
    }

    static WorkshopItemListEventArgs ErrorList(string message)
    {
      return new WorkshopItemListEventArgs(new WorkshopItemList(new List<WorkshopItem>(), 0), true, message);
    }

    static WorkshopItemEventArgs ErrorItem(WorkshopItem item, string message)
    {
      return new WorkshopItemEventArgs(item, true, message);
    }

    static WorkshopItemUpdateEventArgs ErrorUpload(WorkshopItemUpdate update, string message)
    {
      if (update == null) return new WorkshopItemUpdateEventArgs(null, true, message);
      var item = new WorkshopItem
      {
        PublishedFileId = update.PublishedFileId.GetValueOrDefault(),
        Name = update.Name,
        Description = update.Description,
        TagsString = string.Join(",", update.Tags)
      };
      item.Tags.AddRange(update.Tags);
      return new WorkshopItemUpdateEventArgs(item, true, message);
    }
  }
}
#endif
