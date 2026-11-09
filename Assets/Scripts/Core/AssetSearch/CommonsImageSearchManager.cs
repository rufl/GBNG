using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class CommonsImageSearchManager : MonoBehaviour
{
  const int MAX_ASSETS_RETURNED = 10;
  const int MAX_SEARCH_RESULTS = 20;
  const string API_ENDPOINT = "https://commons.wikimedia.org/w/api.php";
  const string API_USER_AGENT = "GameBuilderOSS/1.0 (https://github.com/rufl/GBNG)";
  const string ATTRIBUTION_LICENSE_PREFIX = "CC BY ";
  AssetCache assetCache;

  sealed class SearchOperation : IDisposable
  {
    readonly List<UnityWebRequest> activeRequests = new List<UnityWebRequest>();
    bool cancelled;

    public bool IsCancelled { get { return cancelled; } }

    public bool RegisterRequest(UnityWebRequest request)
    {
      if (cancelled)
      {
        request.Abort();
        return false;
      }

      activeRequests.Add(request);
      return true;
    }

    public void UnregisterRequest(UnityWebRequest request)
    {
      activeRequests.Remove(request);
    }

    public void Dispose()
    {
      if (cancelled) return;
      cancelled = true;

      for (int i = 0; i < activeRequests.Count; i++)
      {
        UnityWebRequest request = activeRequests[i];
        if (request != null && !request.isDone) request.Abort();
      }
    }
  }

  void Awake()
  {
    Util.FindIfNotSet(this, ref assetCache);
  }

  public void RequestRenderable(string uri, RenderableRequestEventHandler requestCallback)
  {
    assetCache.Get(uri, (entry) => requestCallback(entry.GetAssetClone()));
  }

  public IDisposable Search(string searchString, OnActorableSearchResult resultCallback, Action<bool> onComplete)
  {
    SearchOperation operation = new SearchOperation();
    if (string.IsNullOrWhiteSpace(searchString))
    {
      onComplete?.Invoke(false);
      return operation;
    }

    StartCoroutine(SearchRoutine(searchString, resultCallback, onComplete, operation));
    return operation;
  }

  IEnumerator SearchRoutine(
    string searchString,
    OnActorableSearchResult resultCallback,
    Action<bool> onComplete,
    SearchOperation operation)
  {

    string query = API_ENDPOINT
      + "?action=query&generator=search&gsrnamespace=6"
      + "&gsrlimit=" + MAX_SEARCH_RESULTS
      + "&gsrsearch=" + UnityWebRequest.EscapeURL(searchString)
      + "&prop=imageinfo&iiprop=url%7Cmime%7Cextmetadata&iiurlwidth=320"
      + "&iiextmetadatafilter=LicenseShortName%7CArtist%7CCredit%7CLicenseUrl"
      + "&format=json&formatversion=2&maxlag=5";

    using (UnityWebRequest request = UnityWebRequest.Get(query))
    {
      request.timeout = 20;
      request.SetRequestHeader("Api-User-Agent", API_USER_AGENT);
      if (!operation.RegisterRequest(request)) yield break;
      try
      {
        yield return request.SendWebRequest();
      }
      finally
      {
        operation.UnregisterRequest(request);
      }
      if (operation.IsCancelled) yield break;

      if (request.result != UnityWebRequest.Result.Success)
      {
        Debug.LogWarning("Wikimedia Commons image search failed: " + request.error);
        onComplete?.Invoke(false);
        yield break;
      }

      CommonsSearchResponse response = null;
      try
      {
        response = JsonUtility.FromJson<CommonsSearchResponse>(request.downloadHandler.text);
      }
      catch (Exception exception)
      {
        Debug.LogWarning("Could not parse Wikimedia Commons image search results: " + exception.Message);
      }

      if (response == null || response.query == null || response.query.pages == null)
      {
        onComplete?.Invoke(false);
        yield break;
      }

      Array.Sort(response.query.pages, (left, right) => left.index.CompareTo(right.index));
      int acceptedCount = 0;
      foreach (CommonsPage page in response.query.pages)
      {
        if (operation.IsCancelled) yield break;
        if (page == null || page.imageinfo == null || page.imageinfo.Length == 0) continue;
        CommonsImageInfo info = page.imageinfo[0];
        if (info == null || (info.mime != "image/jpeg" && info.mime != "image/png")) continue;
        string licenseName = GetMetadataValue(info.extmetadata == null ? null : info.extmetadata.LicenseShortName);
        if (!IsAllowedLicense(licenseName)) continue;

        string artist = CleanMetadata(GetMetadataValue(info.extmetadata == null ? null : info.extmetadata.Artist));
        if (string.IsNullOrEmpty(artist))
        {
          artist = CleanMetadata(GetMetadataValue(info.extmetadata == null ? null : info.extmetadata.Credit));
        }
        if (IsAttributionRequired(licenseName) && string.IsNullOrEmpty(artist)) continue;

        string attribution = BuildAttribution(page, info, artist);
        if (string.IsNullOrEmpty(info.thumburl) || string.IsNullOrEmpty(info.url)) continue;

        string name = page.title ?? "Wikimedia Commons image";
        if (name.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) name = name.Substring(5);
        ImageResult imageResult = new ImageResult(info.url, info.thumburl, name, attribution);
        StartCoroutine(LoadThumbnail(imageResult, resultCallback, operation));
        acceptedCount++;
        if (acceptedCount >= MAX_ASSETS_RETURNED) break;
      }

      if (!operation.IsCancelled) onComplete?.Invoke(acceptedCount > 0);
    }
  }

  IEnumerator LoadThumbnail(ImageResult imageResult, OnActorableSearchResult resultCallback, SearchOperation operation)
  {
    using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(imageResult.thumbnailUrl))
    {
      request.timeout = 30;
      if (!operation.RegisterRequest(request)) yield break;
      try
      {
        yield return request.SendWebRequest();
      }
      finally
      {
        operation.UnregisterRequest(request);
      }
      if (operation.IsCancelled || request.result != UnityWebRequest.Result.Success) yield break;

      Texture2D texture = DownloadHandlerTexture.GetContent(request);
      if (texture == null) yield break;

      ActorableSearchResult result = new ActorableSearchResult();
      result.preferredRotation = Quaternion.identity;
      result.preferredScaleFunction = (go) => new Vector3(1f, 1f, 1f);
      result.renderableReference.assetType = AssetType.Image;
      result.name = imageResult.name;
      result.renderableReference.uri = new ImageVoosAsset(imageResult.url, imageResult.attribution).GetUri();
      result.thumbnail = texture;
      resultCallback(result);
    }
  }

  static bool IsAllowedLicense(string licenseName)
  {
    if (string.IsNullOrWhiteSpace(licenseName)) return false;
    string license = licenseName.Trim();
    if (string.Equals(license, "Public domain", StringComparison.OrdinalIgnoreCase)
      || string.Equals(license, "CC0", StringComparison.OrdinalIgnoreCase)) return true;
    if (!license.StartsWith(ATTRIBUTION_LICENSE_PREFIX, StringComparison.OrdinalIgnoreCase)) return false;

    string version = license.Substring(ATTRIBUTION_LICENSE_PREFIX.Length);
    return version == "1.0" || version == "2.0" || version == "2.5"
      || version == "3.0" || version == "4.0";
  }

  static bool IsAttributionRequired(string licenseName)
  {
    return licenseName != null
      && licenseName.Trim().StartsWith(ATTRIBUTION_LICENSE_PREFIX, StringComparison.OrdinalIgnoreCase);
  }

  static string BuildAttribution(CommonsPage page, CommonsImageInfo info, string artist)
  {
    List<string> parts = new List<string>();
    if (!string.IsNullOrEmpty(page.title)) parts.Add(page.title);
    if (!string.IsNullOrEmpty(artist)) parts.Add("by " + artist);

    CommonsMetadata metadata = info.extmetadata;
    string licenseName = CleanMetadata(GetMetadataValue(metadata == null ? null : metadata.LicenseShortName));
    if (!string.IsNullOrEmpty(licenseName)) parts.Add(licenseName);

    string licenseUrl = CleanMetadata(GetMetadataValue(metadata == null ? null : metadata.LicenseUrl));
    if (!string.IsNullOrEmpty(licenseUrl)) parts.Add(licenseUrl);
    if (!string.IsNullOrEmpty(info.descriptionurl)) parts.Add(info.descriptionurl);
    return string.Join(" | ", parts.ToArray());
  }
  static string GetMetadataValue(CommonsMetadataValue metadata)
  {
    return metadata == null ? null : metadata.value;
  }

  static string CleanMetadata(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    string plainText = Regex.Replace(WebUtility.HtmlDecode(value), "<[^>]*>", " ");
    return Regex.Replace(plainText, "\\s+", " ").Trim();
  }

  [Serializable]
  class CommonsSearchResponse
  {
    public CommonsQuery query;
  }

  [Serializable]
  class CommonsQuery
  {
    public CommonsPage[] pages;
  }

  [Serializable]
  class CommonsPage
  {
    public int index;
    public string title;
    public CommonsImageInfo[] imageinfo;
  }

  [Serializable]
  class CommonsImageInfo
  {
    public string url;
    public string thumburl;
    public string descriptionurl;
    public string mime;
    public CommonsMetadata extmetadata;
  }

  [Serializable]
  class CommonsMetadata
  {
    public CommonsMetadataValue LicenseShortName;
    public CommonsMetadataValue Artist;
    public CommonsMetadataValue Credit;
    public CommonsMetadataValue LicenseUrl;
  }

  [Serializable]
  class CommonsMetadataValue
  {
    public string value;
  }

  struct ImageResult
  {
    public readonly string url;
    public readonly string thumbnailUrl;
    public readonly string name;
    public readonly string attribution;

    public ImageResult(string url, string thumbnailUrl, string name, string attribution)
    {
      this.url = url;
      this.thumbnailUrl = thumbnailUrl;
      this.name = name;
      this.attribution = attribution;
    }
  }
}
