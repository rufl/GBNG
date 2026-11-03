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
using System.Linq;
using UnityEngine;

public class MultiplayerMenu : ThumbnailMenu
{
  [SerializeField] GameObject gameThumbnailPrefab;
  [SerializeField] GameDetail gameDetailPrefab;
  [SerializeField] Texture2D spoofGameTexture;
  [SerializeField] Texture2D newGameTexture;
  [SerializeField] UnityEngine.UI.Button joinCodeButton;
  [SerializeField] UnityEngine.UI.Button refreshButton;
  [SerializeField] GameObject joinMultiplayerPopupBackground;
  [SerializeField] TextPopup joinMultiplayerPopup;

  [SerializeField] MonoBehaviour[] stuffToTempDisable;
  [SerializeField] GameObject[] objectsToTempDisable;

#if USE_PUN

  GameBuilderSceneController scenes;
  LoadingScreen loadingScreen;
  DynamicPopup dynamicPopup;
  GameDetail gameDetail;

  public static string LastJoinedRoomPrefsKey = "LAST_ROOM_TRIED_TO_JOIN";

  // HACK HACK (for debug).
  public static bool debugShortcutToMpJoin = false;

  public enum MultiplayerSort
  {
    Name,
    PlayerCount
  };

  MultiplayerSort multiplayerSort;

  void Awake()
  {
    Util.FindIfNotSet(this, ref scenes);
    Util.FindIfNotSet(this, ref loadingScreen);
    Util.FindIfNotSet(this, ref dynamicPopup);
  }

  public override void Setup()
  {
    base.Setup();

    joinCodeButton.onClick.AddListener(JoinMultiplayerPopup);
    refreshButton.onClick.AddListener(Refresh);

    joinMultiplayerPopup.confirmWithInputEvent = JoinMultiplayer;
    joinMultiplayerPopup.cancelEvent = CancelMultiplayerPopup;

    searchField.onValueChanged.AddListener((searchString) => SetSearchString(searchString));
    searchClear.onClick.AddListener(() => searchField.text = "");

    SetOpenEvent(CreateThumbnails);
    AddCategory(/* name */ null, /* placeholderText */ "");

    gameDetail = Instantiate(gameDetailPrefab).GetComponent<GameDetail>();
  }

  void Start()
  {
    if (debugShortcutToMpJoin)
    {
      JoinMultiplayerPopup();
      debugShortcutToMpJoin = false;
    }
  }

  IEnumerator OpenRoutine()
  {
    if (!InLobby())
    {
      if (PhotonNetwork.connected)
      {
        PhotonNetwork.Disconnect();
        yield return null;
      }
      PhotonNetwork.autoJoinLobby = true;
      PhotonNetwork.ConnectToBestCloudServer(NetworkingController.GetPhotonGameVersion());
      while (!InLobby())
      {
        // TODO some connecting feedback..
        yield return null;
      }
    }

    Refresh();

    // Keep auto-refreshing until we get SOMETHING
    while (PhotonNetwork.GetRoomList() == null || PhotonNetwork.GetRoomList().Length == 0)
    {
      yield return new WaitForSecondsRealtime(1f);
      Refresh();
    }
  }

  private void Refresh()
  {
    ClearThumbnails();
    CreateThumbnails();
  }

  bool InLobby()
  {
    return PhotonNetwork.connected && !PhotonNetwork.offlineMode && PhotonNetwork.insideLobby;
  }

  public override void Open()
  {
    Util.Log($"Photon state: {PhotonUtil.GetPhotonStateString()}");
    sortText.text = multiplayerSort.ToString();
    base.Open();
    StartCoroutine(OpenRoutine());
  }

  protected override void ChangeSort(Direction direction)
  {
    multiplayerSort = (MultiplayerSort)(1 - (int)multiplayerSort);
    sortText.text = multiplayerSort.ToString();
    UpdateSort();
  }

  bool IsRoomHidden(string roomName)
  {
    // (we can use this to hide rooms known to be broken/incompatible)
    return false;
  }

  protected void CreateThumbnails()
  {
    AddNewGameThumbnail();

    if (!InLobby())
    {
      return;
    }

    foreach (var room in PhotonNetwork.GetRoomList())
    {
      // Some games are broken and crash/disconnect on join..so hide these.
      if (IsRoomHidden(room.Name))
      {
        continue;
      }

      AddRoomThumbnail(room);
    }
  }


  void AddNewGameThumbnail()
  {
    string gameName = "New game";

    GameThumbnail thumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
    thumbnail.SetThumbnail(newGameTexture);
    thumbnail.SetGameSource(GameDetail.GameSource.Local);
    thumbnail.SetName(gameName);
    thumbnail.OnClick = OpenNew;
    thumbnail.GetWriteTime = () => { return System.DateTime.Today; }; // Not relevant
    thumbnail.GetDescription = () => { return "Start a new multiplayer game"; };
    thumbnail.GetPlayerCount = () => { return 0; };
    AddThumbnail(thumbnail);
  }

  void OpenNew()
  {
    dynamicPopup.AskHowToPlayMultiplayer(playOpts =>
    {
      var gameOpts = new GameBuilderApplication.GameOptions { playOptions = playOpts };
      loadingScreen.ShowAndDo(() => sceneController.RestartAndLoadMinimalScene(gameOpts));
    });
  }

  void AddRoomThumbnail(RoomInfo room)
  {
#if USE_PUN
    string gameName = "<no name>";
    Texture2D tnTexture = spoofGameTexture;

    if (room.CustomProperties.ContainsKey(NetworkingController.GameDisplayNameRoomProperty))
    {
      gameName = (string)room.CustomProperties[NetworkingController.GameDisplayNameRoomProperty];
    }

    if (room.CustomProperties.ContainsKey(NetworkingController.ThumbnailZippedJpegRoomProperty))
    {
      try
      {
        byte[] tnBytes = (byte[])room.CustomProperties[NetworkingController.ThumbnailZippedJpegRoomProperty];
        if (tnBytes != null)
        {
          tnTexture = Util.ZippedJpegToTexture2D(tnBytes);
        }
      }
      catch (System.Exception)
      {
        // Wah wah.
      }
    }

    GameThumbnail thumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
    thumbnail.SetThumbnail(tnTexture);
    thumbnail.SetGameSource(GameDetail.GameSource.Multiplayer);
    thumbnail.SetName(gameName);
    thumbnail.OnClick = () => ShowRoomDetails(thumbnail, room.Name);
    thumbnail.GetWriteTime = () => { return System.DateTime.Today; }; // Not relevant
    thumbnail.GetDescription = () => { return "no description"; };
    thumbnail.GetPlayerCount = () => { return room.PlayerCount; };
    AddThumbnail(thumbnail);
#endif
  }

  void ShowRoomDetails(GameThumbnail newThumbnail, string roomCode)
  {
    string detailCopy = $"<b>{newThumbnail.GetName()}</b> - {newThumbnail.GetPlayerCount()} player(s)";
    SelectThumbnail(newThumbnail, (rect) =>
    {
      gameDetail.FitTo(rect);
      gameDetail.OpenSpecial(detailCopy, newThumbnail.GetTexture(), playOpts =>
      {
        // TODO loading screen here?
        scenes.LoadMainSceneAsync(new GameBuilderApplication.GameOptions
        {
          playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true },
          joinCode = roomCode
        });
      }, true);
    });
  }

  protected void UpdateSort()
  {
    if (multiplayerSort == MultiplayerSort.Name)
    {
      SetSorting((t1, t2) =>
      {
        return t1.GetName().CompareTo(t2.GetName());
      });
    }
    else
    {
      SetSorting((t1, t2) =>
      {
        return t2.GetPlayerCount().CompareTo(t1.GetPlayerCount());
      });
    }
  }

  public override void Close()
  {
    if (joinMultiplayerPopup.IsActive())
    {
      CancelMultiplayerPopup();
    }
    else
    {
      base.Close();
    }
  }

  public void JoinMultiplayerPopup()
  {
    joinMultiplayerPopup.Activate();
    joinMultiplayerPopupBackground.SetActive(true);
    if (PlayerPrefs.HasKey(LastJoinedRoomPrefsKey))
    {
      joinMultiplayerPopup.SetInputFieldText(PlayerPrefs.GetString(LastJoinedRoomPrefsKey));
    }
    else
    {
#if UNITY_EDITOR
      joinMultiplayerPopup.SetInputFieldText(NetworkingController.GenerateUniqueRoomName());
#endif
    }
  }

  void JoinMultiplayer(string joinCode)
  {
    joinCode = joinCode.Trim('\n', '\r', ' ', '\t');
    Util.Log($"Trying to join room {joinCode}..");
    PlayerPrefs.SetString(LastJoinedRoomPrefsKey, joinCode);
    PlayerPrefs.Save();
    loadingScreen.ShowAndDo(() =>
    {
      sceneController.JoinMultiplayerGameByCode(joinCode);
    });
  }

  public void CancelMultiplayerPopup()
  {
    joinMultiplayerPopupBackground.SetActive(false);

    joinMultiplayerPopup.Deactivate();
  }
#else
  GameBuilderSceneController scenes;
  LoadingScreen loadingScreen;
  int lastDiscoveryRevision = -1;
  int lastPublicSessionRevision = -1;
  float nextDiscoveryRefresh;

  public override void Setup()
  {
    base.Setup();
    Util.FindIfNotSet(this, ref scenes);
    Util.FindIfNotSet(this, ref loadingScreen);
    joinCodeButton.onClick.AddListener(JoinMultiplayerPopup);
    joinMultiplayerPopup.confirmWithInputEvent = JoinMultiplayer;
    joinMultiplayerPopup.cancelEvent = CancelMultiplayerPopup;
    DirectHostDiscovery.BeginBrowsing();
    DirectSessionDirectory.BeginBrowsing();
    lastDiscoveryRevision = DirectHostDiscovery.Revision;
    lastPublicSessionRevision = DirectSessionDirectory.Revision;
    refreshButton.gameObject.SetActive(true);

    TMPro.TMP_Text joinLabel = joinCodeButton.GetComponentInChildren<TMPro.TMP_Text>(true);
    if (joinLabel != null)
    {
      joinLabel.text = "Join by address";
    }

    SetOpenEvent(CreateThumbnails);
    AddCategory(null, "");
  }

  void Refresh()
  {
    DirectSessionDirectory.RefreshNow();
    RebuildThumbnails();
  }

  void RebuildThumbnails()
  {
    ClearThumbnails();
    CreateThumbnails();
  }

  protected override void Update()
  {
    if (!gameObject.activeInHierarchy || Time.unscaledTime < nextDiscoveryRefresh) return;
    nextDiscoveryRefresh = Time.unscaledTime + 0.5f;
    int discoveryRevision = DirectHostDiscovery.Revision;
    int publicSessionRevision = DirectSessionDirectory.Revision;
    if (discoveryRevision == lastDiscoveryRevision && publicSessionRevision == lastPublicSessionRevision) return;
    lastDiscoveryRevision = discoveryRevision;
    lastPublicSessionRevision = publicSessionRevision;
    RebuildThumbnails();
  }

  void CreateThumbnails()
  {
    GameThumbnail thumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
    thumbnail.SetThumbnail(newGameTexture);
    thumbnail.SetGameSource(GameDetail.GameSource.Local);
    thumbnail.SetName("Host a game");
    thumbnail.OnClick = OpenNew;
    thumbnail.GetWriteTime = () => System.DateTime.Today;
    thumbnail.GetDescription = () => "Host a free direct-connect multiplayer game";
    thumbnail.GetPlayerCount = () => 1;
    AddThumbnail(thumbnail);
    foreach (DirectHostDiscovery.HostInfo host in DirectHostDiscovery.GetDiscoveredHosts())
    {
      GameThumbnail discovered = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
      discovered.SetThumbnail(spoofGameTexture);
      discovered.SetGameSource(GameDetail.GameSource.Multiplayer);
      discovered.SetName(host.Name);
      discovered.OnClick = () => JoinDiscoveredHost(host);
      discovered.GetWriteTime = () => System.DateTime.Today;
      discovered.GetDescription = () => $"{host.PlayerCount}/{host.MaxPlayers} players - {host.Address}:{host.Port}";
      discovered.GetPlayerCount = () => host.PlayerCount;
      AddThumbnail(discovered);
    }
    DirectSessionDirectory.SessionInfo[] publicSessions = DirectSessionDirectory.GetPublicSessions();
    if (!DirectSessionDirectory.IsConfigured)
    {
      AddPublicStatusThumbnail("Public list not configured", DirectSessionDirectory.StatusMessage);
    }
    else if (publicSessions.Length == 0)
    {
      string status = DirectSessionDirectory.StatusMessage;
      string title = status == "Public directory connected."
        ? "No public games online"
        : "Public list unavailable";
      AddPublicStatusThumbnail(title, status + " Public hosts must forward UDP port 7777.");
    }
    else
    {
      foreach (DirectSessionDirectory.SessionInfo session in publicSessions)
      {
        DirectHostDiscovery.HostInfo host = new DirectHostDiscovery.HostInfo
        {
          Address = session.Address,
          Port = (ushort)session.Port,
          Name = session.Name,
          PlayerCount = session.PlayerCount,
          MaxPlayers = session.MaxPlayers
        };
        GameThumbnail publicThumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
        publicThumbnail.SetThumbnail(spoofGameTexture);
        publicThumbnail.SetGameSource(GameDetail.GameSource.Multiplayer);
        publicThumbnail.SetName(session.Name);
        publicThumbnail.OnClick = () => JoinDiscoveredHost(host);
        publicThumbnail.GetWriteTime = () => System.DateTime.Today;
        publicThumbnail.GetDescription = () => $"{session.PlayerCount}/{session.MaxPlayers} players - {session.Address}:{session.Port} (public)";
        publicThumbnail.GetPlayerCount = () => session.PlayerCount;
        AddThumbnail(publicThumbnail);
      }
    }
  }

  void AddPublicStatusThumbnail(string title, string description)
  {
    GameThumbnail status = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
    status.SetThumbnail(spoofGameTexture);
    status.SetGameSource(GameDetail.GameSource.Multiplayer);
    status.SetName(title);
    status.OnClick = () => { };
    status.GetWriteTime = () => System.DateTime.Today;
    status.GetDescription = () => description;
    status.GetPlayerCount = () => 0;
    AddThumbnail(status);
  }

  void JoinDiscoveredHost(DirectHostDiscovery.HostInfo host)
  {
    string addressAndPort = $"{host.Address}:{host.Port}";
    PlayerPrefs.SetString(LastJoinedRoomPrefsKey, addressAndPort);
    PlayerPrefs.Save();
    var options = new GameBuilderApplication.GameOptions
    {
      joinAddress = host.Address,
      joinPort = host.Port,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    loadingScreen.ShowAndDo(() => scenes.LoadMainSceneAsync(options));
  }

  void OpenNew()
  {
    var options = new GameBuilderApplication.GameOptions
    {
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    loadingScreen.ShowAndDo(() => scenes.RestartAndLoadMinimalScene(options));
  }

  public void JoinMultiplayerPopup()
  {
    joinMultiplayerPopup.Activate();
    joinMultiplayerPopupBackground.SetActive(true);
    joinMultiplayerPopup.SetInputFieldText(PlayerPrefs.GetString(
      LastJoinedRoomPrefsKey,
      $"127.0.0.1:{NetworkingController.DefaultDirectHostPort}"));
  }

  void JoinMultiplayer(string addressAndPort)
  {
    addressAndPort = addressAndPort.Trim();
    int separator = addressAndPort.LastIndexOf(':');
    string address = separator < 0 ? addressAndPort : addressAndPort.Substring(0, separator);
    ushort port = NetworkingController.DefaultDirectHostPort;

    if (separator >= 0
      && (!ushort.TryParse(addressAndPort.Substring(separator + 1), out port) || port == 0))
    {
      Util.LogError($"Invalid direct-host port in '{addressAndPort}'.");
      return;
    }

    if (System.Net.IPAddress.TryParse(address, out System.Net.IPAddress parsedAddress))
    {
      if (parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
      {
        Util.LogError("Direct-host connections require an IPv4 address or a hostname with an IPv4 record.");
        return;
      }
      JoinMultiplayerByAddress(parsedAddress.ToString(), port, addressAndPort);
      return;
    }

    if (string.IsNullOrWhiteSpace(address))
    {
      Util.LogError("Enter an IPv4 address or a hostname, optionally followed by :port.");
      return;
    }
    StartCoroutine(ResolveAndJoinHost(address, port, addressAndPort));
  }

  System.Collections.IEnumerator ResolveAndJoinHost(string hostName, ushort port, string originalAddress)
  {
    System.Threading.Tasks.Task<System.Net.IPAddress[]> resolution;
    try
    {
      resolution = System.Net.Dns.GetHostAddressesAsync(hostName);
    }
    catch (System.Exception e)
    {
      Util.LogError($"Could not resolve host '{hostName}': {e.Message}");
      yield break;
    }

    while (!resolution.IsCompleted) yield return null;
    if (resolution.IsCanceled || resolution.IsFaulted)
    {
      string detail = resolution.Exception == null ? "name lookup was cancelled" : resolution.Exception.GetBaseException().Message;
      Util.LogError($"Could not resolve host '{hostName}': {detail}");
      yield break;
    }

    System.Net.IPAddress resolvedAddress = null;
    foreach (System.Net.IPAddress candidate in resolution.Result)
    {
      if (candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
      {
        resolvedAddress = candidate;
        break;
      }
    }
    if (resolvedAddress == null)
    {
      Util.LogError($"Host '{hostName}' has no IPv4 address.");
      yield break;
    }
    JoinMultiplayerByAddress(resolvedAddress.ToString(), port, originalAddress);
  }

  void JoinMultiplayerByAddress(string address, ushort port, string savedAddress)
  {
    PlayerPrefs.SetString(LastJoinedRoomPrefsKey, savedAddress);
    PlayerPrefs.Save();
    CancelMultiplayerPopup();
    var options = new GameBuilderApplication.GameOptions
    {
      joinAddress = address,
      joinPort = port,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    loadingScreen.ShowAndDo(() => scenes.LoadMainSceneAsync(options));
  }

  public void CancelMultiplayerPopup()
  {
    joinMultiplayerPopupBackground.SetActive(false);
    joinMultiplayerPopup.Deactivate();
  }

  protected override void ChangeSort(Direction direction)
  {
  }
#endif
}
