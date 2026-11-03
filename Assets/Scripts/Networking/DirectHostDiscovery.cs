#if !USE_PUN
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public sealed class DirectHostDiscovery : MonoBehaviour
{
  const uint ProtocolMagic = 0x47424431;
  const ushort DiscoveryPort = 47778;
  const string Ipv6DiscoveryGroupAddress = "ff02::4742";
  static readonly IPAddress Ipv6DiscoveryGroup = IPAddress.Parse(Ipv6DiscoveryGroupAddress);
  static readonly byte[] Ipv6DiscoveryGroupBytes = Ipv6DiscoveryGroup.GetAddressBytes();
  const int MaxNameBytes = 96;
  const int MaxPacketBytes = 128;
  const float AdvertisementInterval = 1f;
  const float HostExpirationSeconds = 6f;
  const float SocketRetryInterval = 5f;

  public sealed class HostInfo
  {
    public string Address;
    public ushort Port;
    public string Name;
    public int PlayerCount;
    public int MaxPlayers;
    internal float LastSeen;
  }

  static DirectHostDiscovery instance;
  static int revision;
  readonly Dictionary<string, HostInfo> hosts = new Dictionary<string, HostInfo>();
  readonly byte[] receiveBuffer = new byte[MaxPacketBytes];
  Socket discoverySocket;
  Socket broadcastSocket;
  Socket ipv6DiscoverySocket;
  Socket ipv6AdvertisementSocket;
  NetworkManager manager;
  bool browsing;
  float nextAdvertisementTime;
  float nextDiscoverySocketAttempt;
  float nextBroadcastSocketAttempt;
  float nextIpv6DiscoverySocketAttempt;
  float nextIpv6AdvertisementSocketAttempt;

  public static int Revision { get { return revision; } }

  public static void EnsureAvailable()
  {
    EnsureInstance();
  }

  public static void BeginBrowsing()
  {
    EnsureInstance().browsing = true;
  }

  public static HostInfo[] GetDiscoveredHosts()
  {
    DirectHostDiscovery service = EnsureInstance();
    HostInfo[] result = new HostInfo[service.hosts.Count];
    service.hosts.Values.CopyTo(result, 0);
    Array.Sort(result, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    return result;
  }

  static DirectHostDiscovery EnsureInstance()
  {
    if (instance != null) return instance;
    GameObject serviceObject = new GameObject("DirectHostDiscovery");
    DontDestroyOnLoad(serviceObject);
    return serviceObject.AddComponent<DirectHostDiscovery>();
  }

  void Awake()
  {
    if (instance != null && instance != this)
    {
      Destroy(gameObject);
      return;
    }
    instance = this;
  }

  void Update()
  {
    if (manager == null) manager = NetworkManager.Singleton;
    if (manager != null && manager.IsListening && manager.IsServer)
    {
      if (NetworkingController.IsDirectIpv6Host)
        AdvertiseIpv6Host();
      else
        AdvertiseHost();
    }

    if (browsing)
    {
      PollDiscoverySocket();
      PollIpv6DiscoverySocket();
      ExpireHosts();
    }
  }

  void AdvertiseHost()
  {
    float now = Time.realtimeSinceStartup;
    if (now < nextAdvertisementTime) return;
    nextAdvertisementTime = now + AdvertisementInterval;
    if (broadcastSocket == null)
    {
      if (now < nextBroadcastSocketAttempt) return;
      nextBroadcastSocketAttempt = now + SocketRetryInterval;
      try
      {
        broadcastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        broadcastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host LAN discovery could not open its UDP socket: {e.Message}");
        CloseSocket(ref broadcastSocket);
        return;
      }
    }

    byte[] packet = CreateAdvertisementPacket();

    HashSet<string> destinations = new HashSet<string>();
    destinations.Add(IPAddress.Broadcast.ToString());
    try
    {
      foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
        foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
        {
          IPAddress address = unicast.Address;
          if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || unicast.IPv4Mask == null) continue;
          byte[] addressBytes = address.GetAddressBytes();
          byte[] maskBytes = unicast.IPv4Mask.GetAddressBytes();
          byte[] broadcastBytes = new byte[4];
          for (int i = 0; i < broadcastBytes.Length; i++)
            broadcastBytes[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);
          destinations.Add(new IPAddress(broadcastBytes).ToString());
        }
      }
    }
    catch (NetworkInformationException)
    {
      // The global broadcast target remains usable if interface enumeration fails.
    }
    foreach (string destination in destinations)
    {
      try
      {
        broadcastSocket.SendTo(packet, new IPEndPoint(IPAddress.Parse(destination), DiscoveryPort));
      }
      catch (SocketException)
      {
        // A disabled or disconnected network interface must not stop advertisements on others.
      }
    }

    foreach (string destination in destinations)
    {
      try
      {
        broadcastSocket.SendTo(packet, new IPEndPoint(IPAddress.Parse(destination), DiscoveryPort));
      }
      catch (SocketException)
      {
        // A disabled or disconnected network interface must not stop advertisements on others.
      }
    }
  }

  byte[] CreateAdvertisementPacket()
  {
    string name = GameBuilderApplication.CurrentGameOptions.displayName;
    if (string.IsNullOrWhiteSpace(name))
      name = string.IsNullOrWhiteSpace(PhotonNetwork.player.NickName) ? "Game Builder game" : PhotonNetwork.player.NickName + "'s game";
    byte[] nameBytes = Encoding.UTF8.GetBytes(name);
    while (nameBytes.Length > MaxNameBytes && name.Length > 0)
    {
      name = name.Substring(0, name.Length - 1);
      nameBytes = Encoding.UTF8.GetBytes(name);
    }

    int playerCount = Mathf.Clamp(PhotonNetwork.playerList == null ? 1 : PhotonNetwork.playerList.Length, 1, PhotonNetwork.MaximumDirectPlayers);
    using (MemoryStream stream = new MemoryStream())
    using (BinaryWriter writer = new BinaryWriter(stream))
    {
      writer.Write(ProtocolMagic);
      writer.Write(NetworkingController.DefaultDirectHostPort);
      writer.Write((byte)playerCount);
      writer.Write((byte)PhotonNetwork.MaximumDirectPlayers);
      writer.Write((byte)nameBytes.Length);
      writer.Write(nameBytes);
      return stream.ToArray();
    }
  }

  void AdvertiseIpv6Host()
  {
    float now = Time.realtimeSinceStartup;
    if (now < nextAdvertisementTime) return;
    nextAdvertisementTime = now + AdvertisementInterval;
    if (ipv6AdvertisementSocket == null)
    {
      if (now < nextIpv6AdvertisementSocketAttempt) return;
      nextIpv6AdvertisementSocketAttempt = now + SocketRetryInterval;
      try
      {
        ipv6AdvertisementSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        ipv6AdvertisementSocket.DualMode = false;
        ipv6AdvertisementSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, 1);
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host IPv6 LAN discovery could not open its UDP socket: {e.Message}");
        CloseSocket(ref ipv6AdvertisementSocket);
        return;
      }
    }

    byte[] packet = CreateAdvertisementPacket();
    try
    {
      foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (networkInterface.OperationalStatus != OperationalStatus.Up || !networkInterface.SupportsMulticast) continue;
        IPv6InterfaceProperties ipv6Properties;
        try { ipv6Properties = networkInterface.GetIPProperties().GetIPv6Properties(); }
        catch (NetworkInformationException) { continue; }
        if (ipv6Properties == null || ipv6Properties.Index <= 0) continue;
        try
        {
          IPAddress groupAddress = new IPAddress(Ipv6DiscoveryGroupBytes, ipv6Properties.Index);
          ipv6AdvertisementSocket.SendTo(packet, new IPEndPoint(groupAddress, DiscoveryPort));
        }
        catch (SocketException)
        {
          // A disabled or disconnected network interface must not stop advertisements on others.
        }
      }
    }
    catch (NetworkInformationException)
    {
      // A later advertisement retries after the network interface list recovers.
    }
  }

  void PollIpv6DiscoverySocket()
  {
    if (ipv6DiscoverySocket == null)
    {
      float now = Time.realtimeSinceStartup;
      if (now < nextIpv6DiscoverySocketAttempt) return;
      nextIpv6DiscoverySocketAttempt = now + SocketRetryInterval;
      try
      {
        ipv6DiscoverySocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        ipv6DiscoverySocket.DualMode = false;
        ipv6DiscoverySocket.ExclusiveAddressUse = false;
        ipv6DiscoverySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        ipv6DiscoverySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, DiscoveryPort));
        int joinedInterfaces = 0;
        try
        {
          foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
          {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || !networkInterface.SupportsMulticast) continue;
            IPv6InterfaceProperties ipv6Properties;
            try { ipv6Properties = networkInterface.GetIPProperties().GetIPv6Properties(); }
            catch (NetworkInformationException) { continue; }
            if (ipv6Properties == null || ipv6Properties.Index <= 0) continue;
            try
            {
              ipv6DiscoverySocket.SetSocketOption(
                SocketOptionLevel.IPv6,
                SocketOptionName.AddMembership,
                new IPv6MulticastOption(Ipv6DiscoveryGroup, ipv6Properties.Index));
              joinedInterfaces++;
            }
            catch (SocketException)
            {
              // One unusable adapter must not prevent listening on the remaining interfaces.
            }
          }
        }
        catch (NetworkInformationException)
        {
          // Keep any memberships already joined; future socket retries re-enumerate adapters.
        }

        if (joinedInterfaces == 0)
        {
          CloseSocket(ref ipv6DiscoverySocket);
          return;
        }
        ipv6DiscoverySocket.Blocking = false;
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host IPv6 LAN discovery could not listen on UDP {DiscoveryPort}: {e.Message}");
        CloseSocket(ref ipv6DiscoverySocket);
        return;
      }
    }

    for (int i = 0; i < 32 && ipv6DiscoverySocket.Available > 0; i++)
    {
      EndPoint endpoint = new IPEndPoint(IPAddress.IPv6Any, 0);
      try
      {
        int length = ipv6DiscoverySocket.ReceiveFrom(receiveBuffer, ref endpoint);
        if (endpoint is IPEndPoint remote && TryParseHost(receiveBuffer, length, remote.Address, out HostInfo host))
          RecordHost(host);
      }
      catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
      {
        break;
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host IPv6 LAN discovery receive failed: {e.Message}");
        CloseSocket(ref ipv6DiscoverySocket);
        break;
      }
    }
  }

  void PollDiscoverySocket()
  {
    if (discoverySocket == null)
    {
      float now = Time.realtimeSinceStartup;
      if (now < nextDiscoverySocketAttempt) return;
      nextDiscoverySocketAttempt = now + SocketRetryInterval;
      try
      {
        discoverySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        discoverySocket.ExclusiveAddressUse = false;
        discoverySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        discoverySocket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        discoverySocket.Blocking = false;
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host LAN discovery could not listen on UDP {DiscoveryPort}: {e.Message}");
        CloseSocket(ref discoverySocket);
        return;
      }
    }

    for (int i = 0; i < 32 && discoverySocket.Available > 0; i++)
    {
      EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
      try
      {
        int length = discoverySocket.ReceiveFrom(receiveBuffer, ref endpoint);
        if (endpoint is IPEndPoint remote && TryParseHost(receiveBuffer, length, remote.Address, out HostInfo host))
          RecordHost(host);
      }
      catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
      {
        break;
      }
      catch (SocketException e)
      {
        Debug.LogWarning($"Direct-host LAN discovery receive failed: {e.Message}");
        CloseSocket(ref discoverySocket);
        break;
      }
    }
  }

  bool TryParseHost(byte[] packet, int length, IPAddress address, out HostInfo host)
  {
    host = null;
    if (length < sizeof(uint) + sizeof(ushort) + 3 || length > MaxPacketBytes) return false;
    if (manager != null && manager.IsListening && manager.IsServer && IsLocalAddress(address)) return false;
    try
    {
      using (MemoryStream stream = new MemoryStream(packet, 0, length, false))
      using (BinaryReader reader = new BinaryReader(stream))
      {
        if (reader.ReadUInt32() != ProtocolMagic) return false;
        ushort port = reader.ReadUInt16();
        int playerCount = reader.ReadByte();
        int maxPlayers = reader.ReadByte();
        int nameLength = reader.ReadByte();
        if (port == 0 || playerCount < 1 || maxPlayers < playerCount || maxPlayers > PhotonNetwork.MaximumDirectPlayers
          || nameLength < 1 || nameLength > MaxNameBytes || nameLength != stream.Length - stream.Position) return false;
        string name = new UTF8Encoding(false, true).GetString(reader.ReadBytes(nameLength));
        if (string.IsNullOrWhiteSpace(name)) return false;
        host = new HostInfo
        {
          Address = address.ToString(),
          Port = port,
          Name = name,
          PlayerCount = playerCount,
          MaxPlayers = maxPlayers,
          LastSeen = Time.realtimeSinceStartup
        };
        return true;
      }
    }
    catch (Exception e) when (e is IOException || e is ArgumentException || e is DecoderFallbackException)
    {
      return false;
    }
  }

  static bool IsLocalAddress(IPAddress address)
  {
    try
    {
      foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
          if (unicast.Address.Equals(address)) return true;
    }
    catch (NetworkInformationException)
    {
      // Loopback filtering still applies if interface enumeration is unavailable.
    }
    return IPAddress.IsLoopback(address);
  }

  void RecordHost(HostInfo host)
  {
    string key = host.Address + ":" + host.Port;
    if (hosts.TryGetValue(key, out HostInfo previous))
    {
      bool changed = previous.Name != host.Name || previous.PlayerCount != host.PlayerCount || previous.MaxPlayers != host.MaxPlayers;
      host.LastSeen = Time.realtimeSinceStartup;
      hosts[key] = host;
      if (changed) revision++;
      return;
    }
    hosts.Add(key, host);
    revision++;
  }

  void ExpireHosts()
  {
    float now = Time.realtimeSinceStartup;
    List<string> expired = null;
    foreach (KeyValuePair<string, HostInfo> pair in hosts)
    {
      if (now - pair.Value.LastSeen <= HostExpirationSeconds) continue;
      if (expired == null) expired = new List<string>();
      expired.Add(pair.Key);
    }
    if (expired == null) return;
    foreach (string key in expired) hosts.Remove(key);
    revision++;
  }

  void OnDestroy()
  {
    if (instance == this) instance = null;
    CloseSocket(ref discoverySocket);
    CloseSocket(ref broadcastSocket);
    CloseSocket(ref ipv6DiscoverySocket);
    CloseSocket(ref ipv6AdvertisementSocket);
  }

  static void CloseSocket(ref Socket socket)
  {
    if (socket == null) return;
    try { socket.Close(); }
    catch (SocketException) { }
    socket = null;
  }
}
#endif
