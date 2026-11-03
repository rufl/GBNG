using System.Net;
using System.Text;
using System.Text.Json;
using Godot;
using GBNG.Behaviors;

namespace GBNG.Networking;

public sealed record LegacyLanRoom(string Id, string Name, string Address, int Port, int PlayerCount, int Capacity);
public sealed record LegacyLanPlayer(long Id, int SlotNumber, string Name);

public sealed partial class LegacyLanSession : Node
{
    private const int DiscoveryPort = 47_821;
    private const int MaximumPlayers = 8;
    private const int PackageChunkBytes = 32 * 1024;
    private const int MaximumChunkCount = LegacyProjectPackage.MaximumPackageBytes / PackageChunkBytes + 1;
    private const string DiscoveryRequest = "GBNG-LAN-DISCOVER/1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<long, string> _playerNames = new();
    private readonly Dictionary<string, LegacyLanRoom> _rooms = new(StringComparer.Ordinal);
    private readonly HashSet<long> _projectReadyPeers = new();
    private readonly Dictionary<long, ReceivedPlayerInput> _inputsByPeer = new();
    private readonly Dictionary<long, long> _lastInputSequences = new();
    private readonly Queue<long> _packageTransferQueue = new();
    private ENetMultiplayerPeer? _enetPeer;
    private PacketPeerUdp? _discoveryListener;
    private PacketPeerUdp? _discoveryClient;
    private string _localPlayerName = "Player";
    private string _roomName = "Game Builder room";
    private string _roomId = string.Empty;
    private string? _hostProjectPath;
    private byte[]? _hostPackage;
    private byte[]? _incomingPackage;
    private bool[]? _receivedChunks;
    private int _incomingChunkCount;
    private int _receivedChunkCount;
    private string _incomingProjectName = string.Empty;
    private double _discoveryRemaining;
    private bool _isHost;
    private bool _hostPlayMode;
    private string _status = "Not connected.";

    public event Action? StateChanged;
    public event Action<LegacyLanRoom>? RoomDiscovered;
    public event Action<string>? ProjectReceived;
    public event Action<string>? SnapshotReceived;
    public event Action<bool>? HostPlayModeChanged;
    public event Action<string>? ActorStructureReceived;

    public string Status => _status;
    public IReadOnlyCollection<LegacyLanPlayer> Players { get; private set; } = Array.Empty<LegacyLanPlayer>();
    public IReadOnlyCollection<LegacyLanRoom> Rooms => _rooms.Values;
    public bool HasPeer => _enetPeer is not null;
    public bool IsHost => HasPeer && _isHost;
    public bool IsClient => HasPeer && !_isHost;
    public bool IsMultiplayer => HasPeer;
    public bool IsMaster => !HasPeer || _isHost;
    public string LocalPlayerName => _localPlayerName;
    public bool HostPlayMode => _hostPlayMode;
    public int LocalPlayerSlot
    {
        get
        {
            if (!HasPeer)
                return 1;
            long localPeerId = Multiplayer.GetUniqueId();
            return Players.FirstOrDefault(player => player.Id == localPeerId)?.SlotNumber ?? 1;
        }
    }

    public override void _Ready()
    {
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
        RefreshPlayers();
    }

    public override void _Process(double delta)
    {
        PollDiscovery(_discoveryListener, isHostListener: true);
        PollDiscovery(_discoveryClient, isHostListener: false);
        PumpProjectTransfer();
        if (_discoveryRemaining > 0d)
        {
            _discoveryRemaining -= delta;
            if (_discoveryRemaining <= 0d)
            {
                _discoveryRemaining = 0d;
                _discoveryClient = null;
                SetStatus(_rooms.Count == 0 ? "No LAN rooms found. Join by IP or retry discovery." : $"Found {_rooms.Count} LAN room(s).");
            }
        }
    }

    public Error Host(string projectPath, string roomName, int port, string playerName)
    {
        if (port is < 1 or > 65_535)
            return Fail("Port must be between 1 and 65535.");

        byte[] package;
        try
        {
            package = LegacyProjectPackage.Create(projectPath);
        }
        catch (Exception exception)
        {
            return Fail($"Cannot share this project: {exception.Message}");
        }

        Leave();
        _localPlayerName = NormalizePlayerName(playerName);
        _roomName = NormalizeRoomName(roomName);
        _roomId = Guid.NewGuid().ToString("N");
        _hostPort = port;
        _snapshotElapsed = 0d;
        _hostProjectPath = projectPath;
        _hostPackage = package;
        _playerNames[1] = _localPlayerName;
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateServer(port, MaximumPlayers - 1);
        if (error != Error.Ok)
        {
            Leave();
            return Fail($"Could not host on port {port}: {error}.");
        }

        _enetPeer = peer;
        _isHost = true;
        Multiplayer.MultiplayerPeer = peer;
        _discoveryListener = new PacketPeerUdp();
        Error discoveryError = _discoveryListener.Bind(DiscoveryPort);
        if (discoveryError != Error.Ok)
        {
            _discoveryListener = null;
            GD.PushWarning($"LAN room discovery could not bind UDP {DiscoveryPort}: {discoveryError}. Direct-IP joining remains available.");
        }
        RefreshPlayers();
        SetStatus(discoveryError == Error.Ok
            ? $"Hosting '{_roomName}' on port {port}; LAN discovery is active."
            : $"Hosting '{_roomName}' on port {port}; LAN discovery is unavailable, use direct IP.");
        return Error.Ok;
    }

    public Error Join(string address, int port, string playerName)
    {
        string normalizedAddress = address.Trim();
        if (normalizedAddress.Length == 0 || !IPAddress.TryParse(normalizedAddress, out _))
            return Fail("Enter a valid IPv4 or IPv6 address.");
        if (port is < 1 or > 65_535)
            return Fail("Port must be between 1 and 65535.");

        Leave();
        _localPlayerName = NormalizePlayerName(playerName);
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateClient(normalizedAddress, port);
        if (error != Error.Ok)
            return Fail($"Could not connect to {normalizedAddress}:{port}: {error}.");

        _enetPeer = peer;
        _isHost = false;
        Multiplayer.MultiplayerPeer = peer;
        SetStatus($"Connecting to {normalizedAddress}:{port}…");
        return Error.Ok;
    }

    public void StartDiscovery()
    {
        _rooms.Clear();
        _discoveryClient = new PacketPeerUdp();
        Error bindError = _discoveryClient.Bind(0);
        if (bindError != Error.Ok)
        {
            _discoveryClient = null;
            SetStatus($"LAN discovery could not open a UDP socket: {bindError}. Join by IP instead.");
            return;
        }
        _discoveryClient.SetBroadcastEnabled(true);
        _discoveryClient.SetDestAddress("255.255.255.255", DiscoveryPort);
        Error sendError = _discoveryClient.PutPacket(Encoding.UTF8.GetBytes(DiscoveryRequest));
        if (sendError != Error.Ok)
        {
            _discoveryClient = null;
            SetStatus($"LAN discovery request failed: {sendError}. Join by IP instead.");
            return;
        }
        _discoveryRemaining = 3.5d;
        SetStatus("Searching this LAN for rooms…");
        StateChanged?.Invoke();
    }

    public void Leave()
    {
        bool wasConnected = HasPeer;
        bool wasClient = IsClient;
        string? previousHostPath = _hostProjectPath;
        _discoveryClient = null;
        _discoveryRemaining = 0d;
        _discoveryListener = null;
        _incomingPackage = null;
        _receivedChunks = null;
        _incomingChunkCount = 0;
        _receivedChunkCount = 0;
        _hostPackage = null;
        _packageTransferQueue.Clear();
        _activeTransferPeer = 0;
        _nextPackageChunk = 0;
        _hostProjectPath = null;
        _playerNames.Clear();
        _inputsByPeer.Clear();
        _lastInputSequences.Clear();
        _localInputElapsed = 0d;
        _localInputSequence = 0;
        if (_enetPeer is not null)
        {
            _enetPeer.Close();
            _enetPeer = null;
            Multiplayer.MultiplayerPeer = null;
        }
        _isHost = false;
        _hostPlayMode = false;
        _hostPort = 0;
        _roomId = string.Empty;
        _snapshotElapsed = 0d;
        _projectReadyPeers.Clear();
        _actorStructureDirty = false;
        _lastActors = Array.Empty<GBNG.World.WorldActorNode>();
        if (previousHostPath is not null)
        {
            string generatedRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("user://network-host"));
            string hostDirectory = Path.GetDirectoryName(Path.GetFullPath(previousHostPath)) ?? string.Empty;
            if (hostDirectory.StartsWith(generatedRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Directory.Exists(hostDirectory))
                Directory.Delete(hostDirectory, recursive: true);
        }
        RefreshPlayers();
        if (wasConnected)
            HostPlayModeChanged?.Invoke(false);
        SetStatus("Not connected.");
    }

    public void Tick(double delta, bool playMode, IReadOnlyList<GBNG.World.WorldActorNode> actors)
    {
        if (!IsHost)
            return;
        _lastActors = actors;
        if (_actorStructureDirty && _projectReadyPeers.Count > 0)
        {
            _actorStructureDirty = false;
            foreach (long peerId in _projectReadyPeers.ToArray())
                SendActorStructure(peerId, actors);
        }
        if (!playMode || actors.Count == 0 || _projectReadyPeers.Count == 0)
            return;
        _snapshotElapsed += delta;
        if (_snapshotElapsed < 0.05d)
            return;
        _snapshotElapsed %= 0.05d;
        Rpc(nameof(ReceiveSnapshot), BuildSnapshot(actors));
    }

    public void NotifyProjectLoaded()
    {
        if (IsClient)
            RpcId(1, nameof(ClientProjectReady));
    }

    public void MarkActorStructureChanged() => _actorStructureDirty = true;

    private IReadOnlyList<GBNG.World.WorldActorNode> _lastActors = Array.Empty<GBNG.World.WorldActorNode>();
    private bool _actorStructureDirty;

    private double _snapshotElapsed;

    public bool TryGetPlayerInfo(out string json, out string localPlayerId)
    {
        if (!HasPeer)
        {
            json = JsonSerializer.Serialize(new { allPlayers = new[] { new { id = "local", slotNumber = 1, nickName = "Local Player" } }, localPlayerId = "local" });
            localPlayerId = "local";
            return true;
        }

        long localId = Multiplayer.GetUniqueId();
        LegacyLanPlayer[] players = Players.OrderBy(player => player.SlotNumber).ToArray();
        json = JsonSerializer.Serialize(new
        {
            allPlayers = players.Select(player => new { id = player.Id.ToString(), slotNumber = player.SlotNumber, nickName = player.Name }),
            localPlayerId = localId.ToString()
        });
        localPlayerId = localId.ToString();
        return true;
    }
    public bool ShouldSendLocalInput(double delta, bool playMode)
    {
        if (!IsClient || !playMode || !_hostPlayMode)
        {
            _localInputElapsed = 0d;
            return false;
        }
        _localInputElapsed += delta;
        if (_localInputElapsed < 0.05d)
            return false;
        _localInputElapsed %= 0.05d;
        return true;
    }

    public void SendLocalInput(
        Vector2 lookAxes,
        Vector2 mouseUiPosition,
        float mouseWheelRaw,
        bool pointerOverUi,
        Vector3 mouseRayOrigin,
        Vector3 mouseRayDirection,
        Vector3 cameraPosition,
        Quaternion cameraRotation)
    {
        if (!IsClient || !_hostPlayMode)
            return;
        bool mouseDown = Input.IsMouseButtonPressed(MouseButton.Left) && !pointerOverUi;
        bool secondaryDown = Input.IsMouseButtonPressed(MouseButton.Right) && !pointerOverUi;
        var heldKeys = new List<string>(LegacyInputKeys.ScriptableNames.Length);
        foreach (string keyName in LegacyInputKeys.ScriptableNames)
        {
            if (LegacyInputKeys.IsHeld(keyName, mouseDown, secondaryDown))
                heldKeys.Add(keyName);
        }

        var frame = new LegacyLanInputFrame(
            ++_localInputSequence,
            heldKeys.ToArray(),
            mouseDown,
            secondaryDown,
            Input.IsKeyPressed(Key.Shift),
            lookAxes.X, lookAxes.Y,
            mouseUiPosition.X, mouseUiPosition.Y,
            mouseWheelRaw,
            mouseRayOrigin.X, mouseRayOrigin.Y, mouseRayOrigin.Z,
            mouseRayDirection.X, mouseRayDirection.Y, mouseRayDirection.Z,
            cameraPosition.X, cameraPosition.Y, cameraPosition.Z,
            cameraRotation.X, cameraRotation.Y, cameraRotation.Z, cameraRotation.W);
        RpcId(1, nameof(ReceivePlayerInput), JsonSerializer.Serialize(frame));
    }

    public LegacyLanInputFrame? GetRemoteInputForSlot(int slotNumber)
    {
        if (!IsHost || slotNumber <= 1)
            return null;
        LegacyLanPlayer? player = Players.FirstOrDefault(candidate => candidate.SlotNumber == slotNumber);
        if (player is null || !_inputsByPeer.TryGetValue(player.Id, out ReceivedPlayerInput received))
            return null;
        return System.Diagnostics.Stopwatch.GetTimestamp() - received.Timestamp > System.Diagnostics.Stopwatch.Frequency / 2
            ? null
            : received.Frame;
    }

    private double _localInputElapsed;
    private long _localInputSequence;

    private void OnPeerConnected(long peerId)
    {
        if (!_isHost)
            return;
        _playerNames.TryAdd(peerId, $"Player {peerId}");
        RefreshPlayers();
    }

    private void OnPeerDisconnected(long peerId)
    {
        if (!_isHost)
            return;
        _playerNames.Remove(peerId);
        _inputsByPeer.Remove(peerId);
        _lastInputSequences.Remove(peerId);
        RefreshPlayers();
        SetStatus($"Player {peerId} left the room.");
        _projectReadyPeers.Remove(peerId);
        if (_activeTransferPeer == peerId)
        {
            _activeTransferPeer = 0;
            _nextPackageChunk = 0;
        }

    }
    private void OnConnectedToServer()
    {
        RpcId(1, nameof(RegisterPlayer), _localPlayerName);
        SetStatus("Connected; requesting the host's project copy…");
    }
    public void SetHostPlayMode(bool enabled)
    {
        if (!IsHost || _hostPlayMode == enabled)
            return;
        _hostPlayMode = enabled;
        if (_playerNames.Count > 1)
            Rpc(nameof(ReceivePlayMode), enabled);
    }

    private void OnConnectionFailed()
    {
        SetStatus("Connection failed. Check the address, port, and host firewall.");
        LeavePeerOnly();
    }

    private void OnServerDisconnected()
    {
        SetStatus("The host disconnected.");
        LeavePeerOnly();
    }

    private void LeavePeerOnly()
    {
        bool wasClient = IsClient;
        _isHost = false;
        _hostPlayMode = false;
        _playerNames.Clear();
        _inputsByPeer.Clear();
        _lastInputSequences.Clear();
        _localInputElapsed = 0d;
        _localInputSequence = 0;
        _incomingPackage = null;
        _receivedChunks = null;
        _incomingChunkCount = 0;
        _receivedChunkCount = 0;
        _projectReadyPeers.Clear();
        _packageTransferQueue.Clear();
        _activeTransferPeer = 0;
        _nextPackageChunk = 0;
        RefreshPlayers();
        if (wasClient)
            HostPlayModeChanged?.Invoke(false);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RegisterPlayer(string playerName)
    {
        if (!_isHost || !Multiplayer.IsServer())
            return;
        long senderId = Multiplayer.GetRemoteSenderId();
        if (senderId <= 1 || !_playerNames.ContainsKey(senderId))
            return;
        _playerNames[senderId] = NormalizePlayerName(playerName);
        RefreshPlayers();
        SendRoster();
        SendProjectPackage(senderId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientProjectReady()
    {
        if (!IsHost || !Multiplayer.IsServer())
            return;
        long senderId = Multiplayer.GetRemoteSenderId();
        if (!_playerNames.ContainsKey(senderId))
            return;
        _projectReadyPeers.Add(senderId);
        SendActorStructure(senderId, _lastActors);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRoster(string playersJson)
    {
        try
        {
            LegacyLanPlayer[]? players = JsonSerializer.Deserialize<LegacyLanPlayer[]>(playersJson, JsonOptions);
            Players = players ?? Array.Empty<LegacyLanPlayer>();
            StateChanged?.Invoke();
        }
        catch (JsonException exception)
        {
            GD.PushWarning($"Ignored invalid LAN player roster: {exception.Message}");
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginProjectTransfer(string projectName, int packageSize, int chunkCount)
    {
        if (!IsClient || Path.GetFileName(projectName) != projectName || !projectName.EndsWith(".voos", StringComparison.OrdinalIgnoreCase)
            || packageSize is < 1 or > LegacyProjectPackage.MaximumPackageBytes
            || chunkCount is < 1 or > MaximumChunkCount
            || chunkCount != (packageSize + PackageChunkBytes - 1) / PackageChunkBytes)
        {
            SetStatus("The host sent an invalid project package header.");
            return;
        }
        _incomingProjectName = projectName;
        _incomingPackage = new byte[packageSize];
        _receivedChunks = new bool[chunkCount];
        _incomingChunkCount = chunkCount;
        _receivedChunkCount = 0;
        SetStatus($"Receiving {projectName} from the host…");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveProjectChunk(int index, byte[] bytes)
    {
        if (!IsClient || _incomingPackage is null || _receivedChunks is null || index < 0 || index >= _incomingChunkCount)
            return;
        int offset = checked(index * PackageChunkBytes);
        int expectedLength = Math.Min(PackageChunkBytes, _incomingPackage.Length - offset);
        if (bytes.Length != expectedLength || _receivedChunks[index])
            return;
        Buffer.BlockCopy(bytes, 0, _incomingPackage, offset, bytes.Length);
        _receivedChunks[index] = true;
        _receivedChunkCount++;
        if (_receivedChunkCount % 64 == 0)
            SetStatus($"Receiving { _incomingProjectName}: {_receivedChunkCount}/{_incomingChunkCount} chunks…");
        if (_receivedChunkCount != _incomingChunkCount)
            return;

        try
        {
            string projectPath = LegacyProjectPackage.Extract(_incomingPackage);
            _incomingPackage = null;
            _receivedChunks = null;
            SetStatus($"Received {_incomingProjectName}; opening the shared project copy.");
            ProjectReceived?.Invoke(projectPath);
        }
        catch (Exception exception)
        {
            _incomingPackage = null;
            _receivedChunks = null;
            SetStatus($"Could not unpack the host project: {exception.Message}");
        }
    }
    [Rpc(
        MultiplayerApi.RpcMode.AnyPeer,
        TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered,
        TransferChannel = 1)]
    private void ReceivePlayerInput(string inputJson)
    {
        if (!IsHost || !Multiplayer.IsServer() || inputJson.Length > 8_192)
            return;
        long senderId = Multiplayer.GetRemoteSenderId();
        if (senderId <= 1 || !_playerNames.ContainsKey(senderId))
            return;
        try
        {
            LegacyLanInputFrame? frame = JsonSerializer.Deserialize<LegacyLanInputFrame>(inputJson, JsonOptions);
            if (frame is null || frame.KeysHeld is null || frame.KeysHeld.Length > LegacyInputKeys.ScriptableNames.Length
                || frame.Sequence <= _lastInputSequences.GetValueOrDefault(senderId)
                || !IsFinite(frame))
                return;
            var heldKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string keyName in frame.KeysHeld)
            {
                if (keyName is not null && LegacyInputKeys.ScriptableNames.Contains(keyName, StringComparer.Ordinal))
                    heldKeys.Add(keyName);
            }
            _lastInputSequences[senderId] = frame.Sequence;
            _inputsByPeer[senderId] = new ReceivedPlayerInput(
                frame with { KeysHeld = heldKeys.ToArray() },
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
        catch (JsonException)
        {
            // Ignore malformed input frames from peers.
        }
    }

    private static bool IsFinite(LegacyLanInputFrame frame)
        => float.IsFinite(frame.LookX) && float.IsFinite(frame.LookY)
            && float.IsFinite(frame.MouseUiX) && float.IsFinite(frame.MouseUiY) && float.IsFinite(frame.MouseWheelRaw)
            && float.IsFinite(frame.RayOriginX) && float.IsFinite(frame.RayOriginY) && float.IsFinite(frame.RayOriginZ)
            && float.IsFinite(frame.RayDirectionX) && float.IsFinite(frame.RayDirectionY) && float.IsFinite(frame.RayDirectionZ)
            && float.IsFinite(frame.CameraPositionX) && float.IsFinite(frame.CameraPositionY) && float.IsFinite(frame.CameraPositionZ)
            && float.IsFinite(frame.CameraRotationX) && float.IsFinite(frame.CameraRotationY)
            && float.IsFinite(frame.CameraRotationZ) && float.IsFinite(frame.CameraRotationW);

    private readonly record struct ReceivedPlayerInput(LegacyLanInputFrame Frame, long Timestamp);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveTransferError(string message)
    {
        SetStatus($"Host could not share the project: {message}");
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceivePlayMode(bool enabled)
    {
        _hostPlayMode = enabled;
        HostPlayModeChanged?.Invoke(enabled);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = 1)]
    private void ReceiveSnapshot(string snapshotJson)
    {
        if (IsClient)
            SnapshotReceived?.Invoke(snapshotJson);
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveActorStructure(string actorStatesJson)
    {
        if (IsClient)
            ActorStructureReceived?.Invoke(actorStatesJson);
    }

    private void SendRoster()
    {
        RefreshPlayers();
        string json = JsonSerializer.Serialize(Players.OrderBy(player => player.SlotNumber));
        Rpc(nameof(ReceiveRoster), json);
    }

    private void SendProjectPackage(long peerId)
    {
        if (_hostPackage is null || _hostProjectPath is null)
        {
            RpcId(peerId, nameof(ReceiveTransferError), "The host project is no longer available.");
            return;
        }

        string projectName = Path.GetFileName(_hostProjectPath);
        int chunkCount = (_hostPackage.Length + PackageChunkBytes - 1) / PackageChunkBytes;
        RpcId(peerId, nameof(BeginProjectTransfer), projectName, _hostPackage.Length, chunkCount);
        _packageTransferQueue.Enqueue(peerId);
    }

    private long _activeTransferPeer;
    private int _nextPackageChunk;

    private void PumpProjectTransfer()
    {
        if (!IsHost || _hostPackage is null)
            return;
        if (_activeTransferPeer == 0)
        {
            while (_packageTransferQueue.Count > 0)
            {
                long nextPeer = _packageTransferQueue.Dequeue();
                if (_playerNames.ContainsKey(nextPeer))
                {
                    _activeTransferPeer = nextPeer;
                    _nextPackageChunk = 0;
                    break;
                }
            }
        }
        if (_activeTransferPeer == 0)
            return;

        long peerId = _activeTransferPeer;
        int chunkCount = (_hostPackage.Length + PackageChunkBytes - 1) / PackageChunkBytes;
        for (int sent = 0; sent < 16 && _nextPackageChunk < chunkCount; sent++, _nextPackageChunk++)
        {
            int offset = _nextPackageChunk * PackageChunkBytes;
            int length = Math.Min(PackageChunkBytes, _hostPackage.Length - offset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(_hostPackage, offset, chunk, 0, length);
            RpcId(peerId, nameof(ReceiveProjectChunk), _nextPackageChunk, chunk);
        }
        if (_nextPackageChunk < chunkCount)
            return;

        RpcId(peerId, nameof(ReceivePlayMode), _hostPlayMode);
        SetStatus($"Shared {_hostPackage.Length / (1024 * 1024)} MiB with {_playerNames.GetValueOrDefault(peerId, $"player {peerId}")}.");
        _activeTransferPeer = 0;
        _nextPackageChunk = 0;
    }
    private const int MaximumActorStructureBytes = 16 * 1024 * 1024;

    private void SendActorStructure(long peerId, IReadOnlyList<GBNG.World.WorldActorNode> actors)
    {
        string[] actorData = actors.Select(actor => actor.State.Data.ToJsonString()).ToArray();
        string json = JsonSerializer.Serialize(actorData);
        if (Encoding.UTF8.GetByteCount(json) > MaximumActorStructureBytes)
        {
            RpcId(peerId, nameof(ReceiveTransferError), "Live actor state exceeds the 16 MiB synchronization limit.");
            return;
        }
        RpcId(peerId, nameof(ReceiveActorStructure), json);
    }

    private void PollDiscovery(PacketPeerUdp? socket, bool isHostListener)
    {
        if (socket is null)
            return;
        while (socket.GetAvailablePacketCount() > 0)
        {
            byte[] packet = socket.GetPacket();
            if (packet.Length == 0 || packet.Length > 4096)
                continue;
            string text = Encoding.UTF8.GetString(packet);
            if (isHostListener)
            {
                if (text == DiscoveryRequest)
                    SendRoomAnnouncement(socket, socket.GetPacketIP(), socket.GetPacketPort());
                continue;
            }

            try
            {
                LegacyLanRoom? room = JsonSerializer.Deserialize<LegacyLanRoom>(text, JsonOptions);
                if (room is null || room.Port is < 1 or > 65_535 || room.Id.Length == 0)
                    continue;
                string senderAddress = socket.GetPacketIP();
                var discovered = room with { Address = senderAddress };
                if (_rooms.TryGetValue(discovered.Id, out LegacyLanRoom? previous) && previous == discovered)
                    continue;
                _rooms[discovered.Id] = discovered;
                RoomDiscovered?.Invoke(discovered);
                StateChanged?.Invoke();
            }
            catch (JsonException)
            {
                // Ignore UDP broadcast traffic that is not a Game Builder room response.
            }
        }
    }

    private void SendRoomAnnouncement(PacketPeerUdp socket, string address, int port)
    {
        if (!_isHost || address.Length == 0 || port <= 0)
            return;
        var room = new LegacyLanRoom(_roomId, _roomName, string.Empty, GetHostedPort(), _playerNames.Count, MaximumPlayers);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(room);
        socket.SetDestAddress(address, port);
        socket.PutPacket(payload);
    }

    private int GetHostedPort()
    {
        if (_hostProjectPath is null)
            return 0;
        return _hostPort;
    }

    private int _hostPort;

    private void RefreshPlayers()
    {
        if (!_isHost && HasPeer && Players.Count > 0)
        {
            StateChanged?.Invoke();
            return;
        }
        if (!_isHost && !HasPeer)
        {
            Players = Array.Empty<LegacyLanPlayer>();
            StateChanged?.Invoke();
            return;
        }

        Players = _playerNames
            .OrderBy(pair => pair.Key == 1 ? 0 : 1)
            .ThenBy(pair => pair.Key)
            .Select((pair, index) => new LegacyLanPlayer(pair.Key, index + 1, pair.Value))
            .ToArray();
        StateChanged?.Invoke();
    }

    private static string BuildSnapshot(IReadOnlyList<GBNG.World.WorldActorNode> actors)
    {
        var states = actors.Select(actor =>
        {
            actor.SaveTransform();
            Quaternion rotation = actor.GlobalBasis.GetRotationQuaternion();
            Vector3 scale = actor.GlobalTransform.Basis.Scale;
            Vector3 renderableOffset = actor.State.ReadLegacyVector("renderableOffset", Vector3.Zero);
            Quaternion renderableRotation = actor.State.ReadLegacyQuaternion("renderableRotation");
            Color tint = actor.State.Tint;
            return new ActorSnapshot(
                actor.LegacyName,
                actor.GlobalPosition.X, actor.GlobalPosition.Y, actor.GlobalPosition.Z,
                rotation.X, rotation.Y, rotation.Z, rotation.W,
                scale.X, scale.Y, scale.Z,
                actor.LinearVelocity.X, actor.LinearVelocity.Y, actor.LinearVelocity.Z,
                actor.AngularVelocity.X, actor.AngularVelocity.Y, actor.AngularVelocity.Z,
                tint.R, tint.G, tint.B, tint.A,
                renderableOffset.X, renderableOffset.Y, renderableOffset.Z,
                renderableRotation.X, renderableRotation.Y, renderableRotation.Z, renderableRotation.W,
                actor.State.LoopingAnimation);
        }).ToArray();
        return JsonSerializer.Serialize(states);
    }

    private static string NormalizePlayerName(string playerName)
    {
        string normalized = new(playerName.Where(character => !char.IsControl(character)).Take(32).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "Player" : normalized.Trim();
    }

    private static string NormalizeRoomName(string roomName)
    {
        string normalized = new(roomName.Where(character => !char.IsControl(character)).Take(48).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "Game Builder room" : normalized.Trim();
    }

    private Error Fail(string message)
    {
        SetStatus(message);
        return Error.Failed;
    }

    private void SetStatus(string status)
    {
        _status = status;
        StateChanged?.Invoke();
    }

    public override void _ExitTree()
    {
        Leave();
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        Multiplayer.ConnectedToServer -= OnConnectedToServer;
        Multiplayer.ConnectionFailed -= OnConnectionFailed;
        Multiplayer.ServerDisconnected -= OnServerDisconnected;
    }

    private sealed record ActorSnapshot(
        string Name,
        float X, float Y, float Z,
        float RotationX, float RotationY, float RotationZ, float RotationW,
        float ScaleX, float ScaleY, float ScaleZ,
        float VelocityX, float VelocityY, float VelocityZ,
        float AngularX, float AngularY, float AngularZ,
        float TintR, float TintG, float TintB, float TintA,
        float OffsetX, float OffsetY, float OffsetZ,
        float RenderRotationX, float RenderRotationY, float RenderRotationZ, float RenderRotationW,
        string Animation);
}
