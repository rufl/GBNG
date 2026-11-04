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
  public enum MultiplayerSort
  {
    Name,
    PlayerCount
  };

  MultiplayerSort multiplayerSort;


#if USE_PUN

  GameBuilderSceneController scenes;
  LoadingScreen loadingScreen;
  DynamicPopup dynamicPopup;
  GameDetail gameDetail;

  public static string LastJoinedRoomPrefsKey = "LAST_ROOM_TRIED_TO_JOIN";

  // HACK HACK (for debug).
  public static bool debugShortcutToMpJoin = false;


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
  public static string LastJoinedRoomPrefsKey = "LAST_DIRECT_HOST_ADDRESS";
  int lastDiscoveryRevision = -1;
  int lastPublicSessionRevision = -1;
  float nextDiscoveryRefresh;

  public override void Setup()
  {
    base.Setup();
    searchField.onValueChanged.AddListener((searchString) => SetSearchString(searchString));
    searchClear.onClick.AddListener(() => searchField.text = "");
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
    sortText.text = multiplayerSort.ToString();
    UpdateSort();
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

    GameThumbnail ipv6Thumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
    ipv6Thumbnail.SetThumbnail(newGameTexture);
    ipv6Thumbnail.SetGameSource(GameDetail.GameSource.Local);
    ipv6Thumbnail.SetName("Host IPv6 game");
    ipv6Thumbnail.OnClick = OpenNewIpv6;
    ipv6Thumbnail.GetWriteTime = () => System.DateTime.Today;
    ipv6Thumbnail.GetDescription = () => "IPv6-only direct host; share the address manually";
    ipv6Thumbnail.GetPlayerCount = () => 1;
    AddThumbnail(ipv6Thumbnail);

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
      AddPublicStatusThumbnail(title, status + " Automatic UDP port mapping is attempted; if it fails, forward UDP port 7777 and allow Game Builder through your firewall.");
    }
    else
    {
      foreach (DirectSessionDirectory.SessionInfo session in publicSessions)
      {
        Texture2D thumbnailTexture = spoofGameTexture;
        string thumbnailBase64 = session.ThumbnailZippedJpegBase64;
        int maxEncodedThumbnailChars = ((DirectSessionDirectory.MaxPublicThumbnailBytes + 2) / 3) * 4;
        if (!string.IsNullOrEmpty(thumbnailBase64) && thumbnailBase64.Length <= maxEncodedThumbnailChars)
        {
          try
          {
            byte[] thumbnailBytes = System.Convert.FromBase64String(thumbnailBase64);
            if (thumbnailBytes.Length > 0 && thumbnailBytes.Length <= DirectSessionDirectory.MaxPublicThumbnailBytes)
              thumbnailTexture = Util.ZippedJpegToTexture2D(thumbnailBytes);
          }
          catch (System.Exception)
          {
            // Keep the generic multiplayer image for malformed thumbnails.
          }
        }
        DirectHostDiscovery.HostInfo host = new DirectHostDiscovery.HostInfo
        {
          Address = session.Address,
          Port = (ushort)session.Port,
          Name = session.Name,
          PlayerCount = session.PlayerCount,
          MaxPlayers = session.MaxPlayers,
          ServerCertificatePem = session.ServerCertificatePem
        };
        GameThumbnail publicThumbnail = Instantiate(gameThumbnailPrefab).GetComponent<GameThumbnail>();
        publicThumbnail.SetThumbnail(thumbnailTexture);
        publicThumbnail.SetGameSource(GameDetail.GameSource.Multiplayer);
        publicThumbnail.SetName(session.Name);
        publicThumbnail.OnClick = () => JoinDiscoveredHost(host);
        publicThumbnail.GetWriteTime = () => System.DateTime.Today;
        publicThumbnail.GetDescription = () => $"{session.PlayerCount}/{session.MaxPlayers} players - {FormatAddressAndPort(session.Address, session.Port)} (public)";
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

  static string FormatAddressAndPort(string address, int port)
  {
    System.Net.IPAddress parsedAddress;
    if (System.Net.IPAddress.TryParse(address, out parsedAddress)
      && parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
      return $"[{parsedAddress}]:{port}";
    }
    return $"{address}:{port}";
  }

  void JoinDiscoveredHost(DirectHostDiscovery.HostInfo host)
  {
    if (!DirectHostTls.IsUsablePinnedCertificate(host.ServerCertificatePem))
    {
      Util.LogError("This discovered host has no valid TLS certificate pin and cannot be joined securely.");
      return;
    }
    string invite = DirectHostTls.CreateInvite(host.Address, host.Port, host.ServerCertificatePem);
    PlayerPrefs.SetString(LastJoinedRoomPrefsKey, invite);
    PlayerPrefs.Save();
    var options = new GameBuilderApplication.GameOptions
    {
      joinAddress = host.Address,
      joinPort = host.Port,
      directHostUsesIpv6 = System.Net.IPAddress.TryParse(host.Address, out System.Net.IPAddress parsedPublicAddress)
        && parsedPublicAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
      trustedHostCertificatePem = host.ServerCertificatePem,
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

  void OpenNewIpv6()
  {
    var options = new GameBuilderApplication.GameOptions
    {
      directHostUsesIpv6 = true,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    loadingScreen.ShowAndDo(() => scenes.RestartAndLoadMinimalScene(options));
  }


  public void JoinMultiplayerPopup()
  {
    joinMultiplayerPopup.Activate();
    joinMultiplayerPopupBackground.SetActive(true);
    joinMultiplayerPopup.SetInputFieldText(PlayerPrefs.GetString(LastJoinedRoomPrefsKey, string.Empty));
  }

  static bool TryParseDirectHostAddress(string input, out string address, out ushort port)
  {
    address = null;
    port = NetworkingController.DefaultDirectHostPort;
    if (string.IsNullOrWhiteSpace(input)) return false;

    input = input.Trim();
    if (input[0] == '[')
    {
      int closingBracket = input.IndexOf(']');
      if (closingBracket <= 1) return false;

      address = input.Substring(1, closingBracket - 1);
      string suffix = input.Substring(closingBracket + 1);
      if (suffix.Length > 0
        && (suffix[0] != ':' || !ushort.TryParse(suffix.Substring(1), out port) || port == 0))
      {
        return false;
      }

      return System.Net.IPAddress.TryParse(address, out System.Net.IPAddress bracketedAddress)
        && bracketedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }

    if (System.Net.IPAddress.TryParse(input, out System.Net.IPAddress parsedAddress))
    {
      if (parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
        && parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
      {
        return false;
      }
      address = parsedAddress.ToString();
      return true;
    }

    int separator = input.LastIndexOf(':');
    if (separator >= 0)
    {
      if (input.IndexOf(':') != separator
        || !ushort.TryParse(input.Substring(separator + 1), out port)
        || port == 0)
      {
        return false;
      }
      address = input.Substring(0, separator);
    }
    else
    {
      address = input;
    }

    return !string.IsNullOrWhiteSpace(address);
  }
  static bool TryParseDirectHostInvite(
    string input,
    out string address,
    out ushort port,
    out string certificatePem)
  {
    address = null;
    port = NetworkingController.DefaultDirectHostPort;
    certificatePem = null;
    if (string.IsNullOrWhiteSpace(input)) return false;
    string[] parts = input.Trim().Split('|');
    if (parts.Length != 4 || parts[0] != DirectHostTls.InvitePrefix
      || !ushort.TryParse(
        parts[2],
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out port)
      || port == 0)
      return false;
    if (!TryParseDirectHostAddress(parts[1], out address, out ushort embeddedPort)
      || embeddedPort != NetworkingController.DefaultDirectHostPort)
      return false;
    return DirectHostTls.TryDecodeCertificateFromInvite(parts[3], out certificatePem);
  }

  static System.Net.IPAddress SelectDirectHostAddress(System.Net.IPAddress[] candidates)
  {
    System.Net.IPAddress firstIpv6 = null;
    foreach (System.Net.IPAddress candidate in candidates)
    {
      if (candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return candidate;
      if (firstIpv6 == null && candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
      {
        firstIpv6 = candidate;
      }
    }
    return firstIpv6;
  }

  void JoinMultiplayer(string inviteText)
  {
    if (!TryParseDirectHostInvite(inviteText, out string address, out ushort port, out string certificatePem))
    {
      if (TryParseDirectHostAddress(inviteText, out _, out _))
        Util.LogError("Direct-host connections require encryption. Paste the complete GBDIRECT1 invite copied from the host; raw addresses are not accepted.");
      else
        Util.LogError("Paste the complete GBDIRECT1 invite copied from the host.");
      return;
    }

    string invite = inviteText.Trim();
    if (System.Net.IPAddress.TryParse(address, out System.Net.IPAddress parsedAddress))
    {
      JoinMultiplayerByAddress(parsedAddress.ToString(), port, certificatePem, invite);
      return;
    }

    StartCoroutine(ResolveAndJoinHost(address, port, certificatePem, invite));
  }

  System.Collections.IEnumerator ResolveAndJoinHost(string hostName, ushort port, string certificatePem, string originalInvite)
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

    System.Net.IPAddress resolvedAddress = SelectDirectHostAddress(resolution.Result);
    if (resolvedAddress == null)
    {
      Util.LogError($"Host '{hostName}' has no IPv4 or IPv6 address.");
      yield break;
    }
    JoinMultiplayerByAddress(resolvedAddress.ToString(), port, certificatePem, originalInvite);
  }


  void JoinMultiplayerByAddress(string address, ushort port, string certificatePem, string savedInvite)
  {
    PlayerPrefs.SetString(LastJoinedRoomPrefsKey, savedInvite);
    PlayerPrefs.Save();
    CancelMultiplayerPopup();
    var options = new GameBuilderApplication.GameOptions
    {
      joinAddress = address,
      joinPort = port,
      directHostUsesIpv6 = System.Net.IPAddress.TryParse(address, out System.Net.IPAddress parsedDirectAddress)
        && parsedDirectAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
      trustedHostCertificatePem = certificatePem,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    loadingScreen.ShowAndDo(() => scenes.LoadMainSceneAsync(options));
  }

  public void CancelMultiplayerPopup()
  {
    joinMultiplayerPopupBackground.SetActive(false);
    joinMultiplayerPopup.Deactivate();
  }

#endif
  protected override void ChangeSort(Direction direction)
  {
    multiplayerSort = (MultiplayerSort)(1 - (int)multiplayerSort);
    sortText.text = multiplayerSort.ToString();
    UpdateSort();
  }

  void UpdateSort()
  {
    if (multiplayerSort == MultiplayerSort.Name)
    {
      SetSorting((t1, t2) => t1.GetName().CompareTo(t2.GetName()));
    }
    else
    {
      SetSorting((t1, t2) => t2.GetPlayerCount().CompareTo(t1.GetPlayerCount()));
    }
  }
}
