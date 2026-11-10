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

#if !USE_PUN

using System;
using System.IO;
using System.Linq;
using Unity.Netcode;
using System.Collections.Generic;
namespace Photon
{
  public class PunBehaviour : UnityEngine.MonoBehaviour
  {
    public virtual void OnPhotonPlayerDisconnected(PhotonPlayer otherPlayer) { }
    public virtual void OnPhotonPlayerConnected(PhotonPlayer newPlayer) { }
    public virtual void OnJoinedRoom() { }

    PhotonView cachedPhotonView;
    public PhotonView photonView
    {
      get
      {
        if (cachedPhotonView == null) cachedPhotonView = GetComponent<PhotonView>();
        return cachedPhotonView;
      }
      set { cachedPhotonView = value; }
    }

    void Awake()
    {
      photonView = GetComponent<PhotonView>();
    }
  }
  public class MonoBehaviour : UnityEngine.MonoBehaviour { }
}

[System.AttributeUsage(System.AttributeTargets.Method)]
public class PunRPC : System.Attribute { }

public interface IPunObservable
{
  void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info);
}

public class PhotonPlayer
{
  public string NickName = "playerDummy";
  public int ID = 0;
  internal ulong DirectClientId;
  public bool IsMasterClient { get { return ID == 1; } }

  public static PhotonPlayer Find(object playerId)
  {
    if (playerId is int id) return PhotonNetwork.FindPlayer(id);
    if (playerId is string text && int.TryParse(text, out id)) return PhotonNetwork.FindPlayer(id);
    return null;
  }

  public int GetRoomIndex() { return Math.Max(0, ID - 1); }
}

namespace ExitGames
{
  namespace UtilityScripts
  {
  }
}

public enum ReceiverGroup { Others }
public enum EventCaching { DoNotCache }

public struct RaiseEventOptions
{
  public ReceiverGroup Receivers;
  public EventCaching CachingOption;
}

public enum DisconnectCause { }

public class PhotonRoom
{
  public string name = "dummy";
  public string Name = "dummy";
  public bool IsVisible = false;
}

public static class PhotonNetwork
{
  public static PhotonRoom room = new PhotonRoom();
  public static bool connected = true;
  public static bool isMasterClient = true;
  public static bool offlineMode = true;
  public static bool inRoom = true;
  public static PhotonPlayer player = new PhotonPlayer { ID = 1, NickName = Environment.UserName };
  public static PhotonPlayer[] playerList = new PhotonPlayer[] { player };
  public static PhotonPlayer[] otherPlayers = new PhotonPlayer[0];
  internal static bool autoCleanUpPlayerObjects = false;
  internal static bool autoJoinLobby;
  internal static Action<byte, object, int> OnEventCall { get; set; }
  static int allocatedViewCount;
  static readonly Stack<int> reusableViewIds = new Stack<int>();
  static readonly HashSet<int> allocatedViewIds = new HashSet<int>();
  static int directLocalId = 1;
  internal const int DirectViewIdBlockSize = 2048;
  internal const int MaximumDirectPlayers = short.MaxValue / DirectViewIdBlockSize + 1;
  // Must outlive the directory's 240-second migration watch plus scene reload.
  const float DirectMigrationReservationSeconds = 300f;
  static readonly Dictionary<int, PhotonPlayer> directPeers = new Dictionary<int, PhotonPlayer>();
  static readonly Dictionary<int, string> directPeerNames = new Dictionary<int, string>();
  static readonly Dictionary<ulong, int> directPlayerIdsByClient = new Dictionary<ulong, int>();
  static readonly Dictionary<int, ulong> directClientIdsByPlayer = new Dictionary<int, ulong>();
  static readonly HashSet<int> directPlayersWithIdentity = new HashSet<int>();
  static readonly HashSet<int> directMigrationReservedPlayerIds = new HashSet<int>();
  static float directMigrationReservationExpiry;

  public static PhotonPlayer masterClient { get { return FindPlayer(1); } }

  public static void Disconnect()
  {
    if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
      NetworkManager.Singleton.Shutdown();
    ClearDirectSession();
  }
  internal static void MarkDirectSessionDisconnected()
  {
    connected = false;
    inRoom = false;
    isMasterClient = false;
    offlineMode = false;
  }

  internal static void DisconnectDirectSessionPreservingPlayers()
  {
    MarkDirectSessionDisconnected();
    NetworkManager manager = NetworkManager.Singleton;
    if (manager != null && manager.IsListening && !manager.IsServer)
      manager.Shutdown();
  }

  public static void RaiseEvent(object eventCode, object content, object reliable, object options)
  {
    if (eventCode is byte code) DirectHostRpcRouter.RaiseEvent(code, content);
  }

  internal static void UnAllocateViewID(int viewId)
  {
    int firstViewId = Math.Max(0, player.ID - 1) * DirectViewIdBlockSize;
    int maximumViewId = Math.Min(short.MaxValue, firstViewId + DirectViewIdBlockSize);
    if (viewId <= firstViewId || viewId > maximumViewId || !allocatedViewIds.Remove(viewId)) return;
    reusableViewIds.Push(viewId);
  }

  // Loaded actors in this player's view-ID block remain reserved after a reconnect.
  internal static void RegisterViewID(int viewId)
  {
    if (viewId <= 0) return;
    int firstViewId = Math.Max(0, player.ID - 1) * DirectViewIdBlockSize;
    int maximumViewId = Math.Min(short.MaxValue, firstViewId + DirectViewIdBlockSize);
    if (viewId > firstViewId && viewId <= maximumViewId) allocatedViewIds.Add(viewId);
  }

  internal static int AllocateViewID()
  {
    // A recycled player slot can still own persistent objects in the world.
    while (reusableViewIds.Count > 0)
    {
      int reusableViewId = reusableViewIds.Pop();
      if (PhotonView.IsViewIdInUse(reusableViewId)) continue;
      allocatedViewIds.Add(reusableViewId);
      return reusableViewId;
    }

    int ownerBlock = Math.Max(0, player.ID - 1);
    int firstViewId = ownerBlock * DirectViewIdBlockSize;
    int maximumCount = Math.Min(DirectViewIdBlockSize, short.MaxValue - firstViewId);
    while (allocatedViewCount < maximumCount)
    {
      int viewId = firstViewId + ++allocatedViewCount;
      if (allocatedViewIds.Contains(viewId) || PhotonView.IsViewIdInUse(viewId)) continue;
      allocatedViewIds.Add(viewId);
      return viewId;
    }
    throw new InvalidOperationException("This direct-host session has exceeded this player's actor view ID capacity.");
  }

  internal static void ReleaseIdOfView(PhotonView reliablePhotonView) { }

  internal static void LeaveRoom() { }
  internal static void DestroyAll() { }

  internal static void SetDirectSession(NetworkManager manager, string localNickName)
  {
    if (manager == null || !manager.IsListening) return;
    ulong localClientId = manager.LocalClientId;
    int localId;
    if (manager.IsServer)
    {
      localId = 1;
      if (!MapDirectPlayerId(localId, localClientId))
        throw new InvalidOperationException("The host player ID is already assigned to another client.");
    }
    else if (!directPlayerIdsByClient.TryGetValue(localClientId, out localId))
    {
      return;
    }

    if (directLocalId != localId)
    {
      allocatedViewCount = 0;
      reusableViewIds.Clear();
      allocatedViewIds.Clear();
      directLocalId = localId;
    }
    string nickname = string.IsNullOrWhiteSpace(localNickName) ? "Player" + localId : localNickName;
    player = new PhotonPlayer
    {
      ID = localId,
      DirectClientId = localClientId,
      NickName = nickname
    };
    directPeerNames[localId] = nickname;
    directPlayersWithIdentity.Add(localId);
    isMasterClient = manager.IsServer;
    connected = true;
    inRoom = true;
    offlineMode = false;

    List<PhotonPlayer> players = new List<PhotonPlayer>();
    if (manager.IsServer)
    {
      foreach (ulong clientId in manager.ConnectedClientsIds)
      {
        if (!directPlayerIdsByClient.TryGetValue(clientId, out int id)) continue;
        players.Add(id == localId ? player : new PhotonPlayer
        {
          ID = id,
          DirectClientId = clientId,
          NickName = GetDirectPeerName(id, "Player" + id)
        });
      }
    }
    else
    {
      MapDirectPlayerId(1, NetworkManager.ServerClientId);
      if (directPeers.TryGetValue(1, out PhotonPlayer hostPlayer))
        players.Add(hostPlayer);
      else
        players.Add(new PhotonPlayer
        {
          ID = 1,
          DirectClientId = NetworkManager.ServerClientId,
          NickName = GetDirectPeerName(1, "Host")
        });
      foreach (PhotonPlayer peer in directPeers.Values)
        if (peer.ID != 1 && peer.ID != localId) players.Add(peer);
      if (localId != 1) players.Add(player);
    }
    if (!players.Exists(candidate => candidate.ID == localId)) players.Add(player);
    playerList = players.ToArray();
    List<PhotonPlayer> others = new List<PhotonPlayer>();
    foreach (PhotonPlayer candidate in playerList) if (candidate.ID != localId) others.Add(candidate);
    otherPlayers = others.ToArray();
  }

  static bool MapDirectPlayerId(int playerId, ulong clientId)
  {
    if (playerId <= 0 || playerId > MaximumDirectPlayers) return false;
    if (directPlayerIdsByClient.TryGetValue(clientId, out int mappedPlayerId) && mappedPlayerId != playerId) return false;
    if (directClientIdsByPlayer.TryGetValue(playerId, out ulong mappedClientId) && mappedClientId != clientId) return false;
    directPlayerIdsByClient[clientId] = playerId;
    directClientIdsByPlayer[playerId] = clientId;
    return true;
  }

  internal static bool TryAssignDirectPlayerId(ulong clientId, out int playerId)
  {
    return TryAssignDirectPlayerId(clientId, 0, out playerId);
  }

  internal static bool TryAssignDirectPlayerId(ulong clientId, int preferredPlayerId, out int playerId)
  {
    ExpireDirectMigrationReservations(UnityEngine.Time.realtimeSinceStartup);
    if (directPlayerIdsByClient.TryGetValue(clientId, out playerId))
      return playerId > 1;
    if (preferredPlayerId > 1 && preferredPlayerId <= MaximumDirectPlayers
      && MapDirectPlayerId(preferredPlayerId, clientId))
    {
      directMigrationReservedPlayerIds.Remove(preferredPlayerId);
      directPeerNames[preferredPlayerId] = GetDirectPeerName(preferredPlayerId, "Player" + preferredPlayerId);
      playerId = preferredPlayerId;
      return true;
    }
    for (int candidate = 2; candidate <= MaximumDirectPlayers; candidate++)
    {
      if (directClientIdsByPlayer.ContainsKey(candidate) || directMigrationReservedPlayerIds.Contains(candidate)) continue;
      if (!MapDirectPlayerId(candidate, clientId)) continue;
      directPeerNames[candidate] = GetDirectPeerName(candidate, "Player" + candidate);
      playerId = candidate;
      return true;
    }
    playerId = 0;
    return false;
  }

  internal static void SetDirectMigrationReservedPlayerIds(string playerIds)
  {
    directMigrationReservedPlayerIds.Clear();
    if (string.IsNullOrEmpty(playerIds)) return;
    string[] values = playerIds.Split(',');
    foreach (string value in values)
      if (int.TryParse(value, out int playerId) && playerId > 1 && playerId <= MaximumDirectPlayers)
        directMigrationReservedPlayerIds.Add(playerId);
    directMigrationReservationExpiry = UnityEngine.Time.realtimeSinceStartup
      + DirectMigrationReservationSeconds;
  }

  internal static void ExpireDirectMigrationReservations(float now)
  {
    if (directMigrationReservedPlayerIds.Count == 0 || now < directMigrationReservationExpiry) return;
    directMigrationReservedPlayerIds.Clear();
    directMigrationReservationExpiry = 0f;
  }

  internal static int GetLowestDirectPlayerId()
  {
    int lowest = 0;
    foreach (PhotonPlayer candidate in playerList)
    {
      if (candidate == null || candidate.ID <= 1) continue;
      if (lowest == 0 || candidate.ID < lowest) lowest = candidate.ID;
    }
    return lowest;
  }

  internal static bool AssignDirectLocalPlayerId(NetworkManager manager, int playerId)
  {
    if (manager == null || !manager.IsListening || manager.IsServer
      || playerId <= 1 || playerId > MaximumDirectPlayers) return false;
    if (directPlayerIdsByClient.TryGetValue(manager.LocalClientId, out int previousPlayerId)
      && previousPlayerId != playerId)
    {
      directPlayerIdsByClient.Remove(manager.LocalClientId);
      directClientIdsByPlayer.Remove(previousPlayerId);
      directPeerNames.Remove(previousPlayerId);
      directPlayersWithIdentity.Remove(previousPlayerId);
    }
    if (!MapDirectPlayerId(playerId, manager.LocalClientId)) return false;
    if (directLocalId != playerId)
    {
      allocatedViewCount = 0;
      reusableViewIds.Clear();
      allocatedViewIds.Clear();
      directLocalId = playerId;
    }
    return true;
  }

  internal static int GetDirectPlayerId(ulong clientId)
  {
    return directPlayerIdsByClient.TryGetValue(clientId, out int playerId) ? playerId : 0;
  }

  internal static int ReleaseDirectPlayerId(ulong clientId)
  {
    if (!directPlayerIdsByClient.TryGetValue(clientId, out int playerId) || playerId <= 1) return 0;
    directPlayerIdsByClient.Remove(clientId);
    directClientIdsByPlayer.Remove(playerId);
    directPlayersWithIdentity.Remove(playerId);
    return playerId;
  }

  internal static void ClearDirectSession()
  {
    connected = false;
    inRoom = false;
    isMasterClient = true;
    offlineMode = true;
    player = new PhotonPlayer { ID = 1, NickName = Environment.UserName };
    playerList = new PhotonPlayer[] { player };
    otherPlayers = new PhotonPlayer[0];
    allocatedViewCount = 0;
    reusableViewIds.Clear();
    allocatedViewIds.Clear();
    directLocalId = 1;
    directPeers.Clear();
    directPeerNames.Clear();
    directPlayerIdsByClient.Clear();
    directClientIdsByPlayer.Clear();
    directPlayersWithIdentity.Clear();
    directMigrationReservedPlayerIds.Clear();
    directMigrationReservationExpiry = 0f;
  }
  static string GetDirectPeerName(int playerId, string fallback)
  {
    return directPeerNames.TryGetValue(playerId, out string nickname) && !string.IsNullOrWhiteSpace(nickname)
      ? nickname
      : fallback;
  }

  internal static void SetDirectPeerName(int playerId, string nickname)
  {
    if (playerId <= 0 || playerId > MaximumDirectPlayers || string.IsNullOrWhiteSpace(nickname)) return;
    nickname = nickname.Trim();
    if (nickname.Length > 32) nickname = nickname.Substring(0, 32);
    directPeerNames[playerId] = nickname;
    directPlayersWithIdentity.Add(playerId);
    if (player.ID == playerId) player.NickName = nickname;
    if (directPeers.TryGetValue(playerId, out PhotonPlayer directPeer)) directPeer.NickName = nickname;
    foreach (PhotonPlayer current in playerList)
      if (current.ID == playerId) current.NickName = nickname;
  }
  internal static bool HasDirectPlayerIdentity(int playerId)
  {
    return directPlayersWithIdentity.Contains(playerId);
  }
  internal static void ResetDirectPeers()
  {
    NetworkManager manager = NetworkManager.Singleton;
    int localId = directLocalId;
    string localName = null;
    bool preserveLocalAssignment = directLocalId > 1;
    if (manager != null && manager.IsListening && !manager.IsServer
      && directPlayerIdsByClient.TryGetValue(manager.LocalClientId, out int mappedLocalId))
    {
      localId = mappedLocalId;
      preserveLocalAssignment = localId > 1;
    }
    if (!preserveLocalAssignment && player != null && player.ID > 1)
    {
      localId = player.ID;
      preserveLocalAssignment = true;
    }
    if (preserveLocalAssignment)
    {
      directPeerNames.TryGetValue(localId, out localName);
      if (string.IsNullOrWhiteSpace(localName) && player != null && player.ID == localId)
        localName = player.NickName;
    }

    directPeers.Clear();
    directPeerNames.Clear();
    directPlayerIdsByClient.Clear();
    directClientIdsByPlayer.Clear();
    directPlayersWithIdentity.Clear();
    directLocalId = preserveLocalAssignment ? localId : 0;
    if (preserveLocalAssignment && manager != null && manager.IsListening && !manager.IsServer)
    {
      MapDirectPlayerId(localId, manager.LocalClientId);
      MapDirectPlayerId(1, NetworkManager.ServerClientId);
      directPeerNames[localId] = string.IsNullOrWhiteSpace(localName) ? "Player" + localId : localName;
      directPlayersWithIdentity.Add(localId);
      player.ID = localId;
    }
    connected = false;
    inRoom = false;
    isMasterClient = false;
    offlineMode = false;
  }

  internal static void ForgetDirectPeerName(int playerId)
  {
    if (playerId == player.ID) return;
    directPeerNames.Remove(playerId);
    directPlayersWithIdentity.Remove(playerId);
  }
  internal static bool RegisterDirectPeer(int playerId, ulong clientId)
  {
    if (playerId <= 0 || playerId > MaximumDirectPlayers || playerId == player.ID
      || directPeers.ContainsKey(playerId) || !MapDirectPlayerId(playerId, clientId)) return false;
    PhotonPlayer peer = new PhotonPlayer
    {
      ID = playerId,
      DirectClientId = clientId,
      NickName = GetDirectPeerName(playerId, playerId == 1 ? "Host" : "Player" + playerId)
    };
    directPeers.Add(playerId, peer);
    foreach (Photon.PunBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<Photon.PunBehaviour>(true))
      if (!(behaviour is NetworkingController)) behaviour.OnPhotonPlayerConnected(peer);
    foreach (GlobalUnreliableData sync in UnityEngine.Object.FindObjectsOfType<GlobalUnreliableData>(true))
      sync.OnPhotonPlayerConnected(peer);
    return true;
  }

  internal static bool RemoveDirectPeer(int playerId)
  {
    if (!directPeers.TryGetValue(playerId, out PhotonPlayer peer)) return false;
    directPeers.Remove(playerId);
    directPeerNames.Remove(playerId);
    directPlayersWithIdentity.Remove(playerId);
    directPlayerIdsByClient.Remove(peer.DirectClientId);
    directClientIdsByPlayer.Remove(playerId);
    foreach (Photon.PunBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<Photon.PunBehaviour>(true))
      if (!(behaviour is NetworkingController)) behaviour.OnPhotonPlayerDisconnected(peer);
    return true;
  }


  internal static PhotonPlayer FindPlayer(int playerId)
  {
    foreach (PhotonPlayer candidate in playerList) if (candidate.ID == playerId) return candidate;
    if (player.ID == playerId) return player;
    if (directPeers.TryGetValue(playerId, out PhotonPlayer directPeer)) return directPeer;
    ulong clientId = directClientIdsByPlayer.TryGetValue(playerId, out ulong mappedClientId)
      ? mappedClientId
      : NetworkManager.ServerClientId;
    return playerId > 0 ? new PhotonPlayer
    {
      ID = playerId,
      NickName = GetDirectPeerName(playerId, playerId == 1 ? "Host" : "Player" + playerId),
      DirectClientId = clientId
    } : null;
  }

  internal static PhotonPlayer GetDirectPlayer(ulong clientId)
  {
    int playerId = GetDirectPlayerId(clientId);
    return playerId > 0 ? FindPlayer(playerId) : null;
  }

  internal static ulong? GetDirectClientId(int playerId)
  {
    NetworkManager manager = NetworkManager.Singleton;
    if (playerId <= 0 || manager == null || !manager.IsListening) return null;
    return directClientIdsByPlayer.TryGetValue(playerId, out ulong clientId) ? clientId : (ulong?)null;
  }

  internal static void DeliverDirectEvent(byte eventCode, object content, int senderId)
  {
    OnEventCall?.Invoke(eventCode, content, senderId);
  }
}

public class PhotonStream
{
  readonly List<object> values = new List<object>();
  int nextReadIndex;
  public bool isWriting { get; private set; }
  public bool isReading { get { return !isWriting; } }

  public PhotonStream(bool writing) { isWriting = writing; }

  public void SendNext(object value)
  {
    if (!isWriting) throw new InvalidOperationException("Cannot write to a PhotonStream opened for reading.");
    values.Add(value);
  }

  public object ReceiveNext()
  {
    if (isWriting) throw new InvalidOperationException("Cannot read from a PhotonStream opened for writing.");
    if (nextReadIndex >= values.Count) throw new EndOfStreamException("The serialized player state ended unexpectedly.");
    return values[nextReadIndex++];
  }

  internal byte[] ToBytes()
  {
    return DirectHostRpcRouter.SerializeValues(values.ToArray());
  }

  internal static PhotonStream FromBytes(byte[] bytes)
  {
    PhotonStream stream = new PhotonStream(false);
    stream.values.AddRange(DirectHostRpcRouter.DeserializeValues(bytes));
    return stream;
  }
}

public class PhotonMessageInfo
{
  public PhotonPlayer sender = PhotonNetwork.masterClient;
}

public class RoomInfo { }

public enum CloudRegionCode { }
public enum PhotonTargets { Others, AllViaServer, All, MasterClient }

#endif