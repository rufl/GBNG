#if !USE_PUN
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

public sealed class DirectHostUdpRelay : MonoBehaviour
{
  const int HeaderSize = 37;
  const float HeartbeatSeconds = 3f;

  sealed class HostRoute
  {
    public ulong ClientId;
    public UdpClient Socket;
    public Thread Thread;
    public volatile bool Running;
    public long LastActivityTicks;
  }

  static DirectHostUdpRelay instance;
  static readonly Func<IPAddress, bool> relayAddressHasRoute = HasRelayRoute;
  readonly object socketGate = new object();
  readonly object routesGate = new object();
  readonly Dictionary<ulong, HostRoute> routes = new Dictionary<ulong, HostRoute>();
  NetworkManager manager;
  UdpClient relaySocket;
  IPEndPoint relayEndpoint;
  Thread relayThread;
  byte[] sessionKey;
  string sessionId;
  string activeConfig;
  int hostPort;
  bool hostIpv6;
  volatile bool running;
  ulong heartbeatSequence;
  long lastHeartbeatAckTicks;

  public static bool IsTunnelReady
  {
    get
    {
      DirectHostUdpRelay current = instance;
      if (current == null || !current.running) return false;
      long acknowledged = Interlocked.Read(ref current.lastHeartbeatAckTicks);
      return acknowledged != 0
        && DateTime.UtcNow.Ticks - acknowledged < TimeSpan.FromSeconds(10).Ticks;
    }
  }

  public static void EnsureAvailable()
  {
    if (instance != null) return;
    GameObject serviceObject = new GameObject("DirectHostUdpRelay");
    DontDestroyOnLoad(serviceObject);
    serviceObject.AddComponent<DirectHostUdpRelay>();
  }

  void Awake()
  {
    if (instance != null && instance != this)
    {
      Destroy(gameObject);
      return;
    }
    instance = this;
    manager = NetworkManager.Singleton;
  }

  void Update()
  {
    if (manager == null) manager = NetworkManager.Singleton;
    bool shouldRun = manager != null && manager.IsListening && manager.IsServer;
    if (!shouldRun)
    {
      StopRelay();
      return;
    }

    if (!DirectSessionDirectory.TryGetHostRelayCredentials(
      out string id, out string token, out string address, out ushort port))
    {
      StopRelay();
      return;
    }

    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    ushort listenPort = options.joinPort == 0
      ? NetworkingController.DefaultDirectHostPort
      : options.joinPort;
    string config = id + "|" + token + "|" + address + "|" + port
      + "|" + listenPort + "|" + options.directHostUsesIpv6;
    if (config == activeConfig) return;

    StopRelay();
    try
    {
      IPAddress relayAddress = ResolveRelayAddress(address);
      relayEndpoint = new IPEndPoint(relayAddress, port);
      relaySocket = new UdpClient(relayAddress.AddressFamily);
      relaySocket.Client.ReceiveTimeout = 100;
      relaySocket.Client.SendTimeout = 1000;
      sessionId = id;
      sessionKey = HashToken(token);
      hostPort = listenPort;
      hostIpv6 = options.directHostUsesIpv6;
      heartbeatSequence = (ulong)DateTime.UtcNow.Ticks;
      activeConfig = config;
      running = true;
      relayThread = new Thread(RunRelaySocket) { IsBackground = true, Name = "GameBuilder UDP relay" };
      relayThread.Start();
      Debug.Log("Direct-host UDP relay connected to " + relayEndpoint + ".");
    }
    catch (Exception e)
    {
      StopRelay();
      Debug.LogWarning("Could not start the direct-host UDP relay tunnel: " + e.Message);
    }
  }

  static IPAddress ResolveRelayAddress(string host)
  {
    IPAddress literal;
    if (IPAddress.TryParse(host, out literal)) return literal;
    return SelectRelayAddress(Dns.GetHostAddresses(host), relayAddressHasRoute);
  }

  static IPAddress SelectRelayAddress(IPAddress[] addresses, Func<IPAddress, bool> canReach)
  {
    if (addresses == null) throw new ArgumentNullException(nameof(addresses));
    if (canReach == null) throw new ArgumentNullException(nameof(canReach));
    foreach (IPAddress address in addresses)
    {
      if (address != null && canReach(address)) return address;
    }
    throw new SocketException((int)SocketError.NetworkUnreachable);
  }

  static bool HasRelayRoute(IPAddress address)
  {
    try
    {
      using (Socket socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
      {
        socket.Connect(new IPEndPoint(address, 1));
        return true;
      }
    }
    catch (SocketException)
    {
      return false;
    }
  }

  static byte[] HashToken(string token)
  {
    using (SHA256 sha = SHA256.Create())
      return sha.ComputeHash(Encoding.ASCII.GetBytes(token));
  }

  void RunRelaySocket()
  {
    DateTime nextHeartbeat = DateTime.MinValue;
    while (running)
    {
      if (DateTime.UtcNow >= nextHeartbeat)
      {
        try { SendHeartbeat(); }
        catch (Exception e)
        {
          if (running) Debug.LogWarning("Direct-host UDP relay heartbeat failed: " + e.Message);
        }
        nextHeartbeat = DateTime.UtcNow.AddSeconds(HeartbeatSeconds);
      }

      try
      {
        IPEndPoint sender = relayEndpoint.AddressFamily == AddressFamily.InterNetworkV6
          ? new IPEndPoint(IPAddress.IPv6Any, 0)
          : new IPEndPoint(IPAddress.Any, 0);
        byte[] frame = relaySocket.Receive(ref sender);
        if (sender.Address.Equals(relayEndpoint.Address) && sender.Port == relayEndpoint.Port)
          ProcessRelayFrame(frame);
      }
      catch (SocketException e)
      {
        if (!running) break;
        if (e.SocketErrorCode != SocketError.TimedOut && e.SocketErrorCode != SocketError.WouldBlock)
          Debug.LogWarning("Direct-host UDP relay receive failed: " + e.Message);
      }
      catch (ObjectDisposedException)
      {
        break;
      }
    }
  }

  void SendHeartbeat()
  {
    byte[] frame = CreateFrame((byte)'H', sessionId, ++heartbeatSequence, null);
    byte[] signature;
    using (HMACSHA256 hmac = new HMACSHA256(sessionKey))
      signature = hmac.ComputeHash(frame, 0, HeaderSize);
    byte[] signedFrame = new byte[HeaderSize + signature.Length];
    Buffer.BlockCopy(frame, 0, signedFrame, 0, HeaderSize);
    Buffer.BlockCopy(signature, 0, signedFrame, HeaderSize, signature.Length);
    SendToRelay(signedFrame);
  }

  void ProcessRelayFrame(byte[] frame)
  {
    if (!TryReadFrame(frame, sessionId, out byte messageType, out ulong clientId)) return;
    if (messageType == (byte)'R')
    {
      if (frame.Length == HeaderSize && clientId == heartbeatSequence)
        Interlocked.Exchange(ref lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
      return;
    }
    if (messageType == (byte)'X')
    {
      CloseRoute(clientId);
      return;
    }
    if (messageType != (byte)'D' || frame.Length <= HeaderSize) return;

    HostRoute route;
    lock (routesGate)
    {
      if (!routes.TryGetValue(clientId, out route))
      {
        route = CreateHostRoute(clientId);
        routes.Add(clientId, route);
        route.Thread.Start();
      }
    }
    Interlocked.Exchange(ref route.LastActivityTicks, DateTime.UtcNow.Ticks);

    try
    {
      IPAddress localAddress = hostIpv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
      route.Socket.Client.SendTo(
        frame, HeaderSize, frame.Length - HeaderSize, SocketFlags.None,
        new IPEndPoint(localAddress, hostPort));
    }
    catch (ObjectDisposedException) { }
    catch (SocketException e)
    {
      Debug.LogWarning("Could not forward a relayed datagram to Unity Transport: " + e.Message);
      CloseRoute(clientId);
    }
  }

  HostRoute CreateHostRoute(ulong clientId)
  {
    AddressFamily family = hostIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
    HostRoute route = new HostRoute
    {
      ClientId = clientId,
      Socket = new UdpClient(family),
      Running = true,
      LastActivityTicks = DateTime.UtcNow.Ticks
    };
    route.Socket.Client.ReceiveTimeout = 500;
    route.Thread = new Thread(() => ReceiveHostReplies(route))
    {
      IsBackground = true,
      Name = "GameBuilder relayed peer " + clientId
    };
    return route;
  }

  void ReceiveHostReplies(HostRoute route)
  {
    while (running && route.Running)
    {
      try
      {
        IPEndPoint sender = hostIpv6
          ? new IPEndPoint(IPAddress.IPv6Any, 0)
          : new IPEndPoint(IPAddress.Any, 0);
        byte[] payload = route.Socket.Receive(ref sender);
        if (payload.Length == 0) continue;
        SendToRelay(CreateFrame((byte)'B', sessionId, route.ClientId, payload));
      }
      catch (SocketException e)
      {
        if (!running || !route.Running) break;
        if (e.SocketErrorCode != SocketError.TimedOut && e.SocketErrorCode != SocketError.WouldBlock)
        {
          Debug.LogWarning("Could not receive a Unity Transport relay reply: " + e.Message);
          break;
        }
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref route.LastActivityTicks)
          > TimeSpan.FromSeconds(75).Ticks)
          break;
      }
      catch (ObjectDisposedException) { break; }
      catch (Exception e)
      {
        if (running && route.Running) Debug.LogWarning("Direct-host UDP relay peer failed: " + e.Message);
        break;
      }
    }
    RemoveRoute(route.ClientId, route);
  }

  void SendToRelay(byte[] frame)
  {
    lock (socketGate)
    {
      if (running && relaySocket != null)
        relaySocket.Send(frame, frame.Length, relayEndpoint);
    }
  }

  void CloseRoute(ulong clientId)
  {
    HostRoute route = null;
    lock (routesGate)
    {
      if (routes.TryGetValue(clientId, out route)) routes.Remove(clientId);
    }
    StopRoute(route);
  }

  void RemoveRoute(ulong clientId, HostRoute route)
  {
    bool removed = false;
    lock (routesGate)
    {
      if (routes.TryGetValue(clientId, out HostRoute current) && current == route)
      {
        routes.Remove(clientId);
        removed = true;
      }
    }
    if (removed && running)
      SendToRelay(CreateFrame((byte)'X', sessionId, clientId, null));
    StopRoute(route);
  }

  static void StopRoute(HostRoute route)
  {
    if (route == null) return;
    route.Running = false;
    try { route.Socket.Close(); }
    catch (ObjectDisposedException) { }
    if (route.Thread != null && route.Thread != Thread.CurrentThread)
      route.Thread.Join(250);
  }

  void StopRelay()
  {
    running = false;
    UdpClient socket;
    Thread thread;
    lock (socketGate)
    {
      socket = relaySocket;
      relaySocket = null;
    }
    if (socket != null)
    {
      try { socket.Close(); }
      catch (ObjectDisposedException) { }
    }
    thread = relayThread;
    relayThread = null;
    if (thread != null && thread != Thread.CurrentThread) thread.Join(250);

    List<HostRoute> oldRoutes;
    lock (routesGate)
    {
      oldRoutes = new List<HostRoute>(routes.Values);
      routes.Clear();
    }
    foreach (HostRoute route in oldRoutes) StopRoute(route);
    relayEndpoint = null;
    heartbeatSequence = 0;
    Interlocked.Exchange(ref lastHeartbeatAckTicks, 0);
  }

  void OnDestroy()
  {
    StopRelay();
    if (instance == this) instance = null;
  }

  internal sealed class AllocationResult
  {
    public string Address;
    public ushort Port;
  }

  internal static AllocationResult Allocate(string relayAddress, ushort controlPort, string id)
  {
    if (string.IsNullOrWhiteSpace(relayAddress) || controlPort == 0 || string.IsNullOrEmpty(id) || id.Length != 24)
      throw new ArgumentException("The relay allocation information is invalid.");

    IPAddress remoteAddress = ResolveRelayAddress(relayAddress);
    IPEndPoint relay = new IPEndPoint(remoteAddress, controlPort);
    using (UdpClient client = new UdpClient(remoteAddress.AddressFamily))
    {
      client.Client.ReceiveTimeout = 700;
      client.Connect(relay);
      ulong nonce = CreateNonce();
      byte[] request = CreateFrame((byte)'A', id, nonce, null);
      for (int attempt = 0; attempt < 4; attempt++)
      {
        client.Send(request, request.Length);
        try
        {
          IPEndPoint sender = remoteAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
          byte[] response = client.Receive(ref sender);
          if (!sender.Address.Equals(remoteAddress) || sender.Port != controlPort
            || !TryReadFrame(response, id, out byte messageType, out ulong responseNonce)
            || messageType != (byte)'O' || responseNonce != nonce
            || response.Length != HeaderSize + 2)
            continue;
          ushort port = (ushort)((response[HeaderSize] << 8) | response[HeaderSize + 1]);
          if (port == 0) continue;
          return new AllocationResult { Address = remoteAddress.ToString(), Port = port };
        }
        catch (SocketException e)
        {
          if (e.SocketErrorCode != SocketError.TimedOut && e.SocketErrorCode != SocketError.WouldBlock)
            throw;
        }
      }
    }
    throw new TimeoutException("The relay did not allocate a UDP route for this session.");
  }

  static ulong CreateNonce()
  {
    byte[] bytes = new byte[8];
    using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
    ulong value = ReadUInt64(bytes, 0);
    return value == 0 ? 1UL : value;
  }

  static byte[] CreateFrame(byte messageType, string id, ulong value, byte[] payload)
  {
    if (string.IsNullOrEmpty(id) || id.Length != 24)
      throw new ArgumentException("A relay session id must contain exactly 24 characters.", nameof(id));
    byte[] sessionBytes = Encoding.ASCII.GetBytes(id);
    if (sessionBytes.Length != 24) throw new ArgumentException("The relay session id must be ASCII.", nameof(id));
    byte[] frame = new byte[HeaderSize + (payload == null ? 0 : payload.Length)];
    frame[0] = (byte)'G';
    frame[1] = (byte)'B';
    frame[2] = (byte)'R';
    frame[3] = (byte)'1';
    frame[4] = messageType;
    Buffer.BlockCopy(sessionBytes, 0, frame, 5, 24);
    WriteUInt64(frame, 29, value);
    if (payload != null) Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
    return frame;
  }

  static bool TryReadFrame(byte[] frame, string expectedSessionId, out byte messageType, out ulong value)
  {
    messageType = 0;
    value = 0;
    if (frame == null || frame.Length < HeaderSize
      || frame[0] != (byte)'G' || frame[1] != (byte)'B'
      || frame[2] != (byte)'R' || frame[3] != (byte)'1') return false;
    byte[] expected = Encoding.ASCII.GetBytes(expectedSessionId);
    if (expected.Length != 24) return false;
    for (int i = 0; i < expected.Length; i++)
      if (frame[5 + i] != expected[i]) return false;
    messageType = frame[4];
    value = ReadUInt64(frame, 29);
    return true;
  }

  static void WriteUInt64(byte[] bytes, int offset, ulong value)
  {
    for (int i = 7; i >= 0; i--)
    {
      bytes[offset + i] = (byte)value;
      value >>= 8;
    }
  }

  static ulong ReadUInt64(byte[] bytes, int offset)
  {
    ulong value = 0;
    for (int i = 0; i < 8; i++) value = (value << 8) | bytes[offset + i];
    return value;
  }
}
#endif
