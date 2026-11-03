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
  static readonly Dictionary<int, PhotonPlayer> directPeers = new Dictionary<int, PhotonPlayer>();
  static readonly Dictionary<int, string> directPeerNames = new Dictionary<int, string>();

  public static PhotonPlayer masterClient { get { return FindPlayer(1); } }

  public static void Disconnect()
  {
    if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
      NetworkManager.Singleton.Shutdown();
    ClearDirectSession();
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

  internal static int AllocateViewID()
  {
    if (reusableViewIds.Count > 0)
    {
      int reusableViewId = reusableViewIds.Pop();
      allocatedViewIds.Add(reusableViewId);
      return reusableViewId;
    }

    int ownerBlock = Math.Max(0, player.ID - 1);
    int firstViewId = ownerBlock * DirectViewIdBlockSize;
    int maximumCount = Math.Min(DirectViewIdBlockSize, short.MaxValue - firstViewId);
    if (allocatedViewCount >= maximumCount)
      throw new InvalidOperationException("This direct-host session has exceeded this player's actor view ID capacity.");

    int viewId = firstViewId + ++allocatedViewCount;
    allocatedViewIds.Add(viewId);
    return viewId;
  }

  internal static void ReleaseIdOfView(PhotonView reliablePhotonView) { }

  internal static void LeaveRoom() { }
  internal static void DestroyAll() { }

  internal static void SetDirectSession(NetworkManager manager, string localNickName)
  {
    if (manager == null || !manager.IsListening) return;
    int localId = checked((int)manager.LocalClientId + 1);
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
      DirectClientId = manager.LocalClientId,
      NickName = nickname
    };
    directPeerNames[localId] = nickname;
    isMasterClient = manager.IsServer;
    connected = true;
    inRoom = true;
    offlineMode = false;

    List<PhotonPlayer> players = new List<PhotonPlayer>();
    if (manager.IsServer)
    {
      foreach (ulong clientId in manager.ConnectedClientsIds)
      {
        int id = checked((int)clientId + 1);
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
    if (player.ID == playerId) player.NickName = nickname;
    if (directPeers.TryGetValue(playerId, out PhotonPlayer directPeer)) directPeer.NickName = nickname;
    foreach (PhotonPlayer current in playerList)
      if (current.ID == playerId) current.NickName = nickname;
  }
  internal static void ResetDirectPeers()
  {
    directPeers.Clear();
  }

  internal static void ForgetDirectPeerName(int playerId)
  {
    if (playerId != player.ID) directPeerNames.Remove(playerId);
  }
  internal static bool RegisterDirectPeer(int playerId)
  {
    if (playerId <= 0 || playerId == player.ID || directPeers.ContainsKey(playerId)) return false;
    PhotonPlayer peer = new PhotonPlayer
    {
      ID = playerId,
      DirectClientId = (ulong)(playerId - 1),
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
    foreach (Photon.PunBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<Photon.PunBehaviour>(true))
      if (!(behaviour is NetworkingController)) behaviour.OnPhotonPlayerDisconnected(peer);
    return true;
  }

  internal static PhotonPlayer FindPlayer(int playerId)
  {
    foreach (PhotonPlayer candidate in playerList) if (candidate.ID == playerId) return candidate;
    if (player.ID == playerId) return player;
    if (directPeers.TryGetValue(playerId, out PhotonPlayer directPeer)) return directPeer;
    return playerId > 0 ? new PhotonPlayer { ID = playerId, NickName = GetDirectPeerName(playerId, playerId == 1 ? "Host" : "Player" + playerId), DirectClientId = (ulong)(playerId - 1) } : null;
  }

  internal static PhotonPlayer GetDirectPlayer(ulong clientId)
  {
    return FindPlayer(checked((int)clientId + 1))
      ?? new PhotonPlayer { ID = checked((int)clientId + 1), DirectClientId = clientId, NickName = GetDirectPeerName(checked((int)clientId + 1), "Player" + (clientId + 1)) };
  }

  internal static ulong? GetDirectClientId(int playerId)
  {
    if (playerId <= 0) return null;
    ulong clientId = (ulong)(playerId - 1);
    NetworkManager manager = NetworkManager.Singleton;
    if (manager == null || !manager.IsListening) return null;
    if (clientId == manager.LocalClientId) return clientId;
    if (manager.IsServer && manager.ConnectedClientsIds.Contains(clientId)) return clientId;
    return clientId == NetworkManager.ServerClientId ? clientId : (ulong?)null;
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