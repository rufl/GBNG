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
  // From experiments, it seems like 490kb for that one RPC was a guaranteed
  // disconnect. So, let's stay under 400kb to be safe.
  static int MaximumPlayerInitKbs = 400;
  public const ushort DefaultDirectHostPort = 7777;
  const string DirectHostInitMessageName = "GB_DIRECT_HOST_INIT_V1";
  const string DirectPlayerIdentityMessageName = "GB_DIRECT_PLAYER_IDENTITY_V1";
  const string DirectPlayerInitRequestMessageName = "GB_DIRECT_PLAYER_INIT_REQUEST_V1";
  const string DirectPlayerReadyMessageName = "GB_DIRECT_PLAYER_READY_V1";
  const string DirectPlayerSpawnMessageName = "GB_DIRECT_PLAYER_SPAWN_V1";
  const string DirectPlayerDespawnMessageName = "GB_DIRECT_PLAYER_DESPAWN_V1";
  NetworkManager directNetworkManager;
  UnityTransport directTransport;
  DirectHostRpcRouter directRpcRouter;
  bool registeredDirectInitHandler;
  bool registeredDirectPlayerHandlers;
  bool directWorldReady;
  readonly HashSet<ulong> directInitResponseClients = new HashSet<ulong>();
  string directHostShareAddress;

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

  public string GetJoinedRoomName()
  {
#if USE_PUN
    return PhotonNetwork.room.name;
#else
    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    if (!IsDirectHost())
    {
      return $"{options.joinAddress}:{(options.joinPort == 0 ? DefaultDirectHostPort : options.joinPort)}";
    }

    if (!directHostShareAddress.IsNullOrEmpty())
    {
      return directHostShareAddress;
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
          directHostShareAddress = $"{localAddress}:{DefaultDirectHostPort}";
          return directHostShareAddress;
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
          directHostShareAddress = $"{address}:{DefaultDirectHostPort}";
          return directHostShareAddress;
        }
        fallback = address.ToString();
      }
    }

    directHostShareAddress = $"{fallback}:{DefaultDirectHostPort}";
    return directHostShareAddress;
#endif
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
    if (SteamManager.Initialized)
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
    if (SteamManager.Initialized)
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
    if (!options.playOptions.isMultiplayer)
    {
      mode = Mode.Offline;
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

    if (options.joinAddress.IsNullOrEmpty())
    {
      ushort hostPort = options.joinPort == 0 ? DefaultDirectHostPort : options.joinPort;
      directTransport.SetConnectionData("127.0.0.1", hostPort, "0.0.0.0");
      PhotonNetwork.isMasterClient = true;
      PhotonNetwork.offlineMode = false;
      StartCoroutine(NonPunStartRoutine(true));
    }
    else
    {
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

  IEnumerator NonPunStartRoutine(bool startHost)
  {
    yield return null;
    yield return null;
    yield return null;
    MasterClientInit();
    if (startHost)
    {
      if (!directNetworkManager.IsListening && !directNetworkManager.StartHost())
      {
        OnFatalError($"Could not start a direct-host server on UDP port {DefaultDirectHostPort}.");
        yield break;
      }
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
    PhotonView.InitializeSceneViews();
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
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReadyMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerSpawnMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerDespawnMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerIdentityMessageName);
      directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerInitRequestMessageName);
    }
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerReadyMessageName, OnDirectPlayerReady);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerSpawnMessageName, OnDirectPlayerSpawn);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerDespawnMessageName, OnDirectPlayerDespawn);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerIdentityMessageName, OnDirectPlayerIdentityMessage);
    directNetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(DirectPlayerInitRequestMessageName, OnDirectPlayerInitRequest);
    registeredDirectPlayerHandlers = true;
  }

  void OnDirectClientConnected(ulong clientId)
  {
    if (directNetworkManager == null) return;
    if (!directNetworkManager.IsServer)
    {
      if (clientId == directNetworkManager.LocalClientId)
      {
        PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
        SendDirectPlayerIdentity(NetworkManager.ServerClientId, PhotonNetwork.player.ID, PhotonNetwork.player.NickName);
      }
      return;
    }
    if (clientId == directNetworkManager.LocalClientId) return;
    if (clientId >= (ulong)PhotonNetwork.MaximumDirectPlayers)
    {
      Util.LogError($"Direct hosting supports at most {PhotonNetwork.MaximumDirectPlayers} player IDs.");
      directNetworkManager.DisconnectClient(clientId);
      return;
    }

    try
    {
      PhotonNetwork.SetDirectSession(directNetworkManager, GetLocalDirectPlayerName());
      int joiningPlayerId = checked((int)clientId + 1);
      foreach (PhotonPlayer connectedPlayer in PhotonNetwork.playerList)
      {
        if (connectedPlayer.ID != joiningPlayerId)
          SendDirectPlayerIdentity(clientId, connectedPlayer.ID, connectedPlayer.NickName);
      }

      SendDirectHostInit(clientId);
      directInitResponseClients.Add(clientId);
    }
    catch (Exception e)
    {
      Util.LogError($"Could not send the initial world state to direct client {clientId}: {e}");
      directNetworkManager.DisconnectClient(clientId);
    }
  }
  void SendDirectHostInit(ulong clientId)
  {
    byte[] initPacket = CreateDirectHostInitPacket();
    using (FastBufferWriter writer = new FastBufferWriter(initPacket.Length + sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(initPacket.Length);
      writer.WriteBytesSafe(initPacket);
      directNetworkManager.CustomMessagingManager.SendNamedMessage(
        DirectHostInitMessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
    }
    Util.Log($"Sent initial world state ({initPacket.Length / 1024} KB) to direct client {clientId}.");
  }

  void SendDirectPlayerIdentity(ulong clientId, int playerId, string nickname)
  {
    if (directNetworkManager == null || !directNetworkManager.IsListening) return;
    byte[] nicknameBytes = Encoding.UTF8.GetBytes(nickname ?? string.Empty);
    if (nicknameBytes.Length == 0 || nicknameBytes.Length > 128)
      throw new InvalidDataException("Direct player nicknames must contain 1–128 UTF-8 bytes.");
    using (FastBufferWriter writer = new FastBufferWriter(nicknameBytes.Length + 2 * sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(playerId);
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
      reader.ReadValueSafe(out int nicknameLength);
      if (nicknameLength <= 0 || nicknameLength > 128) throw new InvalidDataException("Invalid direct player nickname length.");
      byte[] nicknameBytes = new byte[nicknameLength];
      reader.ReadBytesSafe(ref nicknameBytes, nicknameLength);
      string nickname = Encoding.UTF8.GetString(nicknameBytes).Trim();
      if (nickname.Length == 0) throw new InvalidDataException("Direct player nickname cannot be empty.");

      if (directNetworkManager.IsServer)
      {
        if (senderClientId == directNetworkManager.LocalClientId
          || playerId != checked((int)senderClientId + 1)
          || playerId > PhotonNetwork.MaximumDirectPlayers) return;
        PhotonNetwork.SetDirectPeerName(playerId, nickname);
        if (kickedPlayerNickNames.Contains(nickname))
        {
          KickPlayer(PhotonNetwork.FindPlayer(playerId));
          return;
        }
        foreach (ulong clientId in directNetworkManager.ConnectedClientsIds)
          if (clientId != directNetworkManager.LocalClientId && clientId != senderClientId)
            SendDirectPlayerIdentity(clientId, playerId, nickname);
      }
      else if (senderClientId == NetworkManager.ServerClientId
        && playerId > 0 && playerId <= PhotonNetwork.MaximumDirectPlayers)
      {
        PhotonNetwork.SetDirectPeerName(playerId, nickname);
        PhotonNetwork.RegisterDirectPeer(playerId);
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
      if (playerId != checked((int)senderClientId + 1)
        || playerId > PhotonNetwork.MaximumDirectPlayers
        || !directWorldReady
        || !directInitResponseClients.Add(senderClientId)) return;

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

  IEnumerator RequestDirectHostInitRoutine()
  {
    SendDirectPlayerIdentity(NetworkManager.ServerClientId, PhotonNetwork.player.ID, PhotonNetwork.player.NickName);
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
      if (playerId != checked((int)senderClientId + 1) || !IsDirectAvatarViewIdValid(playerId, viewId)
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
      PhotonNetwork.RegisterDirectPeer(playerId);
      SpawnDirectRemotePlayer(playerId, viewId, position, rotation);
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
    view.viewID = viewId;
    view.ApplyOwnership(playerId);
    body.Setup(false);
    body.SetDirectPose(position, rotation);
    directRemoteBodies[playerId] = body;
  }

  void OnDirectPlayerDespawn(ulong senderClientId, FastBufferReader reader)
  {
    if (directNetworkManager == null || directNetworkManager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
    reader.ReadValueSafe(out int playerId);
    if (playerId == PhotonNetwork.player.ID) return;
    RemoveDirectRemotePlayer(playerId);
    PhotonNetwork.RemoveDirectPeer(playerId);
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
      if (actor.reliablePhotonView != null && actor.reliablePhotonView.ownerId == playerId)
        actor.reliablePhotonView.TransferOwnership(PhotonNetwork.player.ID);
  }


  void OnDirectClientDisconnected(ulong clientId)
  {
    if (directNetworkManager == null) return;
    if (directNetworkManager.IsServer && clientId != directNetworkManager.LocalClientId)
    {
      int playerId = checked((int)clientId + 1);
      directInitResponseClients.Remove(clientId);
      PhotonPlayer disconnectedPlayer = PhotonNetwork.FindPlayer(playerId);
      RemoveDirectRemotePlayer(playerId);
      BroadcastDirectPlayerDespawn(playerId);
      ReclaimDirectPlayerActors(playerId);
      PlayerLeftMessage message = new PlayerLeftMessage { id = playerId, nickName = disconnectedPlayer.NickName };
      voosEngine.BroadcastMessage("PlayerLeft", message);
      Util.Log($"Direct player {playerId} disconnected.");
      PhotonNetwork.ForgetDirectPeerName(playerId);
      return;
    }

    if (!directNetworkManager.IsServer
      && !wasDirectlyKicked
      && (clientId == directNetworkManager.LocalClientId || clientId == NetworkManager.ServerClientId)
      && GameBuilderApplication.CurrentGameOptions.playOptions.isMultiplayer)
    {
      OnFatalError(receivedPlayerInitPayload
        ? "The host disconnected."
        : "The host disconnected before sending the game state.");
    }
  }

  byte[] CreateDirectHostInitPacket()
  {
    NewPlayerInitPayload payload;
    payload.version = NewPlayerInitPayload.CurrentVersion;
    payload.behaviorDatabase = behaviorSystem.SaveDatabase();
    payload.sojoDatabase = sojoSystem.SaveDatabase();
    payload.terrainMeta = terrain.GetMetadata();
    payload.stage = stage.Save();

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
      if (packetLength <= 0 || packetLength > MaximumPlayerInitKbs * 1024 + 3 * sizeof(int))
      {
        throw new InvalidDataException($"Invalid direct-host initial-state size: {packetLength} bytes.");
      }

      byte[] packet = new byte[packetLength];
      reader.ReadBytesSafe(ref packet, packetLength);
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
      }

      StartCoroutine(DirectClientInitRoutine());
    }
    catch (Exception e)
    {
      OnFatalError($"Could not load the game state from the host.\n{e.Message}");
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
    bool preserveDirectSession = state == State.SwitchingScene
      && directNetworkManager != null
      && directNetworkManager.IsListening;
    if (directNetworkManager != null)
    {
      directNetworkManager.OnClientConnectedCallback -= OnDirectClientConnected;
      directNetworkManager.OnClientDisconnectCallback -= OnDirectClientDisconnected;
      if (registeredDirectInitHandler && directNetworkManager.CustomMessagingManager != null)
      {
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectHostInitMessageName);
        registeredDirectInitHandler = false;
      }
      if (registeredDirectPlayerHandlers && directNetworkManager.CustomMessagingManager != null)
      {
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerReadyMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerSpawnMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerDespawnMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerIdentityMessageName);
        directNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(DirectPlayerInitRequestMessageName);
        registeredDirectPlayerHandlers = false;
      }
      if (directNetworkManager.IsListening && !preserveDirectSession)
      {
        directNetworkManager.Shutdown();
      }
    }
    if (!preserveDirectSession) PhotonNetwork.ClearDirectSession();
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
        userView.viewID = PhotonNetwork.AllocateViewID();
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
    if (info.sender == null || PhotonNetwork.masterClient == null || info.sender.ID != PhotonNetwork.masterClient.ID) return;
    wasDirectlyKicked = true;
    PhotonNetwork.Disconnect();
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
      joinAddress = currentOptions.joinAddress,
      joinPort = currentOptions.joinPort,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
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

    // TEMP TEMP
    if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.F2))
    {
      PhotonNetwork.room.IsVisible = !PhotonNetwork.room.IsVisible;
      Util.Log($"toggled room visible to {PhotonNetwork.room.IsVisible}");
    }
  }

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
    public static int CurrentVersion = 12;
    public int version;
    public Behaviors.Database.Jsonable behaviorDatabase;
    public GameBuilderStage.Persisted stage;
    public SojoDatabase.Saved sojoDatabase;
    public TerrainManager.Metadata terrainMeta;
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
    NewPlayerInitPayload payload;
    payload.version = NewPlayerInitPayload.CurrentVersion;
    payload.behaviorDatabase = behaviorSystem.SaveDatabase();
    payload.sojoDatabase = sojoSystem.SaveDatabase();
    payload.terrainMeta = terrain.GetMetadata();
    payload.stage = stage.Save();

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
    NewPlayerInitPayload payload;
    payload.version = NewPlayerInitPayload.CurrentVersion;
    payload.behaviorDatabase = behaviorSystem.SaveDatabase();
    payload.sojoDatabase = sojoSystem.SaveDatabase();
    payload.terrainMeta = terrain.GetMetadata();
    payload.stage = stage.Save();

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
