#if !USE_PUN
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public sealed class DirectHostRpcRouter : MonoBehaviour
{
  const string RpcName = "GB_DIRECT_RPC_V1";
  const string EventName = "GB_DIRECT_EVENT_V1";
  const string ObservableName = "GB_DIRECT_OBSERVABLE_V1";
  const string OwnershipName = "GB_DIRECT_OWNERSHIP_V1";
  const string PoseName = "GB_DIRECT_PLAYER_POSE_V1";
  const int MaxPacketBytes = 1024 * 1024;
  const int MaxUnreliableEventBytes = 900;
  const float ObservableInterval = 0.2f;
  const float ObservableRefresh = 2f;

  sealed class ObservableState
  {
    public byte[] bytes;
    public float sentAt;
  }

  static DirectHostRpcRouter instance;
  static readonly Dictionary<long, ObservableState> sentStates = new Dictionary<long, ObservableState>();
  NetworkManager manager;
  bool registered;
  float nextObservableTime;
  readonly HashSet<int> knownPlayerIds = new HashSet<int>();
  readonly HashSet<int> currentPlayerIds = new HashSet<int>();

  public void Initialize(NetworkManager networkManager)
  {
    manager = networkManager;
    instance = this;
    RegisterHandlers();
  }
  public void ResetScenePlayerCallbacks()
  {
    knownPlayerIds.Clear();
    currentPlayerIds.Clear();
  }

  void Update()
  {
    if (manager == null) manager = NetworkManager.Singleton;
    if (manager == null || !manager.IsListening || (!manager.IsServer && (!manager.IsConnectedClient || !PhotonNetwork.connected))) return;
    if (!registered) RegisterHandlers();
    PhotonNetwork.SetDirectSession(manager, NetworkingController.GetLocalDirectPlayerName());
    UpdateServerPlayerCallbacks();
    if (Time.realtimeSinceStartup >= nextObservableTime)
    {
      nextObservableTime = Time.realtimeSinceStartup + ObservableInterval;
      SendOwnedObservableStates();
    }
  }

  void RegisterHandlers()
  {
    if (registered || manager == null || !manager.IsListening) return;
    CustomMessagingManager messages = manager.CustomMessagingManager;
    messages.RegisterNamedMessageHandler(RpcName, OnRpcMessage);
    messages.RegisterNamedMessageHandler(EventName, OnEventMessage);
    messages.RegisterNamedMessageHandler(ObservableName, OnObservableMessage);
    messages.RegisterNamedMessageHandler(OwnershipName, OnOwnershipMessage);
    messages.RegisterNamedMessageHandler(PoseName, OnPoseMessage);
    registered = true;
  }

  void UpdateServerPlayerCallbacks()
  {
    if (!manager.IsServer) return;
    currentPlayerIds.Clear();
    foreach (PhotonPlayer directPlayer in PhotonNetwork.playerList)
    {
      if (directPlayer.ID != PhotonNetwork.player.ID && !PhotonNetwork.HasDirectPlayerIdentity(directPlayer.ID)) continue;
      currentPlayerIds.Add(directPlayer.ID);
      NotifyServerPlayerConnected(directPlayer);
    }
    knownPlayerIds.Remove(PhotonNetwork.player.ID);
    foreach (int playerId in new List<int>(knownPlayerIds))
    {
      if (currentPlayerIds.Contains(playerId)) continue;
      NotifyServerPlayerDisconnected(PhotonNetwork.FindPlayer(playerId));
    }
    knownPlayerIds.UnionWith(currentPlayerIds);
  }

  public void NotifyServerPlayerConnected(PhotonPlayer directPlayer)
  {
    if (manager == null || !manager.IsServer || directPlayer == null
      || directPlayer.ID == PhotonNetwork.player.ID || !PhotonNetwork.HasDirectPlayerIdentity(directPlayer.ID)
      || !knownPlayerIds.Add(directPlayer.ID)) return;
    foreach (Photon.PunBehaviour behaviour in FindObjectsOfType<Photon.PunBehaviour>(true))
      if (!(behaviour is NetworkingController)) behaviour.OnPhotonPlayerConnected(directPlayer);
    foreach (GlobalUnreliableData sync in FindObjectsOfType<GlobalUnreliableData>(true))
      sync.OnPhotonPlayerConnected(directPlayer);
  }

  public void NotifyServerPlayerDisconnected(PhotonPlayer directPlayer)
  {
    if (manager == null || !manager.IsServer || directPlayer == null
      || directPlayer.ID == PhotonNetwork.player.ID || !knownPlayerIds.Remove(directPlayer.ID)) return;
    foreach (Photon.PunBehaviour behaviour in FindObjectsOfType<Photon.PunBehaviour>(true))
      if (!(behaviour is NetworkingController)) behaviour.OnPhotonPlayerDisconnected(directPlayer);
  }

  void OnDestroy()
  {
    if (instance != this) return;
    instance = null;
    sentStates.Clear();
    PhotonNetwork.ClearDirectSession();
  }

  static byte[] Encode(Action<BinaryWriter> write)
  {
    using (MemoryStream stream = new MemoryStream())
    using (BinaryWriter writer = new BinaryWriter(stream))
    {
      write(writer);
      writer.Flush();
      return stream.ToArray();
    }
  }

  static void SendPacket(string name, ulong clientId, byte[] packet, NetworkDelivery delivery)
  {
    DirectHostRpcRouter router = instance;
    if (router == null || router.manager == null || !router.manager.IsListening) return;
    using (FastBufferWriter writer = new FastBufferWriter(packet.Length + sizeof(int), Allocator.Temp))
    {
      writer.WriteValueSafe(packet.Length);
      writer.WriteBytesSafe(packet);
      router.manager.CustomMessagingManager.SendNamedMessage(name, clientId, writer, delivery);
    }
  }

  static byte[] ReadPacket(FastBufferReader reader)
  {
    reader.ReadValueSafe(out int length);
    if (length <= 0 || length > MaxPacketBytes) throw new InvalidDataException($"Invalid direct-network packet size: {length} bytes.");
    byte[] packet = new byte[length];
    reader.ReadBytesSafe(ref packet, length);
    return packet;
  }

  static void WriteValue(BinaryWriter writer, object value)
  {
    if (value == null) { writer.Write((byte)0); return; }
    Type type = value.GetType();
    if (type.IsEnum) { writer.Write((byte)1); writer.Write(type.AssemblyQualifiedName); writer.Write(Convert.ToInt64(value)); }
    else if (value is string text) { writer.Write((byte)2); writer.Write(text); }
    else if (value is int i) { writer.Write((byte)3); writer.Write(i); }
    else if (value is bool b) { writer.Write((byte)4); writer.Write(b); }
    else if (value is float f) { writer.Write((byte)5); writer.Write(f); }
    else if (value is byte by) { writer.Write((byte)6); writer.Write(by); }
    else if (value is short s) { writer.Write((byte)7); writer.Write(s); }
    else if (value is long l) { writer.Write((byte)8); writer.Write(l); }
    else if (value is uint ui) { writer.Write((byte)9); writer.Write(ui); }
    else if (value is ulong ul) { writer.Write((byte)10); writer.Write(ul); }
    else if (value is double d) { writer.Write((byte)11); writer.Write(d); }
    else if (value is Vector2 v2) { writer.Write((byte)12); writer.Write(v2.x); writer.Write(v2.y); }
    else if (value is Vector3 v3) { writer.Write((byte)13); writer.Write(v3.x); writer.Write(v3.y); writer.Write(v3.z); }
    else if (value is Quaternion q) { writer.Write((byte)14); writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w); }
    else if (value is Color c) { writer.Write((byte)15); writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
    else if (value is byte[] bytes) { writer.Write((byte)16); writer.Write(bytes.Length); writer.Write(bytes); }
    else if (value is string[] strings)
    {
      writer.Write((byte)17); writer.Write(strings.Length);
      foreach (string item in strings) writer.Write(item ?? string.Empty);
    }
    else if (value is Vector4 v4) { writer.Write((byte)18); writer.Write(v4.x); writer.Write(v4.y); writer.Write(v4.z); writer.Write(v4.w); }
    else if (value is Color32 c32) { writer.Write((byte)19); writer.Write(c32.r); writer.Write(c32.g); writer.Write(c32.b); writer.Write(c32.a); }
    else throw new InvalidDataException($"Direct multiplayer cannot serialize RPC value of type {type.FullName}.");
  }

  static object ReadValue(BinaryReader reader)
  {
    switch (reader.ReadByte())
    {
      case 0: return null;
      case 1:
      {
        string typeName = reader.ReadString();
        Type type = Type.GetType(typeName, false);
        if (type == null || !type.IsEnum) throw new InvalidDataException($"Unknown RPC enum type '{typeName}'.");
        return Enum.ToObject(type, reader.ReadInt64());
      }
      case 2: return reader.ReadString();
      case 3: return reader.ReadInt32();
      case 4: return reader.ReadBoolean();
      case 5: return reader.ReadSingle();
      case 6: return reader.ReadByte();
      case 7: return reader.ReadInt16();
      case 8: return reader.ReadInt64();
      case 9: return reader.ReadUInt32();
      case 10: return reader.ReadUInt64();
      case 11: return reader.ReadDouble();
      case 12: return new Vector2(reader.ReadSingle(), reader.ReadSingle());
      case 13: return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      case 14: return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      case 15: return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      case 16:
      {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxPacketBytes) throw new InvalidDataException("Invalid RPC byte-array length.");
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
      }
      case 17:
      {
        int count = reader.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid RPC string-array length.");
        string[] values = new string[count];
        for (int i = 0; i < count; i++) values[i] = reader.ReadString();
        return values;
      }
      case 18: return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      case 19: return new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
      default: throw new InvalidDataException("Unknown direct-network value type.");
    }
  }

  internal static byte[] SerializeValues(object[] values)
  {
    return Encode(writer =>
    {
      writer.Write(values == null ? 0 : values.Length);
      if (values != null) foreach (object value in values) WriteValue(writer, value);
    });
  }

  internal static object[] DeserializeValues(byte[] packet)
  {
    using (BinaryReader reader = new BinaryReader(new MemoryStream(packet, false)))
    {
      int count = reader.ReadInt32();
      if (count < 0 || count > 256) throw new InvalidDataException("Invalid serialized value count.");
      object[] values = new object[count];
      for (int i = 0; i < count; i++) values[i] = ReadValue(reader);
      if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected data in serialized values.");
      return values;
    }
  }

  public static void SendRpc(PhotonView view, string methodName, PhotonTargets target, object[] args)
  {
    SendRpc(view, methodName, target, 0, args);
  }

  public static void SendRpc(PhotonView view, string methodName, PhotonPlayer target, object[] args)
  {
    if (target != null) SendRpc(view, methodName, PhotonTargets.All, target.ID, args);
  }

  static void SendRpc(PhotonView view, string methodName, PhotonTargets target, int targetPlayerId, object[] args)
  {
    if (view == null) return;
    DirectHostRpcRouter router = instance;
    NetworkManager network = router != null ? router.manager : null;
    bool active = network != null && network.IsListening;
    int localPlayerId = PhotonNetwork.player.ID;
    bool localTarget = targetPlayerId != 0
      ? targetPlayerId == localPlayerId
      : target != PhotonTargets.Others && (target != PhotonTargets.MasterClient || !active || network.IsServer);
    if (localTarget) InvokeRpc(view, methodName, args, PhotonNetwork.player);
    if (!active) return;

    byte[] packet = Encode(writer =>
    {
      writer.Write((byte)target);
      writer.Write(targetPlayerId);
      writer.Write(network.LocalClientId);
      writer.Write(view.viewID);
      writer.Write(methodName);
      writer.Write(args == null ? 0 : args.Length);
      if (args != null) foreach (object arg in args) WriteValue(writer, arg);
    });
    if (network.IsServer)
    {
      if (target == PhotonTargets.MasterClient) return;
      if (targetPlayerId == 0) SendRpcToClients(packet, ulong.MaxValue);
      else
      {
        ulong? clientId = PhotonNetwork.GetDirectClientId(targetPlayerId);
        if (clientId.HasValue && clientId.Value != network.LocalClientId)
          SendPacket(RpcName, clientId.Value, packet, NetworkDelivery.ReliableFragmentedSequenced);
      }
    }
    else SendPacket(RpcName, NetworkManager.ServerClientId, packet, NetworkDelivery.ReliableFragmentedSequenced);
  }

  static void SendRpcToClients(byte[] packet, ulong excludedClient)
  {
    DirectHostRpcRouter router = instance;
    if (router == null || router.manager == null || !router.manager.IsServer) return;
    foreach (ulong clientId in router.manager.ConnectedClientsIds)
      if (clientId != router.manager.LocalClientId && clientId != excludedClient)
        SendPacket(RpcName, clientId, packet, NetworkDelivery.ReliableFragmentedSequenced);
  }

  void OnRpcMessage(ulong senderClientId, FastBufferReader reader)
  {
    try
    {
      byte[] packet = ReadPacket(reader);
      using (BinaryReader input = new BinaryReader(new MemoryStream(packet, false)))
      {
        PhotonTargets target = (PhotonTargets)input.ReadByte();
        int targetPlayerId = input.ReadInt32();
        ulong originClientId = input.ReadUInt64();
        int viewId = input.ReadInt32();
        string methodName = input.ReadString();
        int count = input.ReadInt32();
        if (count < 0 || count > 256) throw new InvalidDataException("Invalid RPC argument count.");
        object[] args = new object[count];
        for (int i = 0; i < count; i++) args[i] = ReadValue(input);
        if (input.BaseStream.Position != input.BaseStream.Length) throw new InvalidDataException("Unexpected RPC packet data.");
        if (manager.IsServer && (originClientId != senderClientId || PhotonNetwork.GetDirectPlayerId(senderClientId) <= 0)) return;

        if (manager.IsServer)
        {
          int senderPlayerId = PhotonNetwork.GetDirectPlayerId(senderClientId);
          bool invokeServer = targetPlayerId == 0 || targetPlayerId == PhotonNetwork.player.ID;
          if (invokeServer)
          {
            PhotonView serverView = PhotonView.Find(viewId);
            if (serverView != null) InvokeRpc(serverView, methodName, args, PhotonNetwork.GetDirectPlayer(originClientId));
          }
          if (targetPlayerId == 0)
          {
            if (target != PhotonTargets.MasterClient) SendRpcToClients(packet, senderClientId);
          }
          else if (targetPlayerId != senderPlayerId)
          {
            ulong? recipient = PhotonNetwork.GetDirectClientId(targetPlayerId);
            if (recipient.HasValue && recipient.Value != manager.LocalClientId)
              SendPacket(RpcName, recipient.Value, packet, NetworkDelivery.ReliableFragmentedSequenced);
          }
        }
        else if (senderClientId == NetworkManager.ServerClientId)
        {
          int localPlayerId = PhotonNetwork.player.ID;
          if (targetPlayerId == 0 && target != PhotonTargets.MasterClient || targetPlayerId == localPlayerId)
          {
            PhotonView clientView = PhotonView.Find(viewId);
            if (clientView != null) InvokeRpc(clientView, methodName, args, PhotonNetwork.GetDirectPlayer(originClientId));
          }
        }
      }
    }
    catch (Exception e) { Debug.LogError($"Invalid direct multiplayer RPC from client {senderClientId}: {e}"); }
  }

  static void InvokeRpc(PhotonView view, string methodName, object[] args, PhotonPlayer sender)
  {
    foreach (MonoBehaviour behaviour in view.GetComponents<MonoBehaviour>())
    {
      if (behaviour == null) continue;
      foreach (MethodInfo method in behaviour.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
      {
        if (method.Name != methodName || method.GetCustomAttribute<PunRPC>() == null) continue;
        ParameterInfo[] parameters = method.GetParameters();
        bool hasInfo = parameters.Length > 0 && parameters[parameters.Length - 1].ParameterType == typeof(PhotonMessageInfo);
        if (parameters.Length != args.Length + (hasInfo ? 1 : 0)) continue;
        bool matches = true;
        for (int i = 0; i < args.Length; i++)
        {
          if (args[i] == null ? parameters[i].ParameterType.IsValueType : !parameters[i].ParameterType.IsInstanceOfType(args[i]))
          { matches = false; break; }
        }
        if (!matches) continue;
        object[] invokeArgs = new object[parameters.Length];
        Array.Copy(args, invokeArgs, args.Length);
        if (hasInfo) invokeArgs[invokeArgs.Length - 1] = new PhotonMessageInfo { sender = sender };
        try { method.Invoke(behaviour, invokeArgs); }
        catch (TargetInvocationException e) { Debug.LogException(e.InnerException ?? e); }
        return;
      }
    }
    Debug.LogWarning($"Could not find [PunRPC] method '{methodName}' on {view.name} (view ID {view.viewID}).");
  }

  static NetworkDelivery GetEventDelivery(byte[] packet)
  {
    return packet.Length <= MaxUnreliableEventBytes
      ? NetworkDelivery.Unreliable
      : NetworkDelivery.ReliableFragmentedSequenced;
  }

  public static void RaiseEvent(byte eventCode, object content)
  {
    DirectHostRpcRouter router = instance;
    if (router == null || router.manager == null || !router.manager.IsListening) return;
    byte[] packet = Encode(writer =>
    {
      writer.Write(eventCode);
      writer.Write(router.manager.LocalClientId);
      WriteValue(writer, content);
    });
    if (router.manager.IsServer) SendEventToClients(packet, ulong.MaxValue);
    else SendPacket(EventName, NetworkManager.ServerClientId, packet, GetEventDelivery(packet));
  }

  static void SendEventToClients(byte[] packet, ulong excludedClient)
  {
    DirectHostRpcRouter router = instance;
    if (router == null || router.manager == null || !router.manager.IsServer) return;
    foreach (ulong clientId in router.manager.ConnectedClientsIds)
      if (clientId != router.manager.LocalClientId && clientId != excludedClient)
        SendPacket(EventName, clientId, packet, GetEventDelivery(packet));
  }

  void OnEventMessage(ulong senderClientId, FastBufferReader reader)
  {
    try
    {
      byte[] packet = ReadPacket(reader);
      using (BinaryReader input = new BinaryReader(new MemoryStream(packet, false)))
      {
        byte code = input.ReadByte();
        ulong originClientId = input.ReadUInt64();
        object content = ReadValue(input);
        if (manager.IsServer)
        {
          int originPlayerId = PhotonNetwork.GetDirectPlayerId(senderClientId);
          if (originClientId != senderClientId || originPlayerId <= 0) return;
          SendEventToClients(packet, senderClientId);
          PhotonNetwork.DeliverDirectEvent(code, content, originPlayerId);
        }
        else if (senderClientId == NetworkManager.ServerClientId)
        {
          int originPlayerId = PhotonNetwork.GetDirectPlayerId(originClientId);
          if (originPlayerId > 0) PhotonNetwork.DeliverDirectEvent(code, content, originPlayerId);
        }
      }
    }
    catch (Exception e) { Debug.LogError($"Invalid direct multiplayer event from client {senderClientId}: {e}"); }
  }

  public static void SendUserBodyPose(PhotonView view, Vector3 position, Quaternion rotation)
  {
    DirectHostRpcRouter router = instance;
    if (view == null || router == null || router.manager == null || !router.manager.IsListening) return;
    if (view.viewID <= 0) return;
    byte[] packet = Encode(writer =>
    {
      writer.Write(view.viewID);
      writer.Write(view.ownerId);
      writer.Write(position.x);
      writer.Write(position.y);
      writer.Write(position.z);
      writer.Write(rotation.x);
      writer.Write(rotation.y);
      writer.Write(rotation.z);
      writer.Write(rotation.w);
    });
    if (router.manager.IsServer) router.SendPoseToClients(packet, ulong.MaxValue);
    else SendPacket(PoseName, NetworkManager.ServerClientId, packet, NetworkDelivery.Unreliable);
  }

  void SendPoseToClients(byte[] packet, ulong excludedClient)
  {
    foreach (ulong clientId in manager.ConnectedClientsIds)
      if (clientId != manager.LocalClientId && clientId != excludedClient)
        SendPacket(PoseName, clientId, packet, NetworkDelivery.Unreliable);
  }

  void OnPoseMessage(ulong senderClientId, FastBufferReader reader)
  {
    try
    {
      byte[] packet = ReadPacket(reader);
      using (BinaryReader input = new BinaryReader(new MemoryStream(packet, false)))
      {
        int viewId = input.ReadInt32();
        int ownerPlayerId = input.ReadInt32();
        Vector3 position = new Vector3(input.ReadSingle(), input.ReadSingle(), input.ReadSingle());
        Quaternion rotation = new Quaternion(input.ReadSingle(), input.ReadSingle(), input.ReadSingle(), input.ReadSingle());
        float rotationMagnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        if (input.BaseStream.Position != input.BaseStream.Length
          || float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)
          || float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z)
          || float.IsNaN(rotation.x) || float.IsNaN(rotation.y) || float.IsNaN(rotation.z) || float.IsNaN(rotation.w)
          || float.IsInfinity(rotation.x) || float.IsInfinity(rotation.y) || float.IsInfinity(rotation.z) || float.IsInfinity(rotation.w)
          || rotationMagnitude < 0.0001f) return;
        rotation = rotation.normalized;
        PhotonView view = PhotonView.Find(viewId);
        if (manager.IsServer)
        {
          if (ownerPlayerId != PhotonNetwork.GetDirectPlayerId(senderClientId) || view == null || view.ownerId != ownerPlayerId) return;
          UserBody body = view.GetComponent<UserBody>();
          if (body == null) return;
          body.SetDirectPose(position, rotation);
          SendPoseToClients(packet, senderClientId);
        }
        else if (senderClientId == NetworkManager.ServerClientId && view != null && view.ownerId == ownerPlayerId
          && ownerPlayerId != PhotonNetwork.player.ID)
        {
          UserBody body = view.GetComponent<UserBody>();
          if (body != null)
          {
            body.SetDirectPose(position, rotation);
          }
        }
      }
    }
    catch (Exception e) { Debug.LogError($"Invalid direct player pose from client {senderClientId}: {e}"); }
  }

  void SendOwnedObservableStates()
  {
    float now = Time.realtimeSinceStartup;
    foreach (PhotonView view in PhotonView.GetAllViews())
    {
      if (view == null || view.viewID == 0 || !view.isMine) continue;
      int componentIndex = 0;
      foreach (MonoBehaviour behaviour in view.GetComponents<MonoBehaviour>())
      {
        if (!(behaviour is IPunObservable observable)) continue;
        PhotonStream stream = new PhotonStream(true);
        try { observable.OnPhotonSerializeView(stream, new PhotonMessageInfo { sender = PhotonNetwork.player }); }
        catch (Exception e) { Debug.LogException(e); componentIndex++; continue; }
        byte[] state = stream.ToBytes();
        long key = ((long)view.viewID << 16) ^ (uint)componentIndex;
        bool changed = !sentStates.TryGetValue(key, out ObservableState previous) || !BytesEqual(previous.bytes, state);
        if (changed || now - previous.sentAt >= ObservableRefresh)
        {
          byte[] packet = Encode(writer =>
          {
            writer.Write(view.viewID);
            writer.Write(PhotonNetwork.player.ID);
            writer.Write(componentIndex);
            writer.Write(state.Length);
            writer.Write(state);
          });
          if (manager.IsServer)
          {
            foreach (ulong clientId in manager.ConnectedClientsIds)
              if (clientId != manager.LocalClientId)
                SendPacket(ObservableName, clientId, packet, NetworkDelivery.ReliableFragmentedSequenced);
          }
          else SendPacket(ObservableName, NetworkManager.ServerClientId, packet, NetworkDelivery.ReliableFragmentedSequenced);
          sentStates[key] = new ObservableState { bytes = state, sentAt = now };
        }
        componentIndex++;
      }
    }
  }

  static bool BytesEqual(byte[] a, byte[] b)
  {
    if (a == null || b == null || a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
    return true;
  }
  public static void ForceResend(PhotonView view)
  {
    if (view == null) return;
    for (int i = 0; i <= 64; i++) sentStates.Remove(((long)view.viewID << 16) ^ (uint)i);
  }

  public static void ForceResendAll()
  {
    sentStates.Clear();
  }

  void OnObservableMessage(ulong senderClientId, FastBufferReader reader)
  {
    try
    {
      byte[] packet = ReadPacket(reader);
      using (BinaryReader input = new BinaryReader(new MemoryStream(packet, false)))
      {
        int viewId = input.ReadInt32();
        int ownerPlayerId = input.ReadInt32();
        int componentIndex = input.ReadInt32();
        int length = input.ReadInt32();
        if (ownerPlayerId <= 0 || componentIndex < 0 || componentIndex > 64 || length < 0 || length > MaxPacketBytes)
          throw new InvalidDataException("Invalid observable state packet.");
        byte[] state = input.ReadBytes(length);
        if (state.Length != length || input.BaseStream.Position != input.BaseStream.Length)
          throw new InvalidDataException("Truncated observable state packet.");
        PhotonView view = PhotonView.Find(viewId);
        if (manager.IsServer)
        {
          int senderPlayerId = PhotonNetwork.GetDirectPlayerId(senderClientId);
          if (view == null || ownerPlayerId != senderPlayerId || view.ownerId != senderPlayerId) return;
          InvokeObservable(view, componentIndex, state, PhotonNetwork.GetDirectPlayer(senderClientId));
          foreach (ulong clientId in manager.ConnectedClientsIds)
            if (clientId != manager.LocalClientId && clientId != senderClientId)
              SendPacket(ObservableName, clientId, packet, NetworkDelivery.ReliableFragmentedSequenced);
        }
        else if (senderClientId == NetworkManager.ServerClientId && view != null && view.ownerId == ownerPlayerId)
        {
          PhotonPlayer sender = PhotonNetwork.FindPlayer(ownerPlayerId);
          if (sender != null) InvokeObservable(view, componentIndex, state, sender);
        }
      }
    }
    catch (Exception e) { Debug.LogError($"Invalid direct multiplayer observable from client {senderClientId}: {e}"); }
  }

  static void InvokeObservable(PhotonView view, int index, byte[] bytes, PhotonPlayer sender)
  {
    int current = 0;
    foreach (MonoBehaviour behaviour in view.GetComponents<MonoBehaviour>())
    {
      if (!(behaviour is IPunObservable observable)) continue;
      if (current++ != index) continue;
      try { observable.OnPhotonSerializeView(PhotonStream.FromBytes(bytes), new PhotonMessageInfo { sender = sender }); }
      catch (Exception e) { Debug.LogException(e); }
      return;
    }
  }

  public static void SendOwnership(int viewId, int newOwnerId)
  {
    DirectHostRpcRouter router = instance;
    if (router == null || router.manager == null || !router.manager.IsListening) return;
    byte[] packet = Encode(writer => { writer.Write(viewId); writer.Write(newOwnerId); });
    if (router.manager.IsServer)
    {
      foreach (ulong clientId in router.manager.ConnectedClientsIds)
        if (clientId != router.manager.LocalClientId)
          SendPacket(OwnershipName, clientId, packet, NetworkDelivery.ReliableSequenced);
    }
    else SendPacket(OwnershipName, NetworkManager.ServerClientId, packet, NetworkDelivery.ReliableSequenced);
  }

  void OnOwnershipMessage(ulong senderClientId, FastBufferReader reader)
  {
    try
    {
      byte[] packet = ReadPacket(reader);
      using (BinaryReader input = new BinaryReader(new MemoryStream(packet, false)))
      {
        int viewId = input.ReadInt32();
        int newOwnerId = input.ReadInt32();
        PhotonView view = PhotonView.Find(viewId);
        if (view == null) return;
        if (manager.IsServer)
        {
          if (view.ownerId != PhotonNetwork.GetDirectPlayerId(senderClientId)) return;
          view.ApplyOwnership(newOwnerId);
          foreach (ulong clientId in manager.ConnectedClientsIds)
            if (clientId != manager.LocalClientId && clientId != senderClientId)
              SendPacket(OwnershipName, clientId, packet, NetworkDelivery.ReliableSequenced);
        }
        else if (senderClientId == NetworkManager.ServerClientId) view.ApplyOwnership(newOwnerId);
      }
    }
    catch (Exception e) { Debug.LogError($"Invalid direct multiplayer ownership update from client {senderClientId}: {e}"); }
  }
}
#endif
