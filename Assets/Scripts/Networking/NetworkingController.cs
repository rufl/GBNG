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

using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using GameBuilder;
using System.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

// Based off of Photon's utility script.
// TODO probably should rename this to just MainController...but also cordone off some of the networking-specific stuff into a new utility.
public class NetworkingController : Photon.PunBehaviour
{
  // NGO caps each reliable-fragmented message at 64,000 bytes; leave room for framing.
  const int MaximumPlayerInitKbs = 400;
  const int DirectHostInitChunkBytes = 48 * 1024;
  public const ushort DefaultDirectHostPort = 7777;
  const string DirectHostInitMessageName = "GB_DIRECT_HOST_INIT_V1";
  const string DirectPlayerIdentityMessageName = "GB_DIRECT_PLAYER_IDENTITY_V1";
  const string DirectPlayerAssignmentMessageName = "GB_DIRECT_PLAYER_ASSIGNMENT_V1";
  const string DirectPlayerReconnectMessageName = "GB_DIRECT_PLAYER_RECONNECT_V1";
  const string DirectSessionMigrationTicketMessageName = "GB_DIRECT_SESSION_MIGRATION_TICKET_V1";
  const string DirectPlayerInitRequestMessageName = "GB_DIRECT_PLAYER_INIT_REQUEST_V1";
  const string DirectPlayerReadyMessageName = "GB_DIRECT_PLAYER_READY_V1";
  const string DirectPlayerSpawnMessageName = "GB_DIRECT_PLAYER_SPAWN_V1";
  const string DirectPlayerDespawnMessageName = "GB_DIRECT_PLAYER_DESPAWN_V1";
#if !USE_STEAMWORKS
  const string DirectWorkshopAssetRequestMessageName = "GB_DIRECT_WORKSHOP_ASSET_REQUEST_V1";
  const string DirectWorkshopAssetResponseMessageName = "GB_DIRECT_WORKSHOP_ASSET_RESPONSE_V1";
  const int DirectWorkshopAssetChunkBytes = 48 * 1024;
  const int MaximumDirectWorkshopAssetBytes = 64 * 1024 * 1024;
  const float DirectWorkshopAssetRequestTimeoutSeconds = 30f;
#endif
  NetworkManager directNetworkManager;
  UnityTransport directTransport;
  DirectHostRpcRouter directRpcRouter;
  bool registeredDirectInitHandler;
  bool registeredDirectPlayerHandlers;
  bool directClientAssignmentReceived;
  bool directWorldReady;
  bool directHasMigrationTicket;
  bool directMigrationCandidate;
  bool directMigrationAbandoned;
  bool directMigrationReconnectPending;
  int directDisconnectedLocalPlayerId;
  DirectSessionDirectory.MigrationTicket directMigrationTicket;
  string lastBroadcastMigrationSessionId;
  string lastBroadcastMigrationToken;
  string lastBroadcastMigrationName;
  int lastBroadcastMigrationGeneration;
  bool lastBroadcastMigrationIsPublic;
  readonly HashSet<ulong> directInitResponseClients = new HashSet<ulong>();
  readonly HashSet<ulong> directClientsAwaitingReconnect = new HashSet<ulong>();
  readonly DirectHostInitChunkAssembler directHostInitAssembler = new DirectHostInitChunkAssembler();
#if !USE_STEAMWORKS
  readonly Dictionary<ulong, List<System.Action<Util.Maybe<string>>>> directWorkshopAssetCallbacks =
    new Dictionary<ulong, List<System.Action<Util.Maybe<string>>>>();
  readonly Dictionary<ulong, DirectWorkshopAssetAssembler> directWorkshopAssetAssemblers =
    new Dictionary<ulong, DirectWorkshopAssetAssembler>();
  readonly Dictionary<ulong, float> directWorkshopAssetDeadlines = new Dictionary<ulong, float>();
  readonly List<ulong> expiredDirectWorkshopAssetIds = new List<ulong>();
  readonly List<List<System.Action<Util.Maybe<string>>>> failedDirectWorkshopAssetCallbacks =
    new List<List<System.Action<Util.Maybe<string>>>>();
#endif
  string directHostShareAddress;
  string directHostShareAddressAddress;
  ushort directHostShareAddressPort;
  byte[] directHostPasswordBytes;

  public static string SteamIdPlayerProperty = "SteamID";
  public static string SteamProfileNamePlayerProperty = "SteamProfileName";

  public static string GameDisplayNameRoomProperty = "gameName";
  public static string ThumbnailZippedJpegRoomProperty = "thumbnailZippedJpeg";
  static string[] PropertiesToShowInLobby = new string[] { GameDisplayNameRoomProperty, ThumbnailZippedJpegRoomProperty };

  static string BadJoinCodeMessage = "Sorry, it looks like you typed an invalid join code. It should look something like 1-123456.";
  static char JoinCodeSeparator = '-';

  public static string LastJoinedRoomPrefKey = "LAST_ROOM_JOINED";

  public interface LocalUserObjects
  {
    void Initialize(UserBody localBodyInstance);
  }


  // This prefab will be spawned for both the local and remote players.
  public UserBody userBodyPrefab;

  // This will be spawned when the world is ready for the player.
  // If it implements LocalObjects, Initialize will be called.
  public GameObject localPrefab;

  public GameObject disableOnJoinRoom;

  public VoosEngine voosEngine;
  public BehaviorSystem behaviorSystem;
  public SaveLoadController saveLoad;
  public GameBundleLibrary gameBundleLibrary;
  public GameBuilderSceneController scenes;
  public GameBuilderStage stage;
  public AutoSaveController autosaves;

  [SerializeField] AssetCache assetCache;
  TerrainManager terrain;
  BuiltinPrefabLibrary builtinPrefabLibrary;
  SojoSystem sojoSystem;
  SceneActorLibrary sceneActorLibrary;
  DynamicPopup popups;
  WorkshopAssetSource workshop;

  private GameObject localUserInstance;
  readonly Dictionary<int, UserBody> directRemoteBodies = new Dictionary<int, UserBody>();

  public enum Mode
  {
    Online,
    Offline
  }

  public Mode mode = Mode.Online;

  string lastRoomNameAttempted = null;
  string lastRoomJoined = null;

  bool wasMasterWhenConnected = false;
  int numOthersWhenConnected = 0;
  string roomNameWhenConnected = null;

  bool receivedPlayerInitPayload = false;
  bool wasDirectlyKicked = false;

  bool didSwitchMaps = false;

  float onlineSeconds = 0f;

  HashSet<string> kickedPlayerNickNames = new HashSet<string>();

  // The player init RPC is pretty big. So avoid sending it too often, if a
  // bunch of players log in at the same time, for example (which can def happen
  // on map switch!). So have a queue, and pop one off per second.
  Queue<PhotonPlayer> playerInitQueue = new Queue<PhotonPlayer>();

  LoadingScreen loadingScreen;

  // Internal state machine
  enum State
  {
    Uninited,
    Playing,
    SwitchingScene,
    Rejoining
  }
  State state = State.Uninited;

  [System.Serializable]
  struct PlayerLeftMessage
  {
    public int id;
    public string nickName;
  }

  public bool GetIsInMultiplayer()
  {
#if USE_PUN
    return mode == Mode.Online && PhotonUtil.ActuallyConnected();
#else
    return mode == Mode.Online
      && directNetworkManager != null
      && directNetworkManager.IsConnectedClient;
#endif
  }

  public bool IsDirectHost()
  {
#if USE_PUN
    return PhotonNetwork.isMasterClient;
#else
    return directNetworkManager != null && directNetworkManager.IsHost;
#endif
  }

  public static bool IsDirectIpv6Host
  {
    get
    {
      GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
      return options.playOptions.isMultiplayer
        && string.IsNullOrEmpty(options.joinAddress)
        && options.directHostUsesIpv6;
    }
  }


  public string GetJoinedRoomName()
  {
#if USE_PUN
    return PhotonNetwork.room.name;
#else
    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    ushort port = options.joinPort == 0 ? DefaultDirectHostPort : options.joinPort;
    if (!options.playOptions.isMultiplayer || directNetworkManager == null || !directNetworkManager.IsListening)
      return string.Empty;
    if (!IsDirectHost())
    {
      directHostShareAddress = CreateDirectHostInvite(
        options.joinAddress,
        port,
        options.trustedHostCertificatePem,
        options.directRelaySessionId,
        options.directRelayAddress,
        options.directRelayPort);
      return directHostShareAddress;
    }

    if (options.directHostUsesIpv6)
    {
      if (IPAddress.TryParse(DirectSessionDirectory.HostPublicAddress, out IPAddress publicIpv6)
        && IsUsableDirectIpv6Address(publicIpv6))
        return GetOrCreateDirectHostShareAddress(publicIpv6.ToString(), port);
      return GetDirectIpv6HostShareAddress(port);
    }

    if (IPAddress.TryParse(DirectSessionDirectory.HostPublicAddress, out IPAddress publicIpv4)
      && publicIpv4.AddressFamily == AddressFamily.InterNetwork)
    {
      ushort sharePort = IsDirectPublicIpv4Address(publicIpv4)
        ? DirectHostPortMapper.PublicPort
        : port;
      return GetOrCreateDirectHostShareAddress(publicIpv4.ToString(), sharePort);
    }



    string fallback = "127.0.0.1";
    try
    {
      using (Socket routeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
      {
        routeSocket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), DefaultDirectHostPort));
        IPAddress localAddress = ((IPEndPoint)routeSocket.LocalEndPoint).Address;
        if (localAddress.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(localAddress))
        {
          return GetOrCreateDirectHostShareAddress(localAddress.ToString(), port);
        }
      }
    }
    catch (SocketException)
    {
      // Fall back to an interface address when there is no default route.
    }

    foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
      if (networkInterface.OperationalStatus != OperationalStatus.Up)
      {
        continue;
      }

      IPInterfaceProperties properties = networkInterface.GetIPProperties();
      bool hasGateway = properties.GatewayAddresses.Count > 0;
      foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
      {
        IPAddress address = unicast.Address;
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
          continue;
        }

        byte[] octets = address.GetAddressBytes();
        bool isPrivate = octets[0] == 10
          || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
          || (octets[0] == 192 && octets[1] == 168);
        if (!isPrivate)
        {
          continue;
        }

        if (hasGateway)
        {
          return GetOrCreateDirectHostShareAddress(address.ToString(), port);
        }
        fallback = address.ToString();
      }
    }

    return GetOrCreateDirectHostShareAddress(fallback, port);
#endif
  }
  string GetOrCreateDirectHostShareAddress(string address, ushort port)
  {
    string invite = CreateDirectHostInvite(address, port, null, null, null, 0);
    if (directHostShareAddress.IsNullOrEmpty()
      || directHostShareAddressAddress != address
      || directHostShareAddressPort != port
      || directHostShareAddress != invite)
    {
      directHostShareAddressAddress = address;
      directHostShareAddressPort = port;
      directHostShareAddress = invite;
    }
    return directHostShareAddress;
  }

  static bool IsDirectPublicIpv4Address(IPAddress address)
  {
    byte[] octets = address.GetAddressBytes();
    return !IPAddress.IsLoopback(address)
      && octets[0] != 0
      && octets[0] < 224
      && !(octets[0] == 10)
      && !(octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
      && !(octets[0] == 192 && octets[1] == 168)
      && !(octets[0] == 169 && octets[1] == 254)
      && !(octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127);
  }

  string CreateDirectHostInvite(
    string address,
    ushort port,
    string certificatePem,
    string relaySessionId,
    string relayAddress,
    ushort relayPort)
  {
#if !USE_PUN
    if (IsDirectHost()
      && DirectSessionDirectory.TryGetHostRelayCredentials(
        out string hostSessionId,
        out _,
        out string hostRelayAddress,
        out ushort hostRelayPort))
    {
      relaySessionId = hostSessionId;
      relayAddress = hostRelayAddress;
      relayPort = hostRelayPort;
    }
#endif
    string certificate = string.IsNullOrEmpty(certificatePem)
      ? DirectHostTls.GetHostCredentials().CertificatePem
      : certificatePem;
#if !USE_PUN
    if (!string.IsNullOrEmpty(relaySessionId)
      && !string.IsNullOrWhiteSpace(relayAddress) && relayPort != 0
      && (!IsDirectHost() || DirectHostUdpRelay.IsTunnelReady))
      return DirectHostTls.CreateRelayInvite(
        address, port, certificate, relayAddress, relayPort, relaySessionId);
#endif
    return DirectHostTls.CreateInvite(address, port, certificate);
  }


  string GetDirectIpv6HostShareAddress(ushort port)
  {
    const string IPv6RouteProbeAddress = "2606:4700:4700::1111";
    try
    {
      using (Socket routeSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
      {
        routeSocket.Connect(new IPEndPoint(IPAddress.Parse(IPv6RouteProbeAddress), 53));
        IPAddress localAddress = ((IPEndPoint)routeSocket.LocalEndPoint).Address;
        if (IsUsableDirectIpv6Address(localAddress))
        {
          return GetOrCreateDirectHostShareAddress(localAddress.ToString(), port);
        }
      }
    }
    catch (SocketException)
    {
      // Fall back to a configured interface when there is no IPv6 route.
    }

    IPAddress globalAddress = null;
    IPAddress uniqueLocalAddress = null;
    try
    {
      foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
        foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
        {
          IPAddress address = unicast.Address;
          if (!IsUsableDirectIpv6Address(address)) continue;
          byte[] bytes = address.GetAddressBytes();
          if ((bytes[0] & 0xE0) == 0x20)
          {
            globalAddress = address;
            break;
          }
          if ((bytes[0] & 0xFE) == 0xFC && uniqueLocalAddress == null)
          {
            uniqueLocalAddress = address;
          }
        }
        if (globalAddress != null) break;
      }
    }
    catch (NetworkInformationException)
    {
      // Use loopback below when interface enumeration is unavailable.
    }

    IPAddress shareAddress = globalAddress ?? uniqueLocalAddress ?? IPAddress.IPv6Loopback;
    return GetOrCreateDirectHostShareAddress(shareAddress.ToString(), port);
  }

  static bool IsUsableDirectIpv6Address(IPAddress address)
  {
    return address.AddressFamily == AddressFamily.InterNetworkV6
      && !IPAddress.IsLoopback(address)
      && !address.IsIPv6LinkLocal
      && !address.IsIPv6Multicast
      && !address.IsIPv4MappedToIPv6;
  }


  void Awake()
  {
    Util.Log($"NetworkingController.Awake t = {Time.realtimeSinceStartup}");

    Util.FindIfNotSet(this, ref voosEngine);
    Util.FindIfNotSet(this, ref behaviorSystem);
    Util.FindIfNotSet(this, ref saveLoad);
    Util.FindIfNotSet(this, ref scenes);
    Util.FindIfNotSet(this, ref gameBundleLibrary);
    Util.FindIfNotSet(this, ref builtinPrefabLibrary);
    Util.FindIfNotSet(this, ref popups);
    Util.FindIfNotSet(this, ref stage);
    Util.FindIfNotSet(this, ref terrain);
    Util.FindIfNotSet(this, ref assetCache);
    Util.FindIfNotSet(this, ref autosaves);
    Util.FindIfNotSet(this, ref sojoSystem);
    Util.FindIfNotSet(this, ref sceneActorLibrary);
    Util.FindIfNotSet(this, ref loadingScreen);
    Util.FindIfNotSet(this, ref workshop);

    // Maybe not the best place..? Should be close to OnApplicationFocus below.
    Application.targetFrameRate = -1;
  }

  void OnApplicationFocus(bool isFocused)
  {
    if (!isFocused)
    {
      Application.targetFrameRate = 30;
    }
    else
    {
      Application.targetFrameRate = -1;
    }
  }

  public void OnEnable()
  {
    scenes.OnBeforeReloadMainScene += OnBeforeReloadMainScene;
  }

  public void OnDisable()
  {
    scenes.OnBeforeReloadMainScene -= OnBeforeReloadMainScene;
  }

  string GetSteamID()
  {
#if USE_STEAMWORKS
    if (GameBuilder.Workshop.SteamRuntime.Initialized)
    {
      return Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
    }
    else
    {
      return null;
    }
#else
    return null;
#endif
  }

  void SetupPlayerProperties()
  {
#if USE_PUN
    var props = (ExitGames.Client.Photon.Hashtable)PhotonNetwork.player.CustomProperties;

    string nickName = null;

#if USE_STEAMWORKS
    if (GameBuilder.Workshop.SteamRuntime.Initialized)
    {
      nickName = Steamworks.SteamFriends.GetPersonaName();
      props[(object)SteamIdPlayerProperty] = GetSteamID();

      PlayerPrefs.SetString("PlayerNickName", nickName);
      PlayerPrefs.Save();
    }
    else
#endif
    {
      nickName = PlayerPrefs.GetString("PlayerNickName");
    }

    if (nickName.IsNullOrEmpty())
    {
      nickName = "Player";
    }

    props[(object)SteamProfileNamePlayerProperty] = nickName;  // This may be redundant given we set the playerName/NickName.
    PhotonNetwork.player.SetCustomProperties(props);
    PhotonNetwork.playerName = nickName;
#endif
  }
  internal static string GetLocalDirectPlayerName()
  {
    string nickname = PlayerPrefs.GetString("PlayerNickName");
    if (string.IsNullOrWhiteSpace(nickname)) return "Player";
    nickname = nickname.Trim();
    return nickname.Length <= 32 ? nickname : nickname.Substring(0, 32);
  }

  bool IsUserBanned(string steamId, out System.DateTime lastDate)
  {
    // (You can use this to implement user banning)
    lastDate = System.DateTime.Now;
    return false;
  }

  bool IsPlayerValid(PhotonPlayer p)
  {
    return p.ID >= 0 && PhotonPlayer.Find(p.ID) == p;
  }

  IEnumerator PumpPlayerInitCoroutine()
  {
    while (true)
    {
      if (this.gameObject == null) break;

      while (true)
      {
        if (playerInitQueue.Count == 0) break;
        PhotonPlayer p = playerInitQueue.Dequeue();
        if (!IsPlayerValid(p)) continue;
        StartCoroutine(InitializeNewPlayer(p));
        break;
      }

      yield return new WaitForSecondsRealtime(1f);
    }
  }

  void Start()
  {
#if USE_PUN
    StartCoroutine(PumpPlayerInitCoroutine());

    var diag = GetComponent<PhotonStatsGui>();
    if (diag != null)
    {
      diag.enabled = false;
    }

    try
    {
      if (!PhotonNetwork.inRoom)
      {
        PhotonNetwork.autoCleanUpPlayerObjects = false;
      }
      else
      {
        Debug.Assert(PhotonNetwork.autoCleanUpPlayerObjects == false);
      }

      PhotonNetwork.autoJoinLobby = false;
      bool isMultiplayer = GameBuilderApplication.CurrentGameOptions.playOptions.isMultiplayer;

      System.DateTime banLastDate;
      if (isMultiplayer && IsUserBanned(GetSteamID(), out banLastDate))
      {
        popups.Show($"You have been temporarily banned from multiplayer games for reported inappropriate or abusive behavior. You will be able to play again after {banLastDate.ToString("MMMM dd, yyyy")}.", "Back", () =>
        {
          scenes.LoadSplashScreen();
        }, 800f);
        return;
      }

      if (isMultiplayer)
      {
        Util.Log($"Multiplayer!");
        SetupPlayerProperties();
        mode = Mode.Online;
        PhotonNetwork.offlineMode = false;
      }

      if (PhotonNetwork.connected && PhotonNetwork.inRoom)
      {
        mode = Mode.Online;
        Util.Log($"StayOnline mode. Pretending we just joined the room.");
        didSwitchMaps = true;
        OnJoinedRoom();
      }
      else if (PhotonNetwork.connected && PhotonNetwork.insideLobby && isMultiplayer)
      {
        string roomToJoin = GameBuilderApplication.CurrentGameOptions.joinCode;
        if (!roomToJoin.IsNullOrEmpty())
        {
          Util.Log($"Trying to join room {roomToJoin}..");
          PhotonNetwork.JoinRoom(roomToJoin.ToLower());
        }
        else
        {
          TryCreateRoom();
        }
      }
      else
      {
        switch (mode)
        {
          case Mode.Online:
            string gameVersion = GetPhotonGameVersion();
            string joinCode = GameBuilderApplication.CurrentGameOptions.joinCode;
            if (joinCode == "*")
            {
              PhotonNetwork.ConnectToBestCloudServer(gameVersion);
            }
            else if (joinCode != null)
            {
              string regionCodeStr = joinCode.Split(JoinCodeSeparator)[0];
              try
              {
                CloudRegionCode regionCode = (CloudRegionCode)System.Int32.Parse(regionCodeStr);
                PhotonNetwork.ConnectToRegion(regionCode, gameVersion);
              }
              catch (System.OverflowException)
              {
                OnInvalidJoinCodeRegion();
              }
            }
            else
            {
              PhotonNetwork.ConnectToBestCloudServer(gameVersion);
            }
            break;
          case Mode.Offline:
            if (PhotonUtil.ActuallyConnected())
            {
              PhotonNetwork.Disconnect();
            }
            DestroyImmediate(GetComponent<ExitGames.UtilityScripts.PlayerRoomIndexing>());
            Util.Log($"Starting offline mode, t = {Time.realtimeSinceStartup}");
            PhotonNetwork.offlineMode = true;
            break;
        }
      }
    }
    catch (System.FormatException)
    {
      OnFatalError(BadJoinCodeMessage);
    }
    catch (System.Exception e)
    {
      OnFatalError(e.ToString());
    }
#else
    EnsureDirectNetworkManager();
    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    if (!DirectHostPassword.TryEncode(options.directHostPassword, out directHostPasswordBytes))
    {
      OnFatalError("Host passwords must be empty or 8–128 UTF-8 bytes.");
      return;
    }
    directNetworkManager.NetworkConfig.ConnectionData = directHostPasswordBytes;
    if (string.IsNullOrEmpty(options.directHostMigrationSessionId))
    {
      DirectSessionDirectory.ClearMigrationTicket();
      directHasMigrationTicket = false;
    }
    else
    {
      DirectSessionDirectory.SetMigrationTicket(
        options.directHostMigrationSessionId,
        options.directHostMigrationToken,
        options.directHostMigrationGeneration,
        options.playOptions.startAsPublic,
        options.directHostMigrationName);
      directHasMigrationTicket = true;
    }
    if (!options.playOptions.isMultiplayer)
    {
      mode = Mode.Offline;
      DirectSessionDirectory.ClearMigrationTicket();
      PhotonNetwork.ClearDirectSession();
      StartCoroutine(NonPunStartRoutine(false));
      return;
    }

    mode = Mode.Online;
    if (directNetworkManager.IsListening)
    {
      directRpcRouter.Initialize(directNetworkManager);
      RegisterDirectNetworkHandlers();
      if (directNetworkManager.IsServer)
      {
        directWorldReady = false;
        directRpcRouter.ResetScenePlayerCallbacks();
        StartCoroutine(NonPunStartRoutine(false));
      }
      else
      {
        PhotonNetwork.ResetDirectPeers();
        PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
        StartCoroutine(RequestDirectHostInitRoutine());
      }
      return;
    }
    try
    {
      directTransport.UseEncryption = true;
      if (options.joinAddress.IsNullOrEmpty())
      {
        if (!string.IsNullOrEmpty(options.directHostMigrationSessionId))
        {
          PhotonNetwork.ClearDirectSession();
          PhotonNetwork.SetDirectMigrationReservedPlayerIds(options.directHostMigrationReservedPlayerIds);
        }
        DirectHostTls.HostCredentials credentials = DirectHostTls.GetHostCredentials();
        directTransport.SetServerSecrets(credentials.CertificatePem, credentials.PrivateKeyPem);
        directHostShareAddress = null;
      }
      else
      {
        if (!DirectHostTls.IsUsablePinnedCertificate(options.trustedHostCertificatePem))
        {
          OnFatalError("This direct-host join requires a valid GBDIRECT1 or GBDIRECT2 invite with the host certificate pin.");
          return;
        }
        directHostShareAddress = null;
        directTransport.SetClientSecrets(
          DirectHostTls.ServerCommonName,
          options.trustedHostCertificatePem);
      }
    }
    catch (Exception e)
    {
      OnFatalError("Could not configure encrypted direct-host networking: " + e.Message);
      return;
    }

    if (options.joinAddress.IsNullOrEmpty())
    {
      ushort hostPort = options.joinPort == 0 ? DefaultDirectHostPort : options.joinPort;
      if (options.directHostUsesIpv6)
        directTransport.SetConnectionData("::1", hostPort, "::");
      else
        directTransport.SetConnectionData("127.0.0.1", hostPort, "0.0.0.0");
      PhotonNetwork.isMasterClient = true;
      PhotonNetwork.offlineMode = false;
      StartCoroutine(NonPunStartRoutine(true));
    }
    else
    {
      if (!string.IsNullOrEmpty(options.directRelaySessionId))
      {
        PhotonNetwork.isMasterClient = false;
        PhotonNetwork.offlineMode = false;
        StartCoroutine(StartDirectClientViaRelay(options));
        return;
      }
      ushort port = options.joinPort == 0 ? DefaultDirectHostPort : options.joinPort;
      directTransport.SetConnectionData(options.joinAddress, port);
      if (!directNetworkManager.StartClient())
      {
        OnFatalError($"Could not connect to {options.joinAddress}:{port}.");
        return;
      }
      directRpcRouter.Initialize(directNetworkManager);
      PhotonNetwork.isMasterClient = false;
      PhotonNetwork.offlineMode = false;
      RegisterDirectNetworkHandlers();
  }
#endif
  }

#if !USE_PUN
  IEnumerator StartDirectClientViaRelay(GameBuilderApplication.GameOptions options)
  {
    System.Threading.Tasks.Task<DirectHostUdpRelay.AllocationResult> allocation = System.Threading.Tasks.Task.Run(
      () => DirectHostUdpRelay.Allocate(
        options.directRelayAddress,
        options.directRelayPort,
        options.directRelaySessionId));
    while (!allocation.IsCompleted) yield return null;
    if (allocation.IsCanceled || allocation.IsFaulted)
    {
      string detail = allocation.Exception == null
        ? "relay allocation was cancelled"
        : allocation.Exception.GetBaseException().Message;
      OnFatalError("Could not connect through the self-hosted UDP relay: " + detail);
      yield break;
    }

    DirectHostUdpRelay.AllocationResult route = allocation.GetAwaiter().GetResult();
    directTransport.SetConnectionData(route.Address, route.Port);
    if (!directNetworkManager.StartClient())
    {
      OnFatalError($"Could not connect to relay {route.Address}:{route.Port}.");
      yield break;
    }
    directRpcRouter.Initialize(directNetworkManager);
    PhotonNetwork.isMasterClient = false;
    PhotonNetwork.offlineMode = false;
    RegisterDirectNetworkHandlers();
  }
#endif
  IEnumerator NonPunStartRoutine(bool startHost)
  {
    yield return null;
    yield return null;
    yield return null;
    MasterClientInit();
    if (startHost)
    {
      ushort hostPort = GameBuilderApplication.CurrentGameOptions.joinPort == 0
        ? DefaultDirectHostPort
        : GameBuilderApplication.CurrentGameOptions.joinPort;
      if (!directNetworkManager.IsListening && !directNetworkManager.StartHost())
      {
        string addressFamily = GameBuilderApplication.CurrentGameOptions.directHostUsesIpv6 ? "IPv6" : "IPv4";
        OnFatalError($"Could not start the direct-host server on {addressFamily} UDP port {hostPort}. Check that the address family is enabled and the port is available.");
        yield break;
      }
      directWorldReady = true;
      DirectHostPortMapper.BeginHostMapping(hostPort);
      directRpcRouter.Initialize(directNetworkManager);
      RegisterDirectNetworkHandlers();
    }
  }

  void EnsureDirectNetworkManager()
  {
    directNetworkManager = NetworkManager.Singleton;
    if (directNetworkManager == null)
    {
      GameObject networkObject = new GameObject("DirectHostNetworkManager");
      networkObject.SetActive(false);
      DontDestroyOnLoad(networkObject);
      directTransport = networkObject.AddComponent<UnityTransport>();
      directRpcRouter = networkObject.AddComponent<DirectHostRpcRouter>();
      directNetworkManager = networkObject.AddComponent<NetworkManager>();
      directNetworkManager.NetworkConfig = new NetworkConfig
      {
        NetworkTransport = directTransport,
        EnableSceneManagement = false
      };
      networkObject.SetActive(true);
    }
    else
    {
      directTransport = directNetworkManager.GetComponent<UnityTransport>();
      if (directTransport == null)
      {
        throw new InvalidOperationException("The existing NetworkManager has no UnityTransport.");
      }
      directRpcRouter = directNetworkManager.GetComponent<DirectHostRpcRouter>();
      if (directRpcRouter == null)
      {
        directRpcRouter = directNetworkManager.gameObject.AddComponent<DirectHostRpcRouter>();
      }
    }
    if (!directNetworkManager.IsListening)
      directNetworkManager.NetworkConfig.ConnectionApproval = true;
    directNetworkManager.ConnectionApprovalCallback = ApproveDirectHostConnection;
    PhotonView.InitializeSceneViews();
    DirectHostDiscovery.EnsureAvailable();
    DirectHostPortMapper.EnsureAvailable();
    DirectSessionDirectory.EnsureAvailable();
#if !USE_PUN
    DirectHostUdpRelay.EnsureAvailable();
#endif
  }
  void ApproveDirectHostConnection(
    NetworkManager.ConnectionApprovalRequest request,
    NetworkManager.ConnectionApprovalResponse response)
  {
    response.Approved = request.ClientNetworkId == NetworkManager.ServerClientId
      || DirectHostPassword.Matches(directHostPasswordBytes, request.Payload);
    response.CreatePlayerObject = false;
    response.Pending = false;
    response.Reason = response.Approved
      ? string.Empty
      : "The shared host password is incorrect.";
  }


  void RegisterDirectNetworkHandlers()
  {
    directNetworkManager.OnClientConnectedCallback -= OnDirectClientConnected;
    directNetworkManager.OnClientConnectedCallback += OnDirectClientConnected;
    directNetworkManager.OnClientDisconnectCallback -= OnDirectClientDisconnected;
    directNetworkManager.OnClientDisconnectCallback += OnDirectClientDisconnected;
    if (registeredDirectInitHandler)
    {
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectHostInitMessageName);
    }
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
      DirectHostInitMessageName, OnDirectHostInitMessage);
    registeredDirectInitHandler = true;
    if (registeredDirectPlayerHandlers)
    {
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerAssignmentMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReconnectMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectSessionMigrationTicketMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReadyMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerSpawnMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerDespawnMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerIdentityMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerInitRequestMessageName);
#if !USE_STEAMWORKS
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectWorkshopAssetRequestMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectWorkshopAssetResponseMessageName);
#endif
    }
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
      DirectPlayerAssignmentMessageName, OnDirectPlayerAssignment);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
      DirectPlayerReconnectMessageName, OnDirectPlayerReconnectRequest);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
      DirectSessionMigrationTicketMessageName, OnDirectSessionMigrationTicket);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerReadyMessageName, OnDirectPlayerReady);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerSpawnMessageName, OnDirectPlayerSpawn);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerDespawnMessageName, OnDirectPlayerDespawn);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerIdentityMessageName, OnDirectPlayerIdentityMessage);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerInitRequestMessageName, OnDirectPlayerInitRequest);
#if !USE_STEAMWORKS
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectWorkshopAssetRequestMessageName, OnDirectWorkshopAssetRequest);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectWorkshopAssetResponseMessageName, OnDirectWorkshopAssetResponse);
#endif
    registeredDirectPlayerHandlers = true;
  }

  void OnDirectClientConnected(ulong clientId)
  {
    if (directNetworkManager == null) return;
    if (!directNetworkManager.IsServer)
    {
      if (clientId == directNetworkManager.LocalClientId)
      {
        directClientAssignmentReceived = false;
        PhotonNetwork.ResetDirectPeers();
        SendDirectPlayerReconnectRequest();
      }
      return;
    }
    if (clientId == directNetworkManager.LocalClientId) return;
    directClientsAwaitingReconnect.Add(clientId);
    SendDirectMigrationTicket(clientId);
    StartCoroutine(AssignDirectPlayerAfterReconnectRequest(clientId));
  }

  void SendDirectPlayerReconnectRequest()
  {
    if (directNetworkManager == null || directNetworkManager.IsServer || !directNetworkManager.IsListening) return;
    int preferredPlayerId = PhotonNetwork.player == null ? 0 : PhotonNetwork.player.ID;
    int migrationPlayerId = GameBuilderApplication.CurrentGameOptions.directHostMigrationPreviousPlayerId;
    if (migrationPlayerId > 1 && migrationPlayerId <= PhotonNetwork.MaximumDirectPlayers)
      preferredPlayerId = migrationPlayerId;
    using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(preferredPlayerId);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectPlayerReconnectMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }
  }


  void OnDirectPlayerReconnectRequest(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || !directNetworkManager.IsServer
      || !directClientsAwaitingReconnect.Contains(senderClientId)) return;
    int preferredPlayerId = 0;
    try { reader.ReadValueSafe(out preferredPlayerId); }
    catch (Exception e) { Util.LogError($"Invalid direct reconnect request from {senderClientId}: {e.Message}"); }
    if (string.IsNullOrEmpty(GameBuilderApplication.CurrentGameOptions.directHostMigrationSessionId))
      preferredPlayerId = 0;
    directClientsAwaitingReconnect.Remove(senderClientId);
    InitializeDirectClient(senderClientId, preferredPlayerId);
  }

  IEnumerator AssignDirectPlayerAfterReconnectRequest(ulong clientId)
  {
    yield return new WaitForSecondsRealtime(2f);
    if (directNetworkManager == null || !directNetworkManager.IsServer
      || !directClientsAwaitingReconnect.Remove(clientId)) yield break;
    foreach (ulong connectedClientId in directNetworkManager.ConnectedClientsIds)
    {
      if (connectedClientId != clientId) continue;
      InitializeDirectClient(clientId, 0);
      yield break;
    }
  }

  void InitializeDirectClient(ulong clientId, int preferredPlayerId)
  {
    if (!PhotonNetwork.TryAssignDirectPlayerId(clientId, preferredPlayerId, out int joiningPlayerId))
    {
      Util.LogError($"Direct hosting is full ({PhotonNetwork.MaximumDirectPlayers} players).");
      directNetworkManager.DisconnectClient(clientId);
      return;
    }

    try
    {
      PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
      if (!string.IsNullOrEmpty(GameBuilderApplication.CurrentGameOptions.directHostMigrationSessionId)
        && preferredPlayerId > 1 && preferredPlayerId != joiningPlayerId)
        ReassignDirectPlayerOwnership(preferredPlayerId, joiningPlayerId);
      SendDirectPlayerAssignment(clientId, joiningPlayerId);
      foreach (PhotonPlayer connectedPlayer in PhotonNetwork.playerList)
      {
        if (connectedPlayer.ID != joiningPlayerId)
          SendDirectPlayerIdentity(clientId, connectedPlayer.ID, connectedPlayer.NickName);
      }
    }
    catch (Exception e)
    {
      Util.LogError($"Could not initialize direct client {clientId}: {e}");
      directRpcRouter.NotifyServerPlayerDisconnected(PhotonNetwork.FindPlayer(joiningPlayerId));
      PhotonNetwork.ForgetDirectPeerName(joiningPlayerId);
      PhotonNetwork.ReleaseDirectPlayerId(clientId);
      directNetworkManager.DisconnectClient(clientId);
    }
  }
  void ReassignDirectPlayerOwnership(int previousPlayerId, int currentPlayerId)
  {
    if (previousPlayerId <= 1 || currentPlayerId <= 1 || previousPlayerId == currentPlayerId || voosEngine == null)
      return;
    foreach (VoosActor actor in voosEngine.EnumerateActors())
    {
      if (actor.reliablePhotonView == null || actor.reliablePhotonView.ownerId != previousPlayerId) continue;
      actor.reliablePhotonView.ApplyOwnershipAndNotify(currentPlayerId);
    }
  }

  void SendDirectMigrationTicket(ulong clientId)
  {
    if (directNetworkManager == null || !directNetworkManager.IsServer
      || !DirectSessionDirectory.TryGetMigrationTicket(out DirectSessionDirectory.MigrationTicket ticket)) return;
    byte[] sessionIdBytes = Encoding.UTF8.GetBytes(ticket.SessionId);
    byte[] tokenBytes = Encoding.UTF8.GetBytes(ticket.Token);
    byte[] nameBytes = Encoding.UTF8.GetBytes(ticket.Name ?? string.Empty);
    if (sessionIdBytes.Length == 0 || sessionIdBytes.Length > 128
      || tokenBytes.Length == 0 || tokenBytes.Length > 128 || nameBytes.Length > 128)
      return;
    using (FastBufferWriter writer = new FastBufferWriter(
      sessionIdBytes.Length + tokenBytes.Length + nameBytes.Length + 5 * sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(sessionIdBytes.Length);
      writer.WriteBytesSafe(sessionIdBytes);
      writer.WriteValueSafe(tokenBytes.Length);
      writer.WriteBytesSafe(tokenBytes);
      writer.WriteValueSafe(nameBytes.Length);
      writer.WriteBytesSafe(nameBytes);
      writer.WriteValueSafe(ticket.Generation);
      writer.WriteValueSafe(ticket.IsPublic ? 1 : 0);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectSessionMigrationTicketMessageName, clientId, writer, NetworkDelivery.ReliableSequenced);
    }
  }

  void OnDirectSessionMigrationTicket(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer
      || senderClientId != NetworkManager.ServerClientId) return;
    try
    {
      reader.ReadValueSafe(out int sessionIdLength);
      if (sessionIdLength < 1 || sessionIdLength > 128)
        throw new InvalidDataException("Invalid migration session ID length.");
      byte[] sessionIdBytes = new byte[sessionIdLength];
      reader.ReadBytesSafe(ref sessionIdBytes, sessionIdLength);
      reader.ReadValueSafe(out int tokenLength);
      if (tokenLength < 1 || tokenLength > 128)
        throw new InvalidDataException("Invalid migration token length.");
      byte[] tokenBytes = new byte[tokenLength];
      reader.ReadBytesSafe(ref tokenBytes, tokenLength);
      reader.ReadValueSafe(out int nameLength);
      if (nameLength < 0 || nameLength > 128)
        throw new InvalidDataException("Invalid migration session name length.");
      byte[] nameBytes = new byte[nameLength];
      reader.ReadBytesSafe(ref nameBytes, nameLength);
      reader.ReadValueSafe(out int generation);
      reader.ReadValueSafe(out int publicValue);
      if (generation < 1 || (publicValue != 0 && publicValue != 1))
        throw new InvalidDataException("Invalid migration ticket metadata.");
      DirectSessionDirectory.SetMigrationTicket(
        Encoding.UTF8.GetString(sessionIdBytes),
        Encoding.UTF8.GetString(tokenBytes),
        generation,
        publicValue == 1,
        Encoding.UTF8.GetString(nameBytes));
      directHasMigrationTicket = true;
    }
    catch (Exception e)
    {
      Util.LogError($"Invalid direct-session migration ticket: {e}");
    }
  }

  void SendDirectPlayerAssignment(ulong clientId, int playerId)
  {
    using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(playerId);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectPlayerAssignmentMessageName, clientId, writer, NetworkDelivery.ReliableSequenced);
    }
  }

  void OnDirectPlayerAssignment(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer
      || senderClientId != NetworkManager.ServerClientId) return;
    try
    {
      reader.ReadValueSafe(out int playerId);
      if (directClientAssignmentReceived)
      {
        if (PhotonNetwork.player.ID != playerId)
          throw new InvalidDataException("The host assigned more than one player ID to this connection.");
        return;
      }
      if (!PhotonNetwork.AssignDirectLocalPlayerId(directNetworkManager, playerId))
        throw new InvalidDataException($"The host assigned invalid or occupied player ID {playerId}.");

      directClientAssignmentReceived = true;
      PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
      SendDirectPlayerIdentity(NetworkManager.ServerClientId, PhotonNetwork.player.ID, PhotonNetwork.player.NickName);
      StartCoroutine(RequestDirectHostInitRoutine());
    }
    catch (Exception e)
    {
      Util.LogError($"Invalid direct player assignment: {e}");
      directNetworkManager.Shutdown();
      OnFatalError("Could not initialize your player slot with the host.");
    }
  }
  void SendDirectHostInit(ulong clientId)
  {
    byte[] initPacket = CreateDirectHostInitPacket();
    int chunkCount = (initPacket.Length + DirectHostInitChunkBytes - 1) / DirectHostInitChunkBytes;
    for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
    {
      int offset = chunkIndex * DirectHostInitChunkBytes;
      int chunkLength = Math.Min(DirectHostInitChunkBytes, initPacket.Length - offset);
      using (FastBufferWriter writer = new FastBufferWriter(chunkLength + 4 * sizeof(int), Allocator.Temp))
      {
        WriteDirectHostInitChunk(writer, initPacket, chunkIndex, chunkCount, chunkLength);
        directNetworkManager.CustomMessagingManager.SendNamedMessage(
          DirectHostInitMessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
      }
    }
    Util.Log($"Sent initial world state ({initPacket.Length / 1024} KB) to direct client {clientId} in {chunkCount} chunks.");
  }

  static void WriteDirectHostInitChunk(FastBufferWriter writer, byte[] packet, int chunkIndex, int chunkCount, int chunkLength)
  {
    int offset = chunkIndex * DirectHostInitChunkBytes;
    writer.WriteValueSafe(packet.Length);
    writer.WriteValueSafe(chunkIndex);
    writer.WriteValueSafe(chunkCount);
    writer.WriteValueSafe(chunkLength);
    writer.WriteBytesSafe(packet, chunkLength, offset);
  }

  void SendDirectPlayerIdentity(ulong clientId, int playerId, string nickname)
  {
    if (directNetworkManager == null || !directNetworkManager.IsListening) return;
    byte[] nicknameBytes = Encoding.UTF8.GetBytes(nickname ?? string.Empty);
    if (nicknameBytes.Length == 0 || nicknameBytes.Length > 128)
      throw new InvalidDataException("Direct player nicknames must contain 1–128 UTF-8 bytes.");
    ulong sourceClientId = directNetworkManager.IsServer
      ? PhotonNetwork.GetDirectClientId(playerId)
        ?? throw new InvalidOperationException($"Player {playerId} has no direct client mapping.")
      : directNetworkManager.LocalClientId;
    using (FastBufferWriter writer = new FastBufferWriter(nicknameBytes.Length + sizeof(int) * 2 + sizeof(ulong), Allocator.Temp))
    {
      writer.WriteValueSafe(playerId);
      writer.WriteValueSafe(sourceClientId);
      writer.WriteValueSafe(nicknameBytes.Length);
      writer.WriteBytesSafe(nicknameBytes);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectPlayerIdentityMessageName, clientId, writer, NetworkDelivery.ReliableSequenced);
    }
  }

  void OnDirectPlayerIdentityMessage(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || !directNetworkManager.IsListening) return;
    try
    {
      reader.ReadValueSafe(out int playerId);
      reader.ReadValueSafe(out ulong sourceClientId);
      reader.ReadValueSafe(out int nicknameLength);
      if (nicknameLength <= 0 || nicknameLength > 128) throw new InvalidDataException("Invalid direct player nickname length.");
      byte[] nicknameBytes = new byte[nicknameLength];
      reader.ReadBytesSafe(ref nicknameBytes, nicknameLength);
      string nickname = Encoding.UTF8.GetString(nicknameBytes).Trim();
      if (nickname.Length == 0) throw new InvalidDataException("Direct player nickname cannot be empty.");

      if (directNetworkManager.IsServer)
      {
        if (senderClientId == directNetworkManager.LocalClientId
          || playerId != PhotonNetwork.GetDirectPlayerId(senderClientId)
          || sourceClientId != senderClientId)
          throw new InvalidDataException("Direct player identity did not match its assigned transport client.");
        bool isNewPlayer = !PhotonNetwork.HasDirectPlayerIdentity(playerId);
        PhotonNetwork.SetDirectPeerName(playerId, nickname);
        if (kickedPlayerNickNames.Contains(nickname))
        {
          PhotonNetwork.ForgetDirectPeerName(playerId);
          KickPlayer(PhotonNetwork.FindPlayer(playerId));
          return;
        }
        if (isNewPlayer)
        {
          HudNotifications cms = FindObjectOfType<HudNotifications>();
          if (cms != null) cms.AddMessage($"'{nickname}' joined!");
        }
        directRpcRouter.NotifyServerPlayerConnected(PhotonNetwork.FindPlayer(playerId));
        foreach (ulong clientId in directNetworkManager.ConnectedClientsIds)
          if (clientId != directNetworkManager.LocalClientId && clientId != senderClientId)
            SendDirectPlayerIdentity(clientId, playerId, nickname);
      }
      else if (senderClientId == NetworkManager.ServerClientId)
      {
        if (playerId <= 0 || playerId > PhotonNetwork.MaximumDirectPlayers
          || playerId == PhotonNetwork.player.ID
          || sourceClientId == directNetworkManager.LocalClientId
          || (playerId == 1) != (sourceClientId == NetworkManager.ServerClientId))
          throw new InvalidDataException("Host sent an invalid direct player identity.");
        bool isNewPlayer = !PhotonNetwork.HasDirectPlayerIdentity(playerId);
        PhotonNetwork.SetDirectPeerName(playerId, nickname);
        if (!PhotonNetwork.RegisterDirectPeer(playerId, sourceClientId)
          && PhotonNetwork.GetDirectPlayerId(sourceClientId) != playerId)
          throw new InvalidDataException("Host reused a direct player ID for a different transport client.");
        if (isNewPlayer && receivedPlayerInitPayload)
        {
          HudNotifications cms = FindObjectOfType<HudNotifications>();
          if (cms != null) cms.AddMessage($"'{nickname}' joined!");
        }
      }
    }
    catch (Exception e)
    {
      Util.LogError($"Invalid direct player identity message from client {senderClientId}: {e}");
      if (directNetworkManager.IsServer && senderClientId != directNetworkManager.LocalClientId)
        directNetworkManager.DisconnectClient(senderClientId);
    }
  }

  void OnDirectPlayerInitRequest(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || !directNetworkManager.IsServer || senderClientId == directNetworkManager.LocalClientId) return;
    try
    {
      reader.ReadValueSafe(out int playerId);
      if (playerId <= 1 || playerId > PhotonNetwork.MaximumDirectPlayers
        || playerId != PhotonNetwork.GetDirectPlayerId(senderClientId)
        || !PhotonNetwork.HasDirectPlayerIdentity(playerId)
        || !directWorldReady
        || !directInitResponseClients.Add(senderClientId)) return;
      directRpcRouter.NotifyServerPlayerConnected(PhotonNetwork.FindPlayer(playerId));

      foreach (PhotonPlayer connectedPlayer in PhotonNetwork.playerList)
        if (connectedPlayer.ID != playerId)
          SendDirectPlayerIdentity(senderClientId, connectedPlayer.ID, connectedPlayer.NickName);
      SendDirectHostInit(senderClientId);
    }
    catch (Exception e)
    {
      directInitResponseClients.Remove(senderClientId);
      Util.LogError($"Could not send the direct-host world state to client {senderClientId}: {e}");
      directNetworkManager.DisconnectClient(senderClientId);
    }
  }
#if !USE_STEAMWORKS
  public bool RequestDirectWorkshopAsset(ulong assetId, System.Action<Util.Maybe<string>> callback)
  {
    if (callback == null || directNetworkManager == null || directNetworkManager.IsServer
      || !directNetworkManager.IsConnectedClient) return false;
    List<System.Action<Util.Maybe<string>>> callbacks;
    if (directWorkshopAssetCallbacks.TryGetValue(assetId, out callbacks))
    {
      callbacks.Add(callback);
      return true;
    }

    callbacks = new List<System.Action<Util.Maybe<string>>>();
    callbacks.Add(callback);
    directWorkshopAssetCallbacks.Add(assetId, callbacks);
    directWorkshopAssetDeadlines.Add(assetId, Time.realtimeSinceStartup + DirectWorkshopAssetRequestTimeoutSeconds);
    using (FastBufferWriter writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp))
    {
      writer.WriteValueSafe(assetId);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectWorkshopAssetRequestMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }
    return true;
  }

  void OnDirectWorkshopAssetRequest(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || !directNetworkManager.IsServer
      || senderClientId == directNetworkManager.LocalClientId) return;
    try
    {
      reader.ReadValueSafe(out ulong assetId);
      int playerId = PhotonNetwork.GetDirectPlayerId(senderClientId);
      if (playerId <= 1 || !PhotonNetwork.HasDirectPlayerIdentity(playerId)) return;
      byte[] archive;
      string error;
      bool found = workshop.TryCreateDirectAssetArchive(assetId, out archive, out error);
      SendDirectWorkshopAssetResponse(senderClientId, assetId, found ? archive : null);
      if (!found) Util.LogWarning($"Could not send direct workshop asset {assetId}: {error}");
    }
    catch (Exception exception)
    {
      Util.LogError($"Invalid direct workshop asset request from {senderClientId}: {exception.Message}");
    }
  }

  void SendDirectWorkshopAssetResponse(ulong clientId, ulong assetId, byte[] archive)
  {
    bool found = archive != null && archive.Length > 0 && archive.Length <= MaximumDirectWorkshopAssetBytes;
    int chunkCount = found ? (archive.Length + DirectWorkshopAssetChunkBytes - 1) / DirectWorkshopAssetChunkBytes : 1;
    for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
    {
      int chunkLength = found ? Math.Min(DirectWorkshopAssetChunkBytes, archive.Length - chunkIndex * DirectWorkshopAssetChunkBytes) : 0;
      using (FastBufferWriter writer = new FastBufferWriter(sizeof(ulong) + 5 * sizeof(int) + chunkLength, Allocator.Temp))
      {
        writer.WriteValueSafe(assetId);
        writer.WriteValueSafe(found ? 1 : 0);
        writer.WriteValueSafe(found ? archive.Length : 0);
        writer.WriteValueSafe(chunkIndex);
        writer.WriteValueSafe(chunkCount);
        writer.WriteValueSafe(chunkLength);
        if (chunkLength > 0) writer.WriteBytesSafe(archive, chunkLength, chunkIndex * DirectWorkshopAssetChunkBytes);
        directNetworkManager.CustomMessagingManager.SendNamedMessage(
          DirectWorkshopAssetResponseMessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
      }
    }
  }

  void OnDirectWorkshopAssetResponse(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
    ulong assetId = 0;
    try
    {
      reader.ReadValueSafe(out assetId);
      reader.ReadValueSafe(out int status);
      reader.ReadValueSafe(out int totalLength);
      reader.ReadValueSafe(out int chunkIndex);
      reader.ReadValueSafe(out int chunkCount);
      reader.ReadValueSafe(out int chunkLength);
      if (status == 0)
      {
        CompleteDirectWorkshopAssetRequest(assetId, Util.Maybe<string>.CreateError("The host does not have this asset."));
        return;
      }
      DirectWorkshopAssetAssembler assembler;
      if (!directWorkshopAssetAssemblers.TryGetValue(assetId, out assembler))
      {
        assembler = new DirectWorkshopAssetAssembler();
        directWorkshopAssetAssemblers.Add(assetId, assembler);
      }
      byte[] archive = assembler.Append(totalLength, chunkIndex, chunkCount, chunkLength, reader);
      if (archive == null) return;
      directWorkshopAssetAssemblers.Remove(assetId);
      string path;
      string error;
      if (workshop.TryStoreDirectAssetArchive(assetId, archive, out path, out error))
        CompleteDirectWorkshopAssetRequest(assetId, Util.Maybe<string>.CreateWith(path));
      else
        CompleteDirectWorkshopAssetRequest(assetId, Util.Maybe<string>.CreateError(error));
    }
    catch (Exception exception)
    {
      directWorkshopAssetAssemblers.Remove(assetId);
      CompleteDirectWorkshopAssetRequest(assetId, Util.Maybe<string>.CreateError("The host sent an invalid asset archive."));
      Util.LogError($"Invalid direct workshop asset response: {exception.Message}");
    }
  }

  void CompleteDirectWorkshopAssetRequest(ulong assetId, Util.Maybe<string> result)
  {
    List<System.Action<Util.Maybe<string>>> callbacks;
    if (!directWorkshopAssetCallbacks.TryGetValue(assetId, out callbacks)) return;
    directWorkshopAssetCallbacks.Remove(assetId);
    directWorkshopAssetDeadlines.Remove(assetId);
    directWorkshopAssetAssemblers.Remove(assetId);
    foreach (System.Action<Util.Maybe<string>> callback in callbacks) callback(result);
  }

  void ExpireDirectWorkshopAssetRequests(float now)
  {
    if (directWorkshopAssetDeadlines.Count == 0) return;
    foreach (KeyValuePair<ulong, float> entry in directWorkshopAssetDeadlines)
      if (now >= entry.Value) expiredDirectWorkshopAssetIds.Add(entry.Key);
    foreach (ulong assetId in expiredDirectWorkshopAssetIds)
      CompleteDirectWorkshopAssetRequest(
        assetId, Util.Maybe<string>.CreateError("The direct host did not respond before the asset request timed out."));
    expiredDirectWorkshopAssetIds.Clear();
  }

  void FailOutstandingDirectWorkshopAssetRequests(string error)
  {
    if (directWorkshopAssetCallbacks.Count == 0) return;
    foreach (List<System.Action<Util.Maybe<string>>> callbacks in directWorkshopAssetCallbacks.Values)
      failedDirectWorkshopAssetCallbacks.Add(callbacks);
    directWorkshopAssetCallbacks.Clear();
    directWorkshopAssetDeadlines.Clear();
    directWorkshopAssetAssemblers.Clear();
    foreach (List<System.Action<Util.Maybe<string>>> callbacks in failedDirectWorkshopAssetCallbacks)
      foreach (System.Action<Util.Maybe<string>> callback in callbacks)
        callback(Util.Maybe<string>.CreateError(error));
    failedDirectWorkshopAssetCallbacks.Clear();
  }

  sealed class DirectWorkshopAssetAssembler
  {
    byte[] archive;
    int expectedChunkCount;
    int nextChunkIndex;

    public byte[] Append(int totalLength, int chunkIndex, int chunkCount, int chunkLength, FastBufferReader reader)
    {
      int expectedCount = (totalLength + DirectWorkshopAssetChunkBytes - 1) / DirectWorkshopAssetChunkBytes;
      if (totalLength <= 0 || totalLength > MaximumDirectWorkshopAssetBytes || chunkCount != expectedCount
        || chunkIndex != nextChunkIndex || chunkIndex < 0 || chunkIndex >= chunkCount
        || chunkLength != Math.Min(DirectWorkshopAssetChunkBytes, totalLength - chunkIndex * DirectWorkshopAssetChunkBytes)
        || reader.Length - reader.Position != chunkLength) throw new InvalidDataException("Invalid direct workshop asset chunk.");
      if (archive == null)
      {
        archive = new byte[totalLength];
        expectedChunkCount = chunkCount;
      }
      if (archive.Length != totalLength || expectedChunkCount != chunkCount) throw new InvalidDataException("Inconsistent direct workshop asset chunks.");
      reader.ReadBytesSafe(ref archive, chunkLength, chunkIndex * DirectWorkshopAssetChunkBytes);
      nextChunkIndex++;
      if (nextChunkIndex != expectedChunkCount) return null;
      byte[] completed = archive;
      archive = null;
      nextChunkIndex = 0;
      expectedChunkCount = 0;
      return completed;
    }
  }
#endif

  IEnumerator RequestDirectHostInitRoutine()
  {

    while (!receivedPlayerInitPayload)
    {
      if (directNetworkManager == null || !directNetworkManager.IsConnectedClient)
      {
        OnFatalError("The host disconnected before reloading the game.");
        yield break;
      }

      using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
      {
        writer.WriteValueSafe(PhotonNetwork.player.ID);
        directNetworkManager.CustomMessagingManager.SendNamedMessage(
          DirectPlayerInitRequestMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
      }
      yield return new WaitForSecondsRealtime(1f);
    }
  }

  void OnDirectPlayerReady(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || !directNetworkManager.IsServer || senderClientId == directNetworkManager.LocalClientId) return;
    try
    {
      reader.ReadValueSafe(out int playerId);
      reader.ReadValueSafe(out int viewId);
      Vector3 position = ReadDirectVector3(ref reader);
      Quaternion rotation = ReadDirectQuaternion(ref reader);
      if (playerId != PhotonNetwork.GetDirectPlayerId(senderClientId)
        || !IsDirectAvatarViewIdValid(playerId, viewId)
        || !IsFinite(position) || !IsFinite(rotation)) return;

      UserBody localBody = localUserInstance != null ? localUserInstance.GetComponent<UserBody>() : null;
      if (localBody != null) SendDirectPlayerSpawn(senderClientId, PhotonNetwork.player.ID, localBody);
      foreach (KeyValuePair<int, UserBody> entry in directRemoteBodies)
        if (entry.Key != playerId && entry.Value != null) SendDirectPlayerSpawn(senderClientId, entry.Key, entry.Value);

      SpawnDirectRemotePlayer(playerId, viewId, position, rotation);
      BroadcastDirectPlayerSpawn(playerId, viewId, position, rotation, senderClientId);
      DirectHostRpcRouter.ForceResendAll();
      Util.Log($"Direct player {playerId} is ready.");
    }
    catch (Exception e)
    {
      Util.LogError($"Invalid direct player-ready message from client {senderClientId}: {e}");
      directNetworkManager.DisconnectClient(senderClientId);
    }
  }

  static Vector3 ReadDirectVector3(ref FastBufferReader reader)
  {
    reader.ReadValueSafe(out float x);
    reader.ReadValueSafe(out float y);
    reader.ReadValueSafe(out float z);
    return new Vector3(x, y, z);
  }

  static Quaternion ReadDirectQuaternion(ref FastBufferReader reader)
  {
    reader.ReadValueSafe(out float x);
    reader.ReadValueSafe(out float y);
    reader.ReadValueSafe(out float z);
    reader.ReadValueSafe(out float w);
    return new Quaternion(x, y, z, w);
  }

  static bool IsFinite(Vector3 value)
  {
    return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
  }

  static bool IsFinite(Quaternion value)
  {
    float magnitude = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
    return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w)
      && magnitude > 0.0001f;
  }

  static bool IsFinite(float value)
  {
    return !float.IsNaN(value) && !float.IsInfinity(value);
  }

  static bool IsDirectAvatarViewIdValid(int playerId, int viewId)
  {
    if (playerId <= 0 || viewId <= 0 || viewId > short.MaxValue) return false;
    return (viewId - 1) / PhotonNetwork.DirectViewIdBlockSize + 1 == playerId;
  }

  void SendDirectPlayerReady()
  {
    if (directNetworkManager == null || !directNetworkManager.IsConnectedClient || directNetworkManager.IsServer) return;
    UserBody body = localUserInstance != null ? localUserInstance.GetComponent<UserBody>() : null;
    PhotonView view = body != null ? body.GetComponent<PhotonView>() : null;
    if (view == null || !IsDirectAvatarViewIdValid(PhotonNetwork.player.ID, view.viewID)) return;
    using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
    {
      writer.WriteValueSafe(PhotonNetwork.player.ID);
      writer.WriteValueSafe(view.viewID);
      WriteDirectTransform(writer, body.transform.position, body.transform.rotation);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectPlayerReadyMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }
    DirectHostRpcRouter.ForceResendAll();
  }

  static void WriteDirectTransform(FastBufferWriter writer, Vector3 position, Quaternion rotation)
  {
    writer.WriteValueSafe(position.x);
    writer.WriteValueSafe(position.y);
    writer.WriteValueSafe(position.z);
    writer.WriteValueSafe(rotation.x);
    writer.WriteValueSafe(rotation.y);
    writer.WriteValueSafe(rotation.z);
    writer.WriteValueSafe(rotation.w);
  }

  void SendDirectPlayerSpawn(ulong targetClientId, int playerId, UserBody body)
  {
    if (body == null || directNetworkManager == null || !directNetworkManager.IsServer) return;
    PhotonView view = body.GetComponent<PhotonView>();
    if (view == null || !IsDirectAvatarViewIdValid(playerId, view.viewID)) return;
    using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
    {
      writer.WriteValueSafe(playerId);
      writer.WriteValueSafe(view.viewID);
      WriteDirectTransform(writer, body.transform.position, body.transform.rotation);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectPlayerSpawnMessageName, targetClientId, writer, NetworkDelivery.ReliableSequenced);
    }
  }

  void BroadcastDirectPlayerSpawn(int playerId, int viewId, Vector3 position, Quaternion rotation, ulong excludedClientId)
  {
    foreach (ulong clientId in directNetworkManager.ConnectedClientsIds)
    {
      if (clientId == directNetworkManager.LocalClientId || clientId == excludedClientId) continue;
      using (FastBufferWriter writer = new FastBufferWriter(64, Allocator.Temp))
      {
        writer.WriteValueSafe(playerId);
        writer.WriteValueSafe(viewId);
        WriteDirectTransform(writer, position, rotation);
        directNetworkManager.CustomMessagingManager.SendNamedMessage(
          DirectPlayerSpawnMessageName, clientId, writer, NetworkDelivery.ReliableSequenced);
      }
    }
  }

  void OnDirectPlayerSpawn(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
    try
    {
      reader.ReadValueSafe(out int playerId);
      reader.ReadValueSafe(out int viewId);
      Vector3 position = ReadDirectVector3(ref reader);
      Quaternion rotation = ReadDirectQuaternion(ref reader);
      if (playerId == PhotonNetwork.player.ID || !IsDirectAvatarViewIdValid(playerId, viewId)
        || !IsFinite(position) || !IsFinite(rotation)) return;
      ulong? peerClientId = PhotonNetwork.GetDirectClientId(playerId);
      if (!peerClientId.HasValue)
        throw new InvalidDataException($"Direct player {playerId} has no transport mapping.");
      if (!PhotonNetwork.RegisterDirectPeer(playerId, peerClientId.Value)
        && PhotonNetwork.GetDirectPlayerId(peerClientId.Value) != playerId)
        throw new InvalidDataException($"Direct player {playerId} maps to a different transport client.");
      SpawnDirectRemotePlayer(playerId, viewId, position, rotation);
      return;
    }
    catch (Exception e) { Util.LogError($"Invalid direct player spawn: {e}"); }
  }

  void SpawnDirectRemotePlayer(int playerId, int viewId, Vector3 position, Quaternion rotation)
  {
    if (playerId == PhotonNetwork.player.ID) return;
    if (directRemoteBodies.TryGetValue(playerId, out UserBody existingBody) && existingBody != null)
    {
      PhotonView existingView = existingBody.GetComponent<PhotonView>();
      if (existingView != null && existingView.viewID == viewId)
      {
        existingBody.SetDirectPose(position, rotation);
        return;
      }
      Destroy(existingBody.gameObject);
      directRemoteBodies.Remove(playerId);
    }

    PhotonView conflictingView = PhotonView.Find(viewId);
    if (conflictingView != null) return;
    GameObject instance = Instantiate(userBodyPrefab.gameObject, position, rotation);
    UserBody body = instance.GetComponent<UserBody>();
    PhotonView view = instance.GetComponent<PhotonView>();
#if !USE_PUN
    view.SetViewId(viewId);
    view.ApplyOwnership(playerId);
#else
    view.viewID = viewId;
    view.ApplyOwnership(playerId);
#endif
    body.Setup(false);
    body.SetDirectPose(position, rotation);
    directRemoteBodies[playerId] = body;
  }

  void OnDirectPlayerDespawn(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
    reader.ReadValueSafe(out int playerId);
    if (playerId <= 1 || playerId > PhotonNetwork.MaximumDirectPlayers) return;

    PhotonPlayer disconnectedPlayer = PhotonNetwork.FindPlayer(playerId);
    bool hadIdentity = PhotonNetwork.HasDirectPlayerIdentity(playerId);
    RemoveDirectRemotePlayer(playerId);
    if (!hadIdentity) return;
    if (!PhotonNetwork.RemoveDirectPeer(playerId))
      PhotonNetwork.ForgetDirectPeerName(playerId);
    PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());

    HudNotifications cms = FindObjectOfType<HudNotifications>();
    if (cms != null) cms.AddMessage($"'{disconnectedPlayer.NickName}' left");
    if (voosEngine != null)
      voosEngine.BroadcastMessage("PlayerLeft", new PlayerLeftMessage
      {
        id = disconnectedPlayer.ID,
        nickName = disconnectedPlayer.NickName
      });
  }

  void RemoveDirectRemotePlayer(int playerId)
  {
    if (!directRemoteBodies.TryGetValue(playerId, out UserBody body)) return;
    directRemoteBodies.Remove(playerId);
    if (body != null) Destroy(body.gameObject);
  }

  void BroadcastDirectPlayerDespawn(int playerId)
  {
    foreach (ulong clientId in directNetworkManager.ConnectedClientsIds)
    {
      if (clientId == directNetworkManager.LocalClientId) continue;
      using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
      {
        writer.WriteValueSafe(playerId);
        directNetworkManager.CustomMessagingManager.SendNamedMessage(
          DirectPlayerDespawnMessageName, clientId, writer, NetworkDelivery.ReliableSequenced);
      }
    }
  }

  void ReclaimDirectPlayerActors(int playerId)
  {
    foreach (VoosActor actor in voosEngine.EnumerateActors())
    {
      if (actor.reliablePhotonView == null || actor.reliablePhotonView.ownerId != playerId) continue;
      actor.reliablePhotonView.TransferOwnership(PhotonNetwork.player.ID);
    }
  }


  static bool ShouldPublishDirectPlayerDeparture(bool hadIdentity)
  {
    // An assigned transport slot is not a gameplay player until its identity
    // handshake completes.
    return hadIdentity;
  }

  void OnDirectClientDisconnected(ulong clientId)
  {
    if (directNetworkManager == null) return;
    if (directNetworkManager.IsServer && clientId != directNetworkManager.LocalClientId)
    {
      directClientsAwaitingReconnect.Remove(clientId);
      int playerId = PhotonNetwork.GetDirectPlayerId(clientId);
      if (playerId <= 1) return;
      directInitResponseClients.Remove(clientId);
      PhotonPlayer disconnectedPlayer = PhotonNetwork.FindPlayer(playerId);
      bool hadIdentity = PhotonNetwork.HasDirectPlayerIdentity(playerId);
      RemoveDirectRemotePlayer(playerId);
      if (ShouldPublishDirectPlayerDeparture(hadIdentity))
      {
        if (directRpcRouter != null)
          directRpcRouter.NotifyServerPlayerDisconnected(disconnectedPlayer);
        BroadcastDirectPlayerDespawn(playerId);
        ReclaimDirectPlayerActors(playerId);
        PlayerLeftMessage message = new PlayerLeftMessage { id = playerId, nickName = disconnectedPlayer.NickName };
        voosEngine.BroadcastMessage("PlayerLeft", message);
        HudNotifications cms = FindObjectOfType<HudNotifications>();
        if (cms != null) cms.AddMessage($"'{disconnectedPlayer.NickName}' left");
        Util.Log($"Direct player {playerId} disconnected.");
      }
      PhotonNetwork.ForgetDirectPeerName(playerId);
      PhotonNetwork.ReleaseDirectPlayerId(clientId);
      PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
      return;
    }

    if (!directNetworkManager.IsServer
      && (clientId == directNetworkManager.LocalClientId || clientId == NetworkManager.ServerClientId)
      && GameBuilderApplication.CurrentGameOptions.playOptions.isMultiplayer)
    {
#if !USE_STEAMWORKS
      FailOutstandingDirectWorkshopAssetRequests("The direct-host connection closed before the asset was received.");
#endif
      bool wasKicked = wasDirectlyKicked;
      directHostInitAssembler.Reset();
      directDisconnectedLocalPlayerId = PhotonNetwork.player == null ? 0 : PhotonNetwork.player.ID;
      directMigrationCandidate = false;
      directHasMigrationTicket = DirectSessionDirectory.TryGetMigrationTicket(out directMigrationTicket);
      if (directHasMigrationTicket && !wasKicked)
        directMigrationCandidate = directDisconnectedLocalPlayerId > 1
          && PhotonNetwork.GetLowestDirectPlayerId() == directDisconnectedLocalPlayerId;
      PhotonNetwork.MarkDirectSessionDisconnected();
      string disconnectError = receivedPlayerInitPayload
        ? "The host disconnected."
        : "The host disconnected before sending the game state.";
      if (ShouldRecoverDirectDisconnect(receivedPlayerInitPayload, wasKicked))
        HandleDirectHostDisconnected(disconnectError);
      else if (!wasKicked)
        OnFatalError(disconnectError);
    }
  }

  static bool ShouldRecoverDirectDisconnect(bool hasLoadedWorld, bool wasKicked)
  {
    return hasLoadedWorld && !wasKicked;
  }

  void HandleDirectHostDisconnected(string error)
  {
    if (!receivedPlayerInitPayload || autosaves == null)
    {
      OnFatalError(error);
      return;
    }

    if (voosEngine != null) voosEngine.HackyForceSetIsRunning(false);
    autosaves.SetPaused(true);
    try
    {
      autosaves.TriggerAutosave(autosaveBundleId =>
      {
        GameBundleLibrary.Entry autosave = default(GameBundleLibrary.Entry);
        if (!string.IsNullOrEmpty(autosaveBundleId)
          && gameBundleLibrary != null
          && gameBundleLibrary.BundleExists(autosaveBundleId))
        {
          autosave = gameBundleLibrary.GetBundleEntry(autosaveBundleId);
        }
        if (autosave.bundle == null)
        {
          OnFatalError(error + "\nThe local autosave could not be recovered.");
          return;
        }

        if (directHasMigrationTicket)
        {
          directMigrationAbandoned = false;
          if (directMigrationCandidate)
          {
            directMigrationReconnectPending = true;
            scenes.RestartAndLoadLibraryBundle(
              autosave,
              CreateDirectMigrationOptions(directMigrationTicket, null, true));
            return;
          }

          string migrationMessage = error
            + "\nYour local copy is saved. Waiting for another player to restore the session.";
          popups.ShowTwoButtons(
            migrationMessage,
            "Recover Offline",
            () => RecoverDirectAutosave(autosave),
            "Keep Waiting",
            () => { },
            800f);
          DirectSessionDirectory.BeginMigrationWatch(
            directMigrationTicket,
            migrated =>
            {
              if (directMigrationAbandoned) return;
              directMigrationReconnectPending = true;
              scenes.RestartAndLoadLibraryBundle(
                autosave,
                CreateDirectMigrationOptions(directMigrationTicket, migrated, false));
            },
            () =>
            {
              if (!directMigrationAbandoned)
                ShowDirectOfflineRecovery(error, autosave);
            });
          return;
        }

        ShowDirectOfflineRecovery(error, autosave);
      });
    }
    catch (Exception e)
    {
      OnFatalError(error + "\nCould not save the local world: " + e.Message);
    }
  }

  GameBuilderApplication.GameOptions CreateDirectMigrationOptions(
    DirectSessionDirectory.MigrationTicket ticket,
    DirectSessionDirectory.SessionInfo migrated,
    bool startHost)
  {
    GameBuilderApplication.GameOptions current = GameBuilderApplication.CurrentGameOptions;
    string migrationName = migrated != null && !string.IsNullOrWhiteSpace(migrated.Name)
      ? migrated.Name
      : !string.IsNullOrWhiteSpace(ticket.Name) ? ticket.Name : current.displayName;
    GameBuilderApplication.GameOptions options = new GameBuilderApplication.GameOptions
    {
      displayName = migrationName,
      directHostPassword = current.directHostPassword,
      directHostMigrationName = migrationName,
      playOptions = new GameBuilderApplication.PlayOptions
      {
        isMultiplayer = true,
        startAsPublic = ticket.IsPublic,
        startInBuildMode = current.playOptions.startInBuildMode
      },
      directHostMigrationSessionId = ticket.SessionId,
      directHostMigrationToken = ticket.Token,
      directHostMigrationGeneration = migrated != null ? migrated.Generation : ticket.Generation,
      directHostMigrationPreviousPlayerId = directDisconnectedLocalPlayerId
    };
    if (startHost)
    {
      options.directHostUsesIpv6 = current.directHostUsesIpv6;
      options.joinPort = 0;
      options.directHostMigrationReservedPlayerIds = BuildDirectMigrationReservedPlayerIds(
        directDisconnectedLocalPlayerId);
    }
    else
    {
      options.joinAddress = migrated.Address;
      options.joinPort = (ushort)migrated.Port;
      options.directHostUsesIpv6 = migrated.AddressFamily == 6;
      options.trustedHostCertificatePem = migrated.ServerCertificatePem;
      options.directRelaySessionId = migrated.RelayPort >= 1 && migrated.RelayPort <= 65535
        ? migrated.SessionId
        : null;
      options.directRelayAddress = migrated.RelayAddress;
      options.directRelayPort = migrated.RelayPort >= 1 && migrated.RelayPort <= 65535
        ? (ushort)migrated.RelayPort
        : (ushort)0;
    }
    return options;
  }

  static string BuildDirectMigrationReservedPlayerIds(int localPlayerId)
  {
    List<int> playerIds = new List<int>();
    PhotonNetwork.AppendKnownDirectPlayerIds(playerIds);
    playerIds.RemoveAll(playerId => playerId <= 1 || playerId == localPlayerId);
    if (playerIds.Count == 0) return string.Empty;
    playerIds.Sort();
    StringBuilder result = new StringBuilder(playerIds.Count * 3);
    for (int i = 0; i < playerIds.Count; i++)
    {
      if (i > 0) result.Append(',');
      result.Append(playerIds[i]);
    }
    return result.ToString();
  }

  void ShowDirectOfflineRecovery(string error, GameBundleLibrary.Entry autosave)
  {
    string userMessage = error + "\nYour local copy was autosaved. Recover it to continue offline.";
    Util.LogError(userMessage);
    popups.ShowTwoButtons(
      userMessage,
      "Recover Autosave",
      () => RecoverDirectAutosave(autosave),
      "Go to Main Menu",
      () =>
      {
        directMigrationAbandoned = true;
        directMigrationReconnectPending = false;
        DirectSessionDirectory.ClearMigrationTicket();
        PhotonNetwork.ClearDirectSession();
        scenes.LoadSplashScreen();
      },
      800f);
  }

  void RecoverDirectAutosave(GameBundleLibrary.Entry autosave)
  {
    directMigrationAbandoned = true;
    directMigrationReconnectPending = false;
    DirectSessionDirectory.ClearMigrationTicket();
    PhotonNetwork.ClearDirectSession();
    scenes.RestartAndLoadLibraryBundle(autosave, new GameBuilderApplication.PlayOptions());
  }


  byte[] CreateDirectHostInitPacket()
  {
    NewPlayerInitPayload payload = CreateNewPlayerInitPayload();

    byte[] metaBytes = Util.GZip(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
    byte[] voosBytes = GetVoosInitBytes();
    byte[] terrainBytes = terrain.SerializeTerrainV2();
    int totalBytes = metaBytes.Length + voosBytes.Length + terrainBytes.Length;
    if (totalBytes > MaximumPlayerInitKbs * 1024)
    {
      throw new InvalidOperationException(
        $"This game is too large for direct multiplayer ({totalBytes / 1024} KB; limit {MaximumPlayerInitKbs} KB).");
    }

    using (MemoryStream stream = new MemoryStream(totalBytes + 3 * sizeof(int)))
    using (BinaryWriter writer = new BinaryWriter(stream))
    {
      writer.Write(metaBytes.Length);
      writer.Write(metaBytes);
      writer.Write(voosBytes.Length);
      writer.Write(voosBytes);
      writer.Write(terrainBytes.Length);
      writer.Write(terrainBytes);
      return stream.ToArray();
    }
  }

  void OnDirectHostInitMessage(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager.IsServer || senderClientId != NetworkManager.ServerClientId)
    {
      return;
    }

    try
    {
      reader.ReadValueSafe(out int packetLength);
      reader.ReadValueSafe(out int chunkIndex);
      reader.ReadValueSafe(out int chunkCount);
      reader.ReadValueSafe(out int chunkLength);
      byte[] packet = directHostInitAssembler.Append(packetLength, chunkIndex, chunkCount, chunkLength, reader);
      if (packet == null) return;

      using (MemoryStream stream = new MemoryStream(packet, false))
      using (BinaryReader binaryReader = new BinaryReader(stream))
      {
        byte[] metaBytes = ReadInitPacketPart(binaryReader, stream);
        byte[] voosBytes = ReadInitPacketPart(binaryReader, stream);
        byte[] terrainBytes = ReadInitPacketPart(binaryReader, stream);
        if (stream.Position != stream.Length)
        {
          throw new InvalidDataException("Unexpected bytes at the end of the direct-host initial state.");
        }

        InitNewPlayerRPC(metaBytes, voosBytes);
        if (!receivedPlayerInitPayload)
        {
          return;
        }
        InitNewPlayerTerrainRPC(terrainBytes);
        int previousPlayerId = GameBuilderApplication.CurrentGameOptions.directHostMigrationPreviousPlayerId;
        if (previousPlayerId > 1 && previousPlayerId != PhotonNetwork.player.ID)
          ReassignDirectPlayerOwnership(previousPlayerId, PhotonNetwork.player.ID);
      }

      StartCoroutine(DirectClientInitRoutine());
    }
    catch (Exception e)
    {
      directHostInitAssembler.Reset();
      OnFatalError($"Could not load the game state from the host.\n{e.Message}");
    }
  }

  sealed class DirectHostInitChunkAssembler
  {
    byte[] packet;
    int expectedChunkCount;
    int nextChunkIndex;

    public byte[] Append(int packetLength, int chunkIndex, int chunkCount, int chunkLength, FastBufferReader reader)
    {
      int maximumPacketBytes = MaximumPlayerInitKbs * 1024 + 3 * sizeof(int);
      if (packetLength <= 0 || packetLength > maximumPacketBytes)
        throw new InvalidDataException($"Invalid direct-host initial-state size: {packetLength} bytes.");

      int expectedCount = (packetLength + DirectHostInitChunkBytes - 1) / DirectHostInitChunkBytes;
      if (chunkCount != expectedCount || chunkIndex < 0 || chunkIndex >= chunkCount)
        throw new InvalidDataException("Invalid direct-host initial-state chunk index or count.");

      if (packet == null)
      {
        if (chunkIndex != 0)
          throw new InvalidDataException("Direct-host initial-state chunks arrived out of order.");
        packet = new byte[packetLength];
        expectedChunkCount = chunkCount;
        nextChunkIndex = 0;
      }

      if (packet.Length != packetLength || expectedChunkCount != chunkCount || chunkIndex != nextChunkIndex)
        throw new InvalidDataException("Direct-host initial-state chunk sequence changed mid-transfer.");

      int offset = chunkIndex * DirectHostInitChunkBytes;
      int expectedLength = Math.Min(DirectHostInitChunkBytes, packetLength - offset);
      if (chunkLength != expectedLength || reader.Length - reader.Position != chunkLength)
        throw new InvalidDataException("Invalid direct-host initial-state chunk length.");

      reader.ReadBytesSafe(ref packet, chunkLength, offset);
      nextChunkIndex++;
      if (nextChunkIndex != expectedChunkCount) return null;

      byte[] completedPacket = packet;
      Reset();
      return completedPacket;
    }

    public void Reset()
    {
      packet = null;
      expectedChunkCount = 0;
      nextChunkIndex = 0;
    }
  }

  byte[] ReadInitPacketPart(BinaryReader reader, MemoryStream stream)
  {
    int length = reader.ReadInt32();
    if (length < 0 || length > MaximumPlayerInitKbs * 1024 || length > stream.Length - stream.Position)
    {
      throw new InvalidDataException($"Invalid initial-state section length: {length} bytes.");
    }

    byte[] bytes = reader.ReadBytes(length);
    if (bytes.Length != length)
    {
      throw new EndOfStreamException("The host's initial state ended unexpectedly.");
    }
    return bytes;
  }

  IEnumerator DirectClientInitRoutine()
  {
    while (!receivedPlayerInitPayload)
    {
      if (directNetworkManager == null || !directNetworkManager.IsConnectedClient)
      {
        OnFatalError("The connection to the host was lost before the game loaded.");
        yield break;
      }
      yield return null;
    }

    PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
    SpawnLocalObjects();
    yield return StartCoroutine(LoadingSequence());
    SendDirectPlayerReady();
  }
  void OnFatalError(string error, string errorForAnalytics = null)
  {
    Util.LogError(error);

    popups.Show(error, "OK", () =>
    {
      scenes.LoadSplashScreen();
    });
  }

#if USE_PUN
  static int AlreadyJoinedCode = 32746;

  public override void OnPhotonJoinRoomFailed(object[] codeAndMsg)
  {
    if (codeAndMsg[0].ToString() == AlreadyJoinedCode.ToString())
    {
      Util.Log($"Ignoring 'already joined..' error, since Photon will just auto retry anyway.");
      return;
    }
    string roomToJoin = GameBuilderApplication.CurrentGameOptions.joinCode;
    string displayError = $"Sorry, couldn't join room '{roomToJoin}' :(\nAre you sure you typed it correctly?\nMaybe one of you is on EXPERIMENTAL and the other is not?\nError: {codeAndMsg[1]} (code {codeAndMsg[0]})";
    OnFatalError(displayError, $"OnPhotonJoinRoomFailed. Error: {codeAndMsg[1]} (code {codeAndMsg[0]})");

    // Clear the last joined room, if it's the same one
    if (PlayerPrefs.GetString(LastJoinedRoomPrefKey, "") == roomToJoin)
    {
      PlayerPrefs.DeleteKey(LastJoinedRoomPrefKey);
      PlayerPrefs.Save();
    }
  }

  public static string GenerateUniqueRoomName()
  {
#if UNITY_EDITOR
    return $"dev-{System.Net.Dns.GetHostName().ToLowerInvariant()}";
#else
    return ((int)UnityEngine.Random.Range(0, 999999)).ToString();
#endif
  }

  public void TryCreateRoom()
  {
    var props = new ExitGames.Client.Photon.Hashtable();
    props.Add(GameDisplayNameRoomProperty, GameBuilderApplication.CurrentGameOptions.displayName);
    RoomOptions roomOptions = new RoomOptions() { MaxPlayers = (byte)GetMaxPlayers(), IsVisible = false, CustomRoomProperties = props, CustomRoomPropertiesForLobby = PropertiesToShowInLobby };

    // Bake the region code into the code so others know..Ideally we'd let you
    // pick the region, in case your friends are far..
    int regionNumber = (int)PhotonNetwork.CloudRegion;
    string roomName = $"{regionNumber}{JoinCodeSeparator}{GenerateUniqueRoomName()}";
    Util.Log($"Trying to create room {roomName}, game name = {GameBuilderApplication.CurrentGameOptions.displayName}");

    PhotonNetwork.CreateRoom(roomName, roomOptions, null);
    lastRoomNameAttempted = roomName;
  }

  public override void OnPhotonCreateRoomFailed(object[] codeAndMsg)
  {
    Util.LogWarning($"Failed to create room '{lastRoomNameAttempted}' :( Error: {codeAndMsg.ToString()}. Trying again..");
    TryCreateRoom();
  }

  public override void OnPhotonRandomJoinFailed(object[] codeAndMsg)
  {
    TryCreateRoom();
  }

  public override void OnConnectedToMaster()
  {
    string roomToJoin = GameBuilderApplication.CurrentGameOptions.joinCode;
    if (roomToJoin == "*")
    {
      PhotonNetwork.JoinLobby();
    }
    else if (roomToJoin != null)
    {
      Util.Log($"Trying to join room {roomToJoin}..");
      // Try to join existing room
      PhotonNetwork.JoinRoom(roomToJoin.ToLower());
    }
    else
    {
      TryCreateRoom();
    }
  }

  public override void OnFailedToConnectToPhoton(DisconnectCause cause)
  {
    OnFatalError($"Woops! Could not connect to the master server. Cause:\n{cause.ToString()}");
  }

  IEnumerator OnJoinedRoomCoroutine()
  {
    // Always wait 1 frame. This is because Photon, in offline mode, can calls
    // this synchronously within TryCreateRoom - bad. So, we'll deal with it.
    yield return null;

    // For some reason..this happens sometimes.
    while (PhotonNetwork.room == null) yield return null;

    lastRoomJoined = PhotonNetwork.room.Name;
    Debug.Log($"Joined room '{lastRoomJoined}'");

    PlayerPrefs.SetString(LastJoinedRoomPrefKey, lastRoomJoined);
    PlayerPrefs.Save();

    if (PhotonNetwork.isMasterClient)
    {
      if (!PhotonNetwork.offlineMode)
      {

        // Setup room properties for the in-lobby game browser.
        var gameParams = GameBuilderApplication.CurrentGameOptions;
        var props = (ExitGames.Client.Photon.Hashtable)PhotonNetwork.room.CustomProperties;
        props[(object)GameDisplayNameRoomProperty] = (object)gameParams.displayName;
        props[(object)ThumbnailZippedJpegRoomProperty] = (object)gameParams.thumbnailZippedJpegBytes;
        PhotonNetwork.room.SetCustomProperties(props);

        PhotonNetwork.room.IsVisible = gameParams.playOptions.startAsPublic;
      }
      MasterClientInit();

      didSwitchMaps = false;
    }
    else
    {
      // This should never happen in offline mode...but just in case
      if (!PhotonNetwork.offlineMode)
      {
      }
      StartCoroutine(NonMasterClientInit());
    }
  }

  public override void OnJoinedRoom()
  {
    StartCoroutine(OnJoinedRoomCoroutine());
  }
#endif

  private void OnDestroy()
  {
#if USE_PUN
    switch (state)
    {
      case State.SwitchingScene:
        return;
      default:
        PhotonNetwork.Disconnect();
        return;
    }
#else
#if !USE_STEAMWORKS
    FailOutstandingDirectWorkshopAssetRequests("The direct-host connection closed before the asset was received.");
#endif
    DirectSessionDirectory.MigrationTicket migrationTicket;
    bool preservePlayerIdentity = directMigrationReconnectPending
      && DirectSessionDirectory.TryGetMigrationTicket(out migrationTicket)
      && GameBuilderApplication.CurrentGameOptions.playOptions.isMultiplayer;
    bool keepDirectConnection = state == State.SwitchingScene
      && directNetworkManager != null
      && directNetworkManager.IsListening;
    preservePlayerIdentity |= keepDirectConnection;
    if (directNetworkManager != null)
    {
      directNetworkManager.OnClientConnectedCallback -= OnDirectClientConnected;
      directNetworkManager.OnClientDisconnectCallback -= OnDirectClientDisconnected;
      directClientsAwaitingReconnect.Clear();
      if (registeredDirectInitHandler && directNetworkManager.CustomMessagingManager != null)
      {
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectHostInitMessageName);
        registeredDirectInitHandler = false;
      }
      if (registeredDirectPlayerHandlers && directNetworkManager.CustomMessagingManager != null)
      {
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerAssignmentMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReconnectMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectSessionMigrationTicketMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReadyMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerSpawnMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerDespawnMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerIdentityMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerInitRequestMessageName);
#if !USE_STEAMWORKS
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectWorkshopAssetRequestMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectWorkshopAssetResponseMessageName);
#endif
        registeredDirectPlayerHandlers = false;
      }
      if (directNetworkManager.IsListening && !keepDirectConnection)
      {
        directNetworkManager.Shutdown();
      }
    }
    if (!preservePlayerIdentity) PhotonNetwork.ClearDirectSession();
#endif
  }

  public string lastLoadedBundleId { get; private set; }

  void MasterClientInit()
  {
    SaveGameToLoad saveGameToLoad = FindObjectOfType<SaveGameToLoad>();
    string bundleId = GameBuilderApplication.CurrentGameOptions.bundleIdToLoad;

    if (saveGameToLoad)
    {
      Debug.Log("Loading saved game!");
      saveLoad.Load(saveGameToLoad.saved, saveGameToLoad.voosFilePath);
      GameObject.Destroy(saveGameToLoad.gameObject);
    }
    else if (!bundleId.IsNullOrEmpty())
    {
      Debug.Log($"Loading game bundle {bundleId}");
      string voosPath = gameBundleLibrary.GetBundle(bundleId).GetVoosPath();
      saveLoad.Load(SaveLoadController.ReadSaveGame(voosPath), voosPath);
#if !USE_STEAMWORKS
      workshop.Load(gameBundleLibrary.GetBundle(bundleId).GetAssetsPath());
#endif
      lastLoadedBundleId = bundleId;
    }
    else
    {
      SaveLoadController.SaveGame save = SaveLoadController.ReadSaveGame(GameBuilderSceneController.GetMinimalScenePath(mode == Mode.Online));
      saveLoad.Load(save);
    }
#if !USE_PUN
    GameBuilderApplication.GameOptions migrationOptions = GameBuilderApplication.CurrentGameOptions;
    if (migrationOptions.playOptions.isMultiplayer
      && string.IsNullOrEmpty(migrationOptions.joinAddress)
      && migrationOptions.directHostMigrationPreviousPlayerId > 1
      && voosEngine != null)
    {
      int previousHostPlayerId = migrationOptions.directHostMigrationPreviousPlayerId;
      foreach (VoosActor actor in voosEngine.EnumerateActors())
      {
        if (actor.reliablePhotonView == null || actor.reliablePhotonView.ownerId != previousHostPlayerId) continue;
        actor.reliablePhotonView.ApplyOwnershipAndNotify(1);
      }
    }
#endif

    using (Util.Profile("SpawnLocalobjects"))
      SpawnLocalObjects();
#if !USE_PUN
    if (directNetworkManager != null && directNetworkManager.IsServer && directNetworkManager.IsListening)
      directWorldReady = true;
#endif
    StartCoroutine(LoadingSequence());
  }

#if USE_PUN
  IEnumerator NonMasterClientInit()
  {
    // Wait for VoosEngine to be init'd.
    while (!receivedPlayerInitPayload)
    {
      Util.Log($"Waiting for network initialization from master..");
      yield return new WaitForSecondsRealtime(1f);
    }

    if (!PhotonNetwork.connected || !PhotonNetwork.inRoom)
    {
      // We got disconnected after the init message...very possible if it's too
      // big. The player should be seeing the popup.
      yield break;
    }

    SpawnLocalObjects();
    StartCoroutine(LoadingSequence());
  }
#endif

  bool IsLoadingResources()
  {
    return assetCache.GetNumActiveDownloads() > 0
    || !terrain.HasReachedSteadyState();
  }

  IEnumerator LoadingSequence()
  {
    float t0 = Time.realtimeSinceStartup;
    loadingScreen.Show();
    yield return new WaitForSecondsRealtime(0.5f);

    System.Action startPlaying = () =>
    {
      float t1 = Time.realtimeSinceStartup;
      loadingScreen.FadeAndHide();
      voosEngine.SetIsRunning(true);

      GameObject.FindObjectOfType<UserMain>().FadeInAudioAfterLoading();
    };

    if (!IsLoadingResources())
    {
      startPlaying();
      yield break;
    }

    loadingScreen.SetCancelButton("Start Playing Anyway", () => startPlaying());

    int totalDownloads = 1;
    int totalChunks = 1;

    while (IsLoadingResources())
    {
      // We compute the # of total downloads in this weird and very approximate way,
      // but it's not like progress bars ever tell the truth anyway.
      // This is necessary because not all downloads may be immediately queueud.
      int downloadsRemaining = assetCache.GetNumActiveDownloads();
      totalDownloads = Mathf.Max(downloadsRemaining, totalDownloads);

      int chunksRemaining = terrain.GetNumImportantChunksRemaining();
      totalChunks = Mathf.Max(chunksRemaining, totalChunks);

      float progress = 1 - ((downloadsRemaining + chunksRemaining) * 1f / (totalDownloads + totalChunks));

      string text = "";

      if (downloadsRemaining > 0)
      {
        text += $"Loading models, {downloadsRemaining} remaining.";
      }

      if (chunksRemaining > 0)
      {
        text += $"\nGenerating terrain, {chunksRemaining} chunks remaining.";
      }

      loadingScreen.SetProgress(progress);
      loadingScreen.SetStatusText(text);

      yield return new WaitForSecondsRealtime(0.1f);
    }

    // One last little bit to reduce chances of people falling through the
    // ground..
    yield return new WaitForSecondsRealtime(0.5f);
    startPlaying();
  }

  private void SpawnLocalObjects()
  {
    using (Util.Profile("disableOnJoinRoom"))
    {
      if (disableOnJoinRoom != null)
      {
        disableOnJoinRoom.SetActive(false);
      }
    }

    using (Util.Profile("localUserBody"))
    {
      Debug.Assert(localUserInstance == null);
      Vector3 pos = new Vector3(0, 1.5f, 0);
#if USE_PUN
      localUserInstance = PhotonNetwork.Instantiate(userBodyPrefab.name, pos, Quaternion.identity, 0);
#else
      localUserInstance = Instantiate(userBodyPrefab.gameObject, pos, Quaternion.identity);
#endif
      UserBody localUserBody = localUserInstance.GetComponent<UserBody>();
#if !USE_PUN
      if (mode == Mode.Online)
      {
        if (directNetworkManager != null && directNetworkManager.IsListening)
          PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
        PhotonView userView = localUserBody.GetComponent<PhotonView>();
        userView.SetViewId(PhotonNetwork.AllocateViewID());
        userView.ApplyOwnership(PhotonNetwork.player.ID);
      }
#endif
      localUserBody.Setup(true);

      using (Util.Profile("localPrefab"))
      {
        if (localPrefab != null)
        {
          var localInstance = GameObject.Instantiate(localPrefab, null);
          LocalUserObjects localInterface = localInstance.GetComponent<LocalUserObjects>();
          if (localInterface != null)
          {
            localInterface.Initialize(localUserBody);
          }
        }
      }
    }
  }

  public string GetPlayerSteamID(PhotonPlayer player)
  {
    object steamId = "(N/A)";
#if USE_PUN
    player.CustomProperties.TryGetValue((object)NetworkingController.SteamIdPlayerProperty, out steamId);
#endif
    return (string)steamId;
  }

#if USE_PUN
  public override void OnPhotonPlayerConnected(PhotonPlayer newPlayer)
  {
    // HACK HACK. Only because CMS is tied to player panels, when it really should be global.
    var cms = FindObjectOfType<HudNotifications>();
    if (cms != null)
    {
      Util.Log($"'{newPlayer.NickName}' joined! SteamID: {GetPlayerSteamID(newPlayer)}");
      cms.AddMessage($"'{newPlayer.NickName}' joined!");
    }

    if (PhotonNetwork.isMasterClient)
    {
      System.DateTime banLastDate;
      if (kickedPlayerNickNames.Contains(newPlayer.NickName)
        || IsUserBanned(GetPlayerSteamID(newPlayer), out banLastDate)
        )
      {
        // RE-KICK!
        KickPlayer(newPlayer);
      }
      else
      {
        // NOTE: Ideally we'd be leveraging the rate-limit system and enqueueing
        // this player into playerInitQueue. However, that is currently broken.
        // Upon a new player joining, Photon resets all ReliableDeltaCompressed
        // views so the new player gets the full view data, then it can resume
        // sending deltas only. However, it does this IMMEDIATELY. So if we
        // don't send the init payload ALSO immediately, the new player won't
        // create those views, and the full view data messages will just get
        // dropped. This means, subsequent delta-messages will get dropped, and
        // RDC views are effectively non-functional for the new player. Bad.
        StartCoroutine(InitializeNewPlayer(newPlayer));
      }
    }
  }

  public override void OnPhotonPlayerDisconnected(PhotonPlayer otherPlayer)
  {
    Util.Log($"Player {otherPlayer.ID} disconnected.");

    var cms = FindObjectOfType<HudNotifications>();
    if (cms != null)
    {
      cms.AddMessage($"'{otherPlayer.NickName}' left");
    }

    if (!PhotonNetwork.isMasterClient)
    {
      return;
    }

    // Inefficient and global, but whatever...
    foreach (UserBody body in FindObjectsOfType<UserBody>())
    {
      if (body.GetComponent<PhotonView>().OwnerActorNr == otherPlayer.ID)
      {
        PhotonNetwork.Destroy(body.gameObject);
      }
    }

    // Reclaim all actors owned by this player. A bit surprised photon doesn't
    // do this for us..
    foreach (VoosActor actor in voosEngine.EnumerateActors())
    {
      if (actor.reliablePhotonView != null && actor.reliablePhotonView.OwnerActorNr == otherPlayer.ID)
      {
        // Why not call actor.RequestOwnership here? That would result in
        // nothing happening, because the other player is definitely gone. But
        // calling view.RequestOwnership() directly will go through the normal
        // Photon flow, bypassing all our logic, and Photon should just grant us
        // ownership.
        actor.reliablePhotonView.RequestOwnership();
      }
    }

    // Broadcast a message saying the player disconnected.
    PlayerLeftMessage playerLeftMessage = new PlayerLeftMessage();
    playerLeftMessage.id = otherPlayer.ID;
    playerLeftMessage.nickName = otherPlayer.NickName;
    voosEngine.BroadcastMessage("PlayerLeft", playerLeftMessage);
  }

  void OnInvalidJoinCodeRegion()
  {
    int maxRegionVal = 0;
    foreach (CloudRegionCode code in Enum.GetValues(typeof(CloudRegionCode)))
    {
      maxRegionVal = Math.Max((int)code, maxRegionVal);
    }
    OnFatalError($"Sorry, it looks like you typed an invalid join code.\nThe first number before the hyphen should be between 0 and {maxRegionVal}.");
  }

  public override void OnConnectionFail(DisconnectCause cause)
  {
    if (cause == DisconnectCause.InvalidRegion)
    {
      OnInvalidJoinCodeRegion();
    }
    else
    {
      voosEngine.HackyForceSetIsRunning(false);

      if (Time.unscaledTime > 10f)
      {
        autosaves.SetPaused(true);
        autosaves.TriggerAutosave(autosaveBundleId =>
        {
          if (numOthersWhenConnected == 0)
          {
            string userMessage = $"Woops! You were disconnected. Reason:\n{cause.ToString()}\nWe've auto-saved the project, so you can recover it and restart the multiplayer session.";
            Util.LogError(userMessage);
            popups.ShowTwoButtons(userMessage,
              "Recover Autosave",
              () =>
              {
                scenes.RestartAndLoadLibraryBundle(gameBundleLibrary.GetBundleEntry(autosaveBundleId), new GameBuilderApplication.PlayOptions());
              },
              "Go to Main Menu",
              () => scenes.LoadSplashScreen(),
              800f
            );
          }
          else
          {
            string userMessage = $"Woops! You were disconnected. Reason:\n{cause.ToString()}. Please reconnect.\n<size=80%><color=#888888>NOTE: If the multiplayer game is gone, you can recover the autosave in your Game Library.</color></size>";
            Util.LogError(userMessage);
            popups.ShowTwoButtons(
              userMessage,
              $"Reconnect to {roomNameWhenConnected}",
              () => scenes.JoinMultiplayerGameByCode(roomNameWhenConnected),
              "Go to Main Menu",
              () => scenes.LoadSplashScreen(),
              800f);
          }
        });
      }
      else
      {
        string userMessage = $"Woops! You were disconnected. Reason:\n{cause.ToString()}.";
        Util.LogError(userMessage);
        popups.Show(userMessage, "Back to Main Menu", () =>
        {
          scenes.LoadSplashScreen();
        });
      }
    }
  }
#endif

  public void KickPlayer(PhotonPlayer player)
  {
    if (player == null) return;
#if USE_PUN
    // Courtesy RPC.
    photonView.RPC("KickRPC", player);
    PhotonNetwork.CloseConnection(player);
    kickedPlayerNickNames.Add(player.NickName);
#else
    if (!IsDirectHost() || player.ID <= 1 || directNetworkManager == null || !directNetworkManager.IsListening) return;
    ulong? clientId = PhotonNetwork.GetDirectClientId(player.ID);
    if (!clientId.HasValue || clientId.Value == directNetworkManager.LocalClientId) return;
    kickedPlayerNickNames.Add(player.NickName);
    Util.Log($"Sending direct kick RPC to player {player.ID} (client {clientId.Value}, view {photonView.viewID}).");
    photonView.RPC("KickRPC", player);
    StartCoroutine(DisconnectDirectClientAfterKick(player.ID));
#endif
  }

#if !USE_PUN
  IEnumerator DisconnectDirectClientAfterKick(int playerId)
  {
    yield return new WaitForSecondsRealtime(0.5f);
    if (directNetworkManager == null || !directNetworkManager.IsServer) yield break;
    ulong? clientId = PhotonNetwork.GetDirectClientId(playerId);
    if (clientId.HasValue && clientId.Value != directNetworkManager.LocalClientId)
      directNetworkManager.DisconnectClient(clientId.Value);
  }
#endif

  [PunRPC]
  void KickRPC(PhotonMessageInfo info)
  {
    Util.Log($"Kick RPC invoked; sender={(info.sender == null ? "none" : info.sender.ID.ToString())}.");
    if (info.sender == null || PhotonNetwork.masterClient == null || info.sender.ID != PhotonNetwork.masterClient.ID) return;
    wasDirectlyKicked = true;
    PhotonNetwork.DisconnectDirectSessionPreservingPlayers();
    popups.Show("You were kicked.", "Back to Main Menu", () =>
    {
      scenes.LoadSplashScreen();
    });
  }

  [PunRPC]
  void JoinRoomRPC(string joinCode)
  {
    // If we don't explicitly leave, there's a chance the room will still think
    // we're in when we try to reconnect..thus saying "you're already in here!!"
    PhotonNetwork.LeaveRoom();

    // Trigger main scene reload. This will end up reconnecting to the region server, which is a bit wasteful, but OK.
    scenes.LoadMainSceneAsync(new GameBuilderApplication.GameOptions { playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }, joinCode = joinCode });
  }
  [PunRPC]
  void SwitchDirectMapRPC(PhotonMessageInfo info)
  {
    if (info.sender == null || PhotonNetwork.masterClient == null || info.sender.ID != PhotonNetwork.masterClient.ID) return;
#if !USE_PUN
    if (directNetworkManager == null || directNetworkManager.IsServer || !directNetworkManager.IsListening) return;
    GameBuilderApplication.GameOptions currentOptions = GameBuilderApplication.CurrentGameOptions;
    GameBuilderApplication.GameOptions options = new GameBuilderApplication.GameOptions
    {
      displayName = currentOptions.displayName,
      joinAddress = currentOptions.joinAddress,
      joinPort = currentOptions.joinPort,
      directHostUsesIpv6 = currentOptions.directHostUsesIpv6,
      trustedHostCertificatePem = currentOptions.trustedHostCertificatePem,
      directHostPassword = currentOptions.directHostPassword,
      directRelaySessionId = currentOptions.directRelaySessionId,
      directRelayAddress = currentOptions.directRelayAddress,
      directRelayPort = currentOptions.directRelayPort,
      directHostMigrationSessionId = currentOptions.directHostMigrationSessionId,
      directHostMigrationToken = currentOptions.directHostMigrationToken,
      directHostMigrationName = currentOptions.directHostMigrationName,
      directHostMigrationGeneration = currentOptions.directHostMigrationGeneration,
      directHostMigrationReservedPlayerIds = currentOptions.directHostMigrationReservedPlayerIds,
      directHostMigrationPreviousPlayerId = currentOptions.directHostMigrationPreviousPlayerId,
      playOptions = currentOptions.playOptions
    };
    state = State.SwitchingScene;
    PhotonNetwork.ResetDirectPeers();
    scenes.LoadMainSceneAsync(options);
#endif
  }

  void OnBeforeReloadMainScene()
  {
#if USE_PUN
    if (mode == Mode.Online && PhotonNetwork.connected)
    {
      string newJoinCode = GameBuilderApplication.CurrentGameOptions.joinCode;
      bool tryingToJoinNewRoom = !newJoinCode.IsNullOrEmpty() && newJoinCode != PhotonNetwork.room.Name;
      bool keepRoom = PhotonNetwork.isMasterClient && !tryingToJoinNewRoom;

      if (!keepRoom)
      {
        Util.Log("Disconnecting, and probably reconnecting into a new game.");
        PhotonNetwork.Disconnect();
      }
      else
      {
        Util.Log("Staying online while switching multiplayer maps.");
        photonView.RPC("JoinRoomRPC", PhotonTargets.Others, GetJoinedRoomName());
        PhotonNetwork.DestroyAll();
        state = State.SwitchingScene;
      }
    }
#else
    if (mode == Mode.Online
      && directNetworkManager != null
      && directNetworkManager.IsServer
      && directNetworkManager.IsListening
      && GameBuilderApplication.CurrentGameOptions.playOptions.isMultiplayer)
    {
      directWorldReady = false;
      directInitResponseClients.Clear();
      photonView.RPC("SwitchDirectMapRPC", PhotonTargets.Others);
      state = State.SwitchingScene;
    }
#endif
  }

  void Update()
  {
    if (PhotonNetwork.connected)
    {
      if (PhotonNetwork.isMasterClient && !wasMasterWhenConnected && onlineSeconds > 10f)
      {
        popups.Show($"You are now the master client! The previous master was disconnected. If you keep the game running, they can probably reconnect and resume normally.", "Continue", null, 800f);
      }
      wasMasterWhenConnected = PhotonNetwork.isMasterClient;
      numOthersWhenConnected = PhotonNetwork.otherPlayers.Length;
      roomNameWhenConnected = PhotonNetwork.room?.Name;
    }

    if (PhotonUtil.ActuallyConnected())
    {
      onlineSeconds += Time.unscaledDeltaTime;
    }

#if !USE_PUN
    PhotonNetwork.ExpireDirectMigrationReservations(Time.realtimeSinceStartup);
#if !USE_STEAMWORKS
    ExpireDirectWorkshopAssetRequests(Time.realtimeSinceStartup);
#endif
    if (directNetworkManager != null && directNetworkManager.IsListening && directNetworkManager.IsServer)
      BroadcastDirectMigrationTicketIfChanged();
#endif

    // TEMP TEMP
    if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.F2))
    {
      PhotonNetwork.room.IsVisible = !PhotonNetwork.room.IsVisible;
      Util.Log($"toggled room visible to {PhotonNetwork.room.IsVisible}");
    }
  }

#if !USE_PUN
  void BroadcastDirectMigrationTicketIfChanged()
  {
    if (!DirectSessionDirectory.TryGetMigrationTicket(out DirectSessionDirectory.MigrationTicket ticket)
      || (ticket.SessionId == lastBroadcastMigrationSessionId
        && ticket.Token == lastBroadcastMigrationToken
        && ticket.Name == lastBroadcastMigrationName
        && ticket.Generation == lastBroadcastMigrationGeneration
        && ticket.IsPublic == lastBroadcastMigrationIsPublic)) return;
    foreach (ulong clientId in directNetworkManager.ConnectedClientsIds)
      if (clientId != directNetworkManager.LocalClientId)
        SendDirectMigrationTicket(clientId);
    lastBroadcastMigrationSessionId = ticket.SessionId;
    lastBroadcastMigrationToken = ticket.Token;
    lastBroadcastMigrationName = ticket.Name;
    lastBroadcastMigrationGeneration = ticket.Generation;
    lastBroadcastMigrationIsPublic = ticket.IsPublic;
  }
#endif

  public int GetNumOtherPlayers()
  {
#if USE_PUN
    return PhotonNetwork.otherPlayers.Length;
#else
    if (directNetworkManager == null || !directNetworkManager.IsConnectedClient)
    {
      return 0;
    }
    return Mathf.Max(
      directNetworkManager.IsServer ? 0 : 1,
      directNetworkManager.ConnectedClientsIds.Count - (directNetworkManager.IsServer ? 1 : 0));
#endif
  }

  public int GetMaxPlayers()
  {
#if USE_PUN
    return 8;
#else
    return PhotonNetwork.MaximumDirectPlayers;
#endif
  }

  // TODO TODO umm is this the same as a save game? Should part of that code be shared?
  [System.Serializable]
  struct NewPlayerInitPayload
  {
    // Hmm...I wonder if we can make this a hash of all the sub-parts' versions.
    // 9 is first version with diggable ground.
    public static int CurrentVersion = 13;
    public int version;
    public Behaviors.Database.Jsonable behaviorDatabase;
    public GameBuilderStage.Persisted stage;
    public SojoDatabase.Saved sojoDatabase;
    public TerrainManager.Metadata terrainMeta;
    public BehaviorSystem.SavedCardPacks cardPacks;
    public SceneActorLibrary.SavedActorPacks actorPacks;
  }

  NewPlayerInitPayload CreateNewPlayerInitPayload()
  {
    return new NewPlayerInitPayload
    {
      version = NewPlayerInitPayload.CurrentVersion,
      behaviorDatabase = behaviorSystem.SaveDatabase(),
      sojoDatabase = sojoSystem.SaveDatabase(),
      terrainMeta = terrain.GetMetadata(),
      stage = stage.Save(),
      cardPacks = behaviorSystem.GetCardPacks(),
      actorPacks = sceneActorLibrary.GetActorPacks()
    };
  }

  void LoadNewPlayerProjectLibraries(NewPlayerInitPayload payload)
  {
    if (payload.cardPacks != null) behaviorSystem.LoadCardPacks(payload.cardPacks);
    if (payload.actorPacks != null) sceneActorLibrary.LoadActorPacks(payload.actorPacks);
  }

  public static string GetPhotonGameVersion()
  {
    // I've thought about just using the build commit here...but that would
    // preclude the pretty useful case of testing by connecting to a build with
    // an editor.
    return NewPlayerInitPayload.CurrentVersion.ToString()
    + "."
    + VoosActor.CurrentVersionNumber.ToString()
    + "."
    + TerrainSystem.serialization_version.ToString()
    + "."
    + TerrainManager.NetworkingVersion.ToString();
  }

  public static void LogJsonSizeStats<T>(T jsonable)
  {
    string json = JsonUtility.ToJson(jsonable);
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    byte[] zipped = Util.GZip(bytes);
    Util.Log($"{typeof(T).FullName}: {bytes.Length / 1024} kb raw, {zipped.Length / 1024} kb zipped");
  }

  public void LogInitPlayerPayloadStats()
  {
    NewPlayerInitPayload payload = CreateNewPlayerInitPayload();

    // Garbage-happy size profiling
    LogJsonSizeStats(payload.behaviorDatabase);
    LogJsonSizeStats(payload.sojoDatabase);
    LogJsonSizeStats(payload.terrainMeta);
    LogJsonSizeStats(payload.stage);

    byte[] terrainBytes = terrain.SerializeTerrainV2();
    Util.Log($"terrain is {terrainBytes.Length / 1024} zipped");
  }

  byte[] VOOS_INIT_BYTES_REUSED = new byte[MaximumPlayerInitKbs * 1024];

  public byte[] GetVoosInitBytes()
  {
    var voosBuffer = VOOS_INIT_BYTES_REUSED;
    Array.Clear(voosBuffer, 0, voosBuffer.Length);
    var writer = new VoosBinaryWriter(voosBuffer);
    voosEngine.SerializePlayerInitPayloadV2(writer);
    return Util.GZip(writer.ToArray());
  }

  IEnumerator InitializeNewPlayer(PhotonPlayer newPlayer)
  {
    NewPlayerInitPayload payload = CreateNewPlayerInitPayload();

    string metaJson = JsonUtility.ToJson(payload);
    byte[] zippedMetaBytes = Util.GZip(Encoding.UTF8.GetBytes(metaJson));
    byte[] zippedVoosBytes = GetVoosInitBytes();
    byte[] terrainV2Bytes = terrain.SerializeTerrainV2();

    if (((zippedMetaBytes.Length + zippedVoosBytes.Length + terrainV2Bytes.Length) / 1024) > MaximumPlayerInitKbs)
    {
      throw new System.Exception($"Sorry, your game has become too large for multiplayer. We recommend loading it in single player and removing some unnecessary actors, assets, or cards. If you need more help, please post to the Steam discussion forums.");
    }

    Util.Log($"Sending {voosEngine.GetNumActors()} actors and to new player {newPlayer.ID}, zippedMetaBytes: {zippedMetaBytes.Length / 1024} KB, zippedVoosBytes: {zippedVoosBytes.Length / 1024} KB");
    photonView.RPC("InitNewPlayerRPC", newPlayer, zippedMetaBytes, zippedVoosBytes);

    // Send terrain on its own. Avoid the JSON stuff.
    Util.Log($"Sending terrain init msg, {terrainV2Bytes.Length / 1024} kb zipped");
    photonView.RPC("InitNewPlayerTerrainRPC", newPlayer, terrainV2Bytes);

    // IMPORTANT: Also tell ALL players that, after the prior RPCs, the new
    // player should be ready to participate normally. This will trigger
    // everyone to, for example, broadcast fresh snapshots for actors they own
    // to the new player. This was done anyway, by Photon's builtin behavior,
    // but because we manage actor views on our own, the new player wasn't ready
    // to receive those yet. Wasteful, yes, but we need this.
    yield return new WaitForSecondsRealtime(3f);
    photonView.RPC("NewPlayerWasInitializedRPC", PhotonTargets.AllViaServer);

    // NOTE FOR FUTURE: I think the real solution here is to stop relying on
    // Photon's RDC at all. It's not efficient for us (we can do change
    // detection at the setter level..), and having PhotonView coupled to
    // VoosActor's life time is unnecessarily restrictive. Ideally, all we would
    // do is this: VoosEngine would not be allowed to run on the new client
    // until all other players have sent over their local actors' full state.
    // But while VOOS is paused, we still can receive delta updates via RPC.
    // Then when we get all state from all other players, we can start running
    // like normal. This means we don't waste bandwidth by sending all actor
    // state TWICE to the same client, as we do now.

  }

  [PunRPC]
  void NewPlayerWasInitializedRPC()
  {
    // See comment at RPC call site.
    foreach (var actor in voosEngine.EnumerateActors())
    {
      if (actor.reliablePhotonView != null)
      {
        actor.reliablePhotonView.ForceResendToAllPlayers();
      }
    }
  }

  TerrainManager.Metadata lastReceivedTerrainMeta;

  [PunRPC]
  void InitNewPlayerRPC(byte[] zippedMetaBytes, byte[] zippedVoosBytes)
  {
    Util.Log($"InitNewPlayerRPC, {zippedMetaBytes.Length / 1024} KB zippedMetaBytes, {zippedVoosBytes.Length / 1024} KB zippedVoosBytes");
    byte[] unzippedMetaBytes = Util.UnGZip(zippedMetaBytes);
    string payloadJson = Encoding.UTF8.GetString(unzippedMetaBytes, 0, unzippedMetaBytes.Length);
    NewPlayerInitPayload payload = JsonUtility.FromJson<NewPlayerInitPayload>(payloadJson);

    if (payload.version != NewPlayerInitPayload.CurrentVersion)
    {
      OnFatalError($"The game you're trying to join seems to be running an incompatible version of the game.\nQuit the game and make sure Steam has no pending updates.");
      return;
    }

    byte[] unzippedVoosBytes = Util.UnGZip(zippedVoosBytes);
    var voosReader = new VoosBinaryReader(unzippedVoosBytes);

    behaviorSystem.LoadDatabaseForNetworkInit(payload.behaviorDatabase);
    LoadNewPlayerProjectLibraries(payload);
    voosEngine.DeserializePlayerInitV2(voosReader);
    sojoSystem.LoadDatabase(payload.sojoDatabase);
    stage.Load(payload.stage);

    receivedPlayerInitPayload = true;

    lastReceivedTerrainMeta = payload.terrainMeta;
  }

  [PunRPC]
  void InitNewPlayerTerrainRPC(byte[] bytes)
  {
    Util.Log($"InitNewPlayerTerrainRPC, {bytes.Length} bytes");
    Debug.Assert(receivedPlayerInitPayload, "Got InitNewPlayerTerrainRPC before InitNewPlayerRPC");
    terrain.Reset(stage.GetGroundSize(), bytes, false, null, lastReceivedTerrainMeta.customStyleWorkshopIds);
  }

  [PunRPC]
  void TriggerTerrainResetRPC()
  {
    // Passing null for data (and for custom styles) will cause it to copy from what's currently there.
    terrain.Reset(stage.GetGroundSize(), null, false, null);
  }

  public void TriggerTerrainReset()
  {
    photonView.RPC("TriggerTerrainResetRPC", PhotonTargets.AllViaServer);
  }

  public static bool CanDoMultiplayerMapSwitch()
  {
#if USE_PUN
    return PhotonUtil.ActuallyConnected() && PhotonNetwork.inRoom && PhotonNetwork.isMasterClient;
#else
    return NetworkManager.Singleton != null
      && NetworkManager.Singleton.IsListening
      && NetworkManager.Singleton.IsServer
      && PhotonNetwork.inRoom;
#endif
  }

  internal void SendReportToMasterClient(string nickName, string description)
  {
    if (PhotonNetwork.isMasterClient)
    {
      return;
    }

    photonView.RPC("SendReportToMasterClientRPC", PhotonNetwork.masterClient, nickName, description);
  }

  void KickByNickName(string nickName)
  {
    foreach (var player in PhotonNetwork.playerList)
    {
      if (player.NickName == nickName)
      {
        KickPlayer(player);
        break;
      }
    }
  }

  [PunRPC]
  void SendReportToMasterClientRPC(string nickName, string description)
  {
    popups.Show(new DynamicPopup.Popup
    {
      fullWidthButtons = true,
      getMessage = () => $"Player {nickName} has been reported.",
      buttons = new List<PopupButton.Params>(){
        new PopupButton.Params{ getLabel = () => $"Kick {nickName}", onClick = () => KickByNickName(nickName) },
        new PopupButton.Params{ getLabel = () => $"OK", onClick = () => {}}
      }
    });
  }
}
