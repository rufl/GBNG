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

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.Events;

public class TemplateItem : MonoBehaviour
{
  [SerializeField] TMPro.TMP_Text title;
  [SerializeField] Image thumbnail;

  UnityWebRequest thumbnailWebRequest;
  UnityWebRequestAsyncOperation thumbnailRequestOperation;
  Texture2D thumbnailTexture;
  Sprite thumbnailSprite;

  public void SetTitle(string title)
  {
    this.title.text = title;
  }

  public void SetThumbnailPath(string path)
  {
    ReleaseThumbnailRequest();

    // MacOSX requires file://
    if (!path.StartsWith("file://"))
    {
      path = "file://" + path;
    }

    thumbnailWebRequest = UnityWebRequestTexture.GetTexture(path);
    try
    {
      thumbnailRequestOperation = thumbnailWebRequest.SendWebRequest();
      if (thumbnailRequestOperation.isDone)
      {
        CompleteThumbnailRequest(thumbnailRequestOperation);
      }
      else
      {
        thumbnailRequestOperation.completed += OnThumbnailRequestCompleted;
      }
    }
    catch
    {
      ReleaseThumbnailRequest();
      throw;
    }
  }

  public void AddClickListener(UnityAction listener)
  {
    GetComponent<Button>().onClick.AddListener(listener);
  }

  void OnThumbnailRequestCompleted(AsyncOperation operation)
  {
    CompleteThumbnailRequest(operation as UnityWebRequestAsyncOperation);
  }

  void CompleteThumbnailRequest(UnityWebRequestAsyncOperation operation)
  {
    if (operation == null || operation != thumbnailRequestOperation) return;

    UnityWebRequest request = operation.webRequest;
    if (!object.ReferenceEquals(request, thumbnailWebRequest)) return;

    operation.completed -= OnThumbnailRequestCompleted;
    thumbnailRequestOperation = null;
    thumbnailWebRequest = null;

    Texture2D texture = null;
    bool assigned = false;
    try
    {
      if (request.result != UnityWebRequest.Result.Success || thumbnail == null) return;

      texture = DownloadHandlerTexture.GetContent(request);
      if (texture == null) return;

      Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
      if (thumbnailSprite != null) Destroy(thumbnailSprite);
      if (thumbnailTexture != null) Destroy(thumbnailTexture);

      thumbnailTexture = texture;
      thumbnailSprite = sprite;
      thumbnail.sprite = sprite;
      assigned = true;
    }
    finally
    {
      request.Dispose();
      if (!assigned && texture != null) Destroy(texture);
    }
  }

  void ReleaseThumbnailRequest()
  {
    UnityWebRequestAsyncOperation operation = thumbnailRequestOperation;
    thumbnailRequestOperation = null;
    if (operation != null) operation.completed -= OnThumbnailRequestCompleted;

    UnityWebRequest request = thumbnailWebRequest;
    thumbnailWebRequest = null;
    if (request == null) return;

    if (!request.isDone) request.Abort();
    request.Dispose();
  }

  void OnDestroy()
  {
    ReleaseThumbnailRequest();
    if (thumbnailSprite != null) Destroy(thumbnailSprite);
    if (thumbnailTexture != null) Destroy(thumbnailTexture);
  }
}
