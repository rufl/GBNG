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

#if USE_STEAMWORKS
using GameBuilder.Workshop;
#endif

using GameBuilder;

public class GameDetail : MonoBehaviour
{
  [SerializeField] UnityEngine.UI.Image thumbnailImage;
  [SerializeField] TMPro.TextMeshProUGUI descriptionField;
  [SerializeField] UnityEngine.UI.Button playButton;
  [SerializeField] UnityEngine.UI.Button buildButton;
  [SerializeField] UnityEngine.UI.Button subscribeButton;
  [SerializeField] TMPro.TextMeshProUGUI subscribeButtonText;
  [SerializeField] TMPro.TextMeshProUGUI downloadText;
  [SerializeField] GameObject downloadTextObject;
  [SerializeField] GameObject openButtonsParentObject;
  [SerializeField] Texture2D placeholderThumbnailTexture;
  Sprite ownedThumbnailSprite;

  DynamicPopup popups;
  LoadingScreen loadingScreen;

  GameBuilderSceneController sceneController;
  GameSource gameSource;
#if USE_STEAMWORKS
  WorkshopItem workshopItem;
#endif

  const string SUBSCRIBE_TEXT = "SUBSCRIBE";
  const string SUBSCRIBING_TEXT = "SUBBING";
  const string UNSUBSCRIBE_TEXT = "UNSUBSCRIBE";
  const string UNSUBSCRIBING_TEXT = "UNSUBBING";
  const string UPDATE_TEXT = "UPDATE";
  const string UPDATING_TEXT = "UPDATING";

  System.Action playAction;
  System.Action buildAction;

  public enum GameSource
  {
    Local,
    Workshop,
    Multiplayer,
    Special,
    Autosave
  }

  void Awake()
  {
    Util.FindIfNotSet(this, ref sceneController);
    Util.FindIfNotSet(this, ref popups);
    Util.FindIfNotSet(this, ref loadingScreen);
    playButton.onClick.AddListener(() => playAction?.Invoke());
    buildButton.onClick.AddListener(() => buildAction?.Invoke());
#if USE_STEAMWORKS
    subscribeButton.onClick.AddListener(OnSubscribeButton);
#endif
  }
  public void FitTo(RectTransform parentRect)
  {
    transform.parent = parentRect.transform;
    transform.localScale = Vector3.one;
    RectTransform rect = GetComponent<RectTransform>();
    rect.offsetMin = Vector2.zero;
    rect.offsetMax = Vector2.zero;
  }

  public void OpenSpecial(string descriptionText, Texture2D thumbnail, System.Action<GameBuilderApplication.PlayOptions> customLoadAction, bool bypassPlayOptsDialog)
  {
    gameObject.SetActive(true);
    gameSource = GameSource.Special;
    descriptionField.text = descriptionText;

    SetThumbnail(thumbnail);

    if (bypassPlayOptsDialog)
    {
      playAction = () => customLoadAction(new GameBuilderApplication.PlayOptions());
    }
    else
    {
      playAction = () => OnPlayTriggered(playOpts => customLoadAction(playOpts));
    }
  }

  public void OpenLocal(GameBundleLibrary.Entry entry)
  {
    gameObject.SetActive(true);
    gameSource = GameSource.Local;
    GameBundle.Metadata metadata = entry.bundle.GetMetadata();
    descriptionField.text = $"<b>{metadata.name}</b>\n{metadata.description}";

    Texture2D texture = entry.bundle.GetThumbnail();
    SetThumbnail(texture != null ? texture : placeholderThumbnailTexture);

    playAction = () => OnPlayTriggered(playOpts =>
    {
      LoadLibraryBundle(entry, playOpts);
    });
  }

#if USE_STEAMWORKS
  string GetWorkshopHeader(WorkshopItem item)
  {
    if (item.VotesUp + item.VotesDown > 5)
    {
      return $"{Mathf.RoundToInt(100 * item.VotesUp / (item.VotesUp + item.VotesDown))}% rating - {item.Subscriptions} subs";
    }
    else
    {
      return $"{item.Subscriptions} subscribers";
    }
  }

  public void OpenWorkshop(Texture2D texture, WorkshopItem item)
  {
    gameObject.SetActive(true);
    gameSource = GameSource.Workshop;
    workshopItem = item;
    descriptionField.text = $"<b>{workshopItem.Name}</b> - {GetWorkshopHeader(workshopItem)}\n{workshopItem.Description}";
    if (texture != null)
    {
      SetOwnedThumbnailSprite(Sprite.Create(
        texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0, 0), 100F, 0, SpriteMeshType.FullRect));
    }
    playAction = () => OnPlayTriggered(playOpts => LoadByWorkshopItem(workshopItem, playOpts));
    buildAction = () => LoadByWorkshopItem(workshopItem, new GameBuilderApplication.PlayOptions { startInBuildMode = true }, true);
    subscribeButtonText.text = GetSubscribeButtonText(workshopItem);
  }
#endif

  public void SetThumbnail(Texture2D texture)
  {
    Sprite newSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0, 0));
    if (newSprite != null)
    {
      SetOwnedThumbnailSprite(newSprite);
    }
  }

  void SetOwnedThumbnailSprite(Sprite sprite)
  {
    if (ownedThumbnailSprite == sprite) return;
    Sprite previousSprite = ownedThumbnailSprite;
    ownedThumbnailSprite = sprite;
    thumbnailImage.sprite = sprite;
    DestroyThumbnailSprite(previousSprite);
  }

  void OnDestroy()
  {
    DestroyThumbnailSprite(ownedThumbnailSprite);
    ownedThumbnailSprite = null;
  }

  static void DestroyThumbnailSprite(Sprite sprite)
  {
    if (sprite == null) return;
    if (Application.isPlaying) Destroy(sprite);
    else DestroyImmediate(sprite);
  }

  void Update()
  {
    if (gameSource != GameSource.Workshop
#if USE_STEAMWORKS
|| workshopItem == null
#endif
)
    {
      subscribeButton.gameObject.SetActive(false);
      downloadTextObject.SetActive(false);
      openButtonsParentObject.SetActive(true);
      buildButton.gameObject.SetActive(false);
    }
    else
    {
      WorkshopItemUpdate();
    }
  }

  void WorkshopItemUpdate()
  {
    subscribeButton.gameObject.SetActive(true);
    // Debug.Log(workshopItem.Name + ": " + workshopItem.IsSubscribed + " : " + workshopItem.IsInstalled + " : " + workshopItem.IsDownloading);
#if USE_STEAMWORKS
    if (workshopItem.IsSubscribed)
      subscribeButtonText.text = GetSubscribeButtonText(workshopItem);
    if (workshopItem.IsSubscribed)
    {
      if (workshopItem.IsDownloading)
      {
        downloadTextObject.SetActive(true);
        openButtonsParentObject.SetActive(false);
        downloadText.text = $"Downloading...{Mathf.FloorToInt(100 * SteamWorkshopService.Instance.GetDownloadProgress(workshopItem))}%";
      }
      else if (!workshopItem.IsReady)
      {
        downloadTextObject.SetActive(true);
        openButtonsParentObject.SetActive(false);
        downloadText.text = workshopItem.IsUpdateNeeded ? "Update available..." : "Installing...";
      }
      else
      {
        downloadTextObject.SetActive(false);
        openButtonsParentObject.SetActive(true);
        buildButton.gameObject.SetActive(true);
      }
    }
    else
#endif
    {
      downloadTextObject.SetActive(false);
      openButtonsParentObject.SetActive(false);
    }
  }

#if USE_STEAMWORKS
  string GetSubscribeButtonText(WorkshopItem item)
  {
    if (!item.IsSubscribed) return SUBSCRIBE_TEXT;
    if (!item.IsUpdateNeeded) return UNSUBSCRIBE_TEXT;
    return item.IsDownloading ? UPDATING_TEXT : UPDATE_TEXT;
  }

  void OnSubscribeButton()
  {

    if (workshopItem.IsSubscribed && !workshopItem.IsUpdateNeeded)
    {
      SteamWorkshopService.Instance.Unsubscribe(workshopItem, OnUnsubscribed);
      subscribeButtonText.text = UNSUBSCRIBING_TEXT;
    }
    else if (!workshopItem.IsDownloading)
    {
      SteamWorkshopService.Instance.Subscribe(workshopItem, OnSubscribed);
      subscribeButtonText.text = workshopItem.IsSubscribed ? UPDATING_TEXT : SUBSCRIBING_TEXT;
    }
  }

  void OnSubscribed(WorkshopItemEventArgs args)
  {
    // Debug.Log(args.ErrorMessage);
    // if (args.IsError) Debug.Log(args.ErrorMessage);
    // else Debug.Log("subbed " + args.Item.Name + " " + args.Item.IsSubscribed);
    if (args.IsError) Debug.LogError(args.ErrorMessage);

    workshopItem = args.Item;
    subscribeButtonText.text = GetSubscribeButtonText(workshopItem);
  }

  void OnUnsubscribed(WorkshopItemEventArgs args)
  {
    // Debug.Log(args.ErrorMessage);
    // if (args.IsError) Debug.Log(args.ErrorMessage);
    // else Debug.Log("ybsubbed" + args.Item.IsSubscribed);
    workshopItem = args.Item;
    subscribeButtonText.text = SUBSCRIBE_TEXT;
  }
#endif

  void OnPlayTriggered(System.Action<GameBuilderApplication.PlayOptions> playAction)
  {
    if (NetworkingController.CanDoMultiplayerMapSwitch())
    {
      bool startAsPublic;
#if USE_PUN
      startAsPublic = PhotonNetwork.room.IsVisible;
#else
      startAsPublic = GameBuilderApplication.CurrentGameOptions.playOptions.startAsPublic;
#endif
      playAction.Invoke(new GameBuilderApplication.PlayOptions
      {
        isMultiplayer = true,
        startAsPublic = startAsPublic
      });
    }
    else
    {
      popups.AskHowToPlay(playAction);
    }
  }

  void LoadLibraryBundle(GameBundleLibrary.Entry entry, GameBuilderApplication.PlayOptions playOpts)
  {
    loadingScreen.ShowAndDo(() =>
    {
      sceneController.RestartAndLoadLibraryBundle(entry, playOpts);
    });
  }

#if USE_STEAMWORKS
  void LoadByWorkshopItem(WorkshopItem item, GameBuilderApplication.PlayOptions playOpts, bool remix = false)
  {
    Debug.Assert(item.IsSubscribed, "Item is not subscribed");
    Debug.Assert(!item.IsDownloading, "Item is still downloading");
    Debug.Assert(item.IsInstalled, "Item is not installed");

    if (thumbnailImage.sprite == null)
    {
      Util.LogError($"WARNING tn sprite is null");
    }
    Texture2D tnTexture = thumbnailImage.sprite?.texture;
    loadingScreen.ShowAndDo(() =>
    {
      sceneController.LoadWorkshopItem(new LoadableWorkshopItem(item), playOpts, tnTexture, remix);
    });

  }
#endif
}
