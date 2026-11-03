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
    private const int ActorStructureChunkCharacters = 4 * 1024;
    private const int ActorStructureChunksPerFrame = 16;
    private const int MaximumActorStructureBytes = 16 * 1024 * 1024;
    private const int MaximumActorStructureChunkCount = (MaximumActorStructureBytes + ActorStructureChunkCharacters - 1) / ActorStructureChunkCharacters;
    private const int DocumentPatchChunkCharacters = 4 * 1024;
    private const int DocumentPatchChunksPerFrame = 16;
    private const int MaximumDocumentPatchBytes = 16 * 1024 * 1024;
    private const int MaximumDocumentPatchChunkCount = (MaximumDocumentPatchBytes + DocumentPatchChunkCharacters - 1) / DocumentPatchChunkCharacters;
    private const int AssetBundleChunksPerFrame = 16;
    private const int MaximumAssetBundleBytes = LegacyProjectPackage.MaximumPackageBytes;
    private const int MaximumAssetBundleChunkCount = (MaximumAssetBundleBytes + PackageChunkBytes - 1) / PackageChunkBytes;
    private const string DiscoveryRequest = "GBNG-LAN-DISCOVER/1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<long, string> _playerNames = new();
    private readonly Dictionary<string, LegacyLanRoom> _rooms = new(StringComparer.Ordinal);
    private readonly HashSet<long> _projectReadyPeers = new();
    private readonly Dictionary<long, ReceivedPlayerInput> _inputsByPeer = new();
    private readonly Dictionary<long, long> _lastInputSequences = new();
    private readonly Queue<long> _packageTransferQueue = new();
    private readonly Dictionary<long, ActorStructureTransfer> _actorStructureTransfers = new();
    private readonly Dictionary<long, OutgoingDocumentPatch> _outgoingDocumentPatches = new();
    private readonly Dictionary<long, IncomingDocumentPatch> _incomingClientDocumentPatches = new();
    private IncomingDocumentPatch? _incomingHostDocumentPatch;
    private readonly Dictionary<long, OutgoingAssetBundle> _outgoingAssetBundles = new();
    private readonly Dictionary<long, IncomingAssetBundle> _incomingClientAssetBundles = new();
    private readonly HashSet<long> _pendingAssetBundleTransferIds = new();
    private IncomingAssetBundle? _incomingHostAssetBundle;
    private long _nextAssetBundleTransferId;
    private long _nextDocumentPatchTransferId;
    private ENetMultiplayerPeer? _enetPeer;
    private PacketPeerUdp? _discoveryListener;
    private PacketPeerUdp? _discoveryClient;
    private string _localPlayerName = "Player";
    private string _roomName = "Game Builder room";
    private string _roomId = string.Empty;
    private string? _hostProjectPath;
    private byte[]? _activeTransferPackage;
    private byte[]? _incomingPackage;
    private bool[]? _receivedChunks;
    private char[]? _incomingActorStructure;
    private bool[]? _receivedActorStructureChunks;
    private int _receivedActorStructureChunkCount;
    private int _incomingChunkCount;
    private int _receivedChunkCount;
    private string _incomingProjectName = string.Empty;
    public event Action<long, long, string>? ClientAssetBundleReceived;
    public event Action<long, long, string>? AssetBundleReceived;
    public event Action<string>? AssetBundleRejected;
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
    public event Action<long, string>? ClientDocumentPatchReceived;
    public event Action<string>? DocumentPatchReceived;
    public event Action<long>? ClientProjectReadyReceived;
    public event Action<string, bool>? HostProjectCopyEnding;
    public event Action<string>? DocumentPatchRejected;

    public string Status => _status;
    public IReadOnlyCollection<LegacyLanPlayer> Players { get; private set; } = Array.Empty<LegacyLanPlayer>();
    public IReadOnlyCollection<LegacyLanRoom> Rooms => _rooms.Values;
    public bool HasPeer => _enetPeer is not null || _webRtcPeer is not null;
    public bool IsHost => HasPeer && _isHost;
    public bool IsClient => HasPeer && !_isHost;
    public bool IsMultiplayer => HasPeer;
    public bool IsMaster => !HasPeer || _isHost;
    public string LocalPlayerName => _localPlayerName;
    public long LocalPeerId => HasPeer ? Multiplayer.GetUniqueId() : 1;
    public bool HasPendingAssetSync
        => IsClient
            ? _pendingAssetBundleTransferIds.Count > 0 || _incomingHostAssetBundle is not null
            : IsHost && _outgoingAssetBundles.Values.Any(transfer => transfer.Current is not null || transfer.Pending.Count > 0);
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
        PollInternetTransport(delta);
        PollDiscovery(_discoveryListener, isHostListener: true);
        PollDiscovery(_discoveryClient, isHostListener: false);
        PumpProjectTransfer();
        PumpActorStructureTransfers();
        PumpDocumentPatchTransfers();
        PumpAssetBundleTransfers();
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

        try
        {
            _ = LegacyProjectPackage.Create(projectPath);
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
        bool wasHost = IsHost;
        bool wasConnected = HasPeer;
        bool wasClient = IsClient;
        string? previousHostPath = _hostProjectPath;
        _discoveryClient = null;
        _discoveryRemaining = 0d;
        _discoveryListener = null;
        LeaveInternetTransport();
        _incomingPackage = null;
        _receivedChunks = null;
        _incomingChunkCount = 0;
        _receivedChunkCount = 0;
        _incomingActorStructure = null;
        _receivedActorStructureChunks = null;
        _receivedActorStructureChunkCount = 0;
        _actorStructureTransfers.Clear();
        _outgoingDocumentPatches.Clear();
        _incomingClientDocumentPatches.Clear();
        _incomingHostDocumentPatch = null;
        _outgoingAssetBundles.Clear();
        _incomingClientAssetBundles.Clear();
        _pendingAssetBundleTransferIds.Clear();
        _incomingHostAssetBundle = null;
        _activeTransferPackage = null;
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
        }
        if (wasConnected)
            Multiplayer.MultiplayerPeer = null;
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
            HostProjectCopyEnding?.Invoke(previousHostPath, wasHost);
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
    public void SubmitDocumentPatch(string json)
    {
        if (!IsClient || !IsValidDocumentPatchPayload(json))
            return;
        QueueDocumentPatch(1, json);
    }

    public void BroadcastDocumentPatch(string json)
    {
        if (!IsHost || !IsValidDocumentPatchPayload(json))
            return;
        foreach (long peerId in _projectReadyPeers)
            QueueDocumentPatch(peerId, json);
    }

    public void RejectDocumentPatch(long peerId, string message)
    {
        if (IsHost && _playerNames.ContainsKey(peerId))
            RpcId(peerId, nameof(ReceiveDocumentPatchRejected), message);
        DocumentPatchRejected?.Invoke(message);
    }

    private static bool IsValidDocumentPatchPayload(string json)
        => json.Length >= 2 && Encoding.UTF8.GetByteCount(json) <= MaximumDocumentPatchBytes;

    private void QueueDocumentPatch(long peerId, string json)
    {
        if (!_outgoingDocumentPatches.TryGetValue(peerId, out OutgoingDocumentPatch? transfer))
        {
            transfer = new OutgoingDocumentPatch();
            _outgoingDocumentPatches.Add(peerId, transfer);
        }
        transfer.Pending.Enqueue(json);
    }
    public void UpdateActorSnapshot(IReadOnlyList<GBNG.World.WorldActorNode> actors)
    {
        if (IsHost)
            _lastActors = actors;
    }

    public bool SendDocumentSnapshot(long peerId, string json)
    {
        if (!IsHost || !_projectReadyPeers.Contains(peerId) || !IsValidDocumentPatchPayload(json))
            return false;
        QueueDocumentPatch(peerId, json);
        return true;
    }

    public void SendAssetBundleToPeer(string sourceProjectPath, long peerId)
    {
        if (!IsHost || !_projectReadyPeers.Contains(peerId))
            return;
        string sidecarDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(sourceProjectPath)) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(sourceProjectPath)}.assets");
        if (!File.Exists(Path.Combine(sidecarDirectory, "manifest.json")))
            return;
        try
        {
            byte[] package = LegacyProjectPackage.CreateAssetBundle(sourceProjectPath);
            QueueAssetBundle(peerId, new AssetBundlePayload(package, 1, 0));
        }
        catch (Exception exception)
        {
            string message = $"Could not sync project assets to the joining player: {exception.Message}";
            RpcId(peerId, nameof(ReceiveAssetBundleRejected), 0L, message);
            AssetBundleRejected?.Invoke(message);
        }
    }

    public bool SubmitAssetBundle(string sourceProjectPath, out long transferId)
    {
        transferId = 0;
        if (!IsClient)
            return false;
        try
        {
            long uploadId = NextAssetBundleTransferId();
            byte[] package = LegacyProjectPackage.CreateAssetBundle(sourceProjectPath);
            long originPeerId = Multiplayer.GetUniqueId();
            _pendingAssetBundleTransferIds.Add(uploadId);
            QueueAssetBundle(1, new AssetBundlePayload(package, originPeerId, uploadId));
            transferId = uploadId;
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            AssetBundleRejected?.Invoke($"Could not share the imported model: {exception.Message}");
            return false;
        }
    }

    public void BroadcastAssetBundle(string sourceProjectPath, long originPeerId, long originTransferId)
    {
        if (!IsHost || _projectReadyPeers.Count == 0)
            return;
        try
        {
            byte[] package = LegacyProjectPackage.CreateAssetBundle(sourceProjectPath);
            var payload = new AssetBundlePayload(package, originPeerId, originTransferId);
            foreach (long peerId in _projectReadyPeers)
                QueueAssetBundle(peerId, payload);
        }
        catch (Exception exception)
        {
            string message = $"Could not share the project assets: {exception.Message}";
            if (originPeerId > 1)
                RejectAssetBundle(originPeerId, originTransferId, message);
            else
                AssetBundleRejected?.Invoke(message);
        }
    }

    public void RejectAssetBundle(long peerId, long transferId, string message)
    {
        if (IsHost && _playerNames.ContainsKey(peerId))
            RpcId(peerId, nameof(ReceiveAssetBundleRejected), transferId, message);
        AssetBundleRejected?.Invoke(message);
    }

    private long NextAssetBundleTransferId()
    {
        long transferId = ++_nextAssetBundleTransferId;
        if (transferId > 0)
            return transferId;
        _nextAssetBundleTransferId = 1;
        return 1;
    }

    private void QueueAssetBundle(long peerId, AssetBundlePayload payload)
    {
        if (!_outgoingAssetBundles.TryGetValue(peerId, out OutgoingAssetBundle? transfer))
        {
            transfer = new OutgoingAssetBundle();
            _outgoingAssetBundles.Add(peerId, transfer);
        }
        transfer.Pending.Enqueue(payload);
    }

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
        if (peerId <= int.MaxValue)
            RemoveWebRtcPeer((int)peerId);
        if (!_isHost)
            return;
        _playerNames.Remove(peerId);
        _inputsByPeer.Remove(peerId);
        _lastInputSequences.Remove(peerId);
        RefreshPlayers();
        SetStatus($"Player {peerId} left the room.");
        _projectReadyPeers.Remove(peerId);
        _actorStructureTransfers.Remove(peerId);
        _outgoingDocumentPatches.Remove(peerId);
        _incomingClientDocumentPatches.Remove(peerId);
        _outgoingAssetBundles.Remove(peerId);
        _incomingClientAssetBundles.Remove(peerId);
        if (_activeTransferPeer == peerId)
        {
            _activeTransferPeer = 0;
            _activeTransferPackage = null;
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
        LeaveInternetTransport();
        _enetPeer?.Close();
        _enetPeer = null;
        Multiplayer.MultiplayerPeer = null;
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
        _incomingActorStructure = null;
        _receivedActorStructureChunks = null;
        _receivedActorStructureChunkCount = 0;
        _outgoingDocumentPatches.Clear();
        _incomingClientDocumentPatches.Clear();
        _incomingHostDocumentPatch = null;
        _outgoingAssetBundles.Clear();
        _incomingClientAssetBundles.Clear();
        _pendingAssetBundleTransferIds.Clear();
        _incomingHostAssetBundle = null;
        _actorStructureTransfers.Clear();
        _projectReadyPeers.Clear();
        _packageTransferQueue.Clear();
        _activeTransferPeer = 0;
        _activeTransferPackage = null;
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
        ClientProjectReadyReceived?.Invoke(senderId);
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
    private sealed class ActorStructureTransfer(string payload, int chunkCount)
    {
        public string Payload { get; set; } = payload;
        public int ChunkCount { get; } = chunkCount;
        public int NextChunk { get; set; }
    }
    private sealed class OutgoingDocumentPatch
    {
        public Queue<string> Pending { get; } = new();
        public string? Payload { get; set; }
        public long TransferId { get; set; }
        public int ChunkCount { get; set; }
        public int NextChunk { get; set; }
    }

    private sealed class IncomingDocumentPatch(long transferId, int characterCount, int chunkCount)
    {
        public long TransferId { get; } = transferId;
        public char[] Payload { get; } = new char[characterCount];
        public bool[] ReceivedChunks { get; } = new bool[chunkCount];
        public int ReceivedChunkCount { get; set; }
    }

    private sealed record AssetBundlePayload(byte[] Bytes, long OriginPeerId, long OriginTransferId);

    private sealed class OutgoingAssetBundle
    {
        public Queue<AssetBundlePayload> Pending { get; } = new();
        public AssetBundlePayload? Current { get; set; }
        public long TransferId { get; set; }
        public int NextChunk { get; set; }
    }

    private sealed class IncomingAssetBundle(
        long transferId,
        long originPeerId,
        long originTransferId,
        int packageSize,
        int chunkCount)
    {
        public long TransferId { get; } = transferId;
        public long OriginPeerId { get; } = originPeerId;
        public long OriginTransferId { get; } = originTransferId;
        public byte[] Package { get; } = new byte[packageSize];
        public bool[] ReceivedChunks { get; } = new bool[chunkCount];
        public int ReceivedChunkCount { get; set; }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveTransferError(string message)
    {
        SetStatus($"The host could not finish synchronization: {message}");
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
    private void BeginActorStructure(int characterCount, int chunkCount)
    {
        if (!IsClient
            || characterCount is < 2 or > MaximumActorStructureBytes
            || chunkCount is < 1 or > MaximumActorStructureChunkCount
            || chunkCount != (characterCount + ActorStructureChunkCharacters - 1) / ActorStructureChunkCharacters)
        {
            _incomingActorStructure = null;
            _receivedActorStructureChunks = null;
            _receivedActorStructureChunkCount = 0;
            return;
        }

        _incomingActorStructure = new char[characterCount];
        _receivedActorStructureChunks = new bool[chunkCount];
        _receivedActorStructureChunkCount = 0;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveActorStructureChunk(int index, string chunk)
    {
        if (!IsClient
            || _incomingActorStructure is null
            || _receivedActorStructureChunks is null
            || index < 0
            || index >= _receivedActorStructureChunks.Length)
            return;

        int offset = index * ActorStructureChunkCharacters;
        int expectedLength = Math.Min(ActorStructureChunkCharacters, _incomingActorStructure.Length - offset);
        if (chunk.Length != expectedLength || _receivedActorStructureChunks[index])
            return;

        chunk.CopyTo(0, _incomingActorStructure, offset, chunk.Length);
        _receivedActorStructureChunks[index] = true;
        _receivedActorStructureChunkCount++;
        if (_receivedActorStructureChunkCount != _receivedActorStructureChunks.Length)
            return;

        string actorStatesJson = new(_incomingActorStructure);
        _incomingActorStructure = null;
        _receivedActorStructureChunks = null;
        _receivedActorStructureChunkCount = 0;
        if (Encoding.UTF8.GetByteCount(actorStatesJson) > MaximumActorStructureBytes)
        {
            GD.PushWarning("Ignored network actor structure exceeding the 16 MiB synchronization limit.");
            return;
        }
        ActorStructureReceived?.Invoke(actorStatesJson);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginClientDocumentPatch(long transferId, int characterCount, int chunkCount)
    {
        long senderId = Multiplayer.GetRemoteSenderId();
        if (!IsHost || !Multiplayer.IsServer() || senderId <= 1 || !_projectReadyPeers.Contains(senderId))
            return;
        if (!TryCreateIncomingDocumentPatch(transferId, characterCount, chunkCount, out IncomingDocumentPatch incoming))
        {
            RejectDocumentPatch(senderId, "The host rejected an invalid collaborative edit transfer.");
            return;
        }
        if (_incomingClientDocumentPatches.ContainsKey(senderId))
        {
            RejectDocumentPatch(senderId, "A collaborative edit transfer is already in progress.");
            return;
        }
        _incomingClientDocumentPatches.Add(senderId, incoming);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveClientDocumentPatchChunk(long transferId, int index, string chunk)
    {
        long senderId = Multiplayer.GetRemoteSenderId();
        if (!IsHost || !Multiplayer.IsServer() || senderId <= 1 || !_projectReadyPeers.Contains(senderId)
            || !_incomingClientDocumentPatches.TryGetValue(senderId, out IncomingDocumentPatch? incoming)
            || incoming.TransferId != transferId)
            return;
        if (!TryReceiveDocumentPatchChunk(incoming, index, chunk, out string? payload))
        {
            _incomingClientDocumentPatches.Remove(senderId);
            RejectDocumentPatch(senderId, "The host rejected an incomplete or malformed collaborative edit.");
            return;
        }
        if (payload is not null)
        {
            _incomingClientDocumentPatches.Remove(senderId);
            ClientDocumentPatchReceived?.Invoke(senderId, payload);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginHostDocumentPatch(long transferId, int characterCount, int chunkCount)
    {
        if (!IsClient || !TryCreateIncomingDocumentPatch(transferId, characterCount, chunkCount, out IncomingDocumentPatch incoming))
        {
            _incomingHostDocumentPatch = null;
            DocumentPatchRejected?.Invoke("The host sent an invalid collaborative edit transfer.");
            return;
        }
        if (_incomingHostDocumentPatch is not null)
        {
            _incomingHostDocumentPatch = null;
            DocumentPatchRejected?.Invoke("The host sent overlapping collaborative edit transfers.");
            return;
        }
        _incomingHostDocumentPatch = incoming;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHostDocumentPatchChunk(long transferId, int index, string chunk)
    {
        if (!IsClient || _incomingHostDocumentPatch is not IncomingDocumentPatch incoming || incoming.TransferId != transferId)
            return;
        if (!TryReceiveDocumentPatchChunk(incoming, index, chunk, out string? payload))
        {
            _incomingHostDocumentPatch = null;
            DocumentPatchRejected?.Invoke("The host sent an incomplete or malformed collaborative edit.");
            return;
        }
        if (payload is not null)
        {
            _incomingHostDocumentPatch = null;
            DocumentPatchReceived?.Invoke(payload);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveDocumentPatchRejected(string message)
    {
        DocumentPatchRejected?.Invoke(message);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginClientAssetBundle(long transferId, long originTransferId, int packageSize, int chunkCount)
    {
        long senderId = Multiplayer.GetRemoteSenderId();
        if (!IsHost || !Multiplayer.IsServer() || senderId <= 1 || !_projectReadyPeers.Contains(senderId))
            return;
        if (!TryCreateIncomingAssetBundle(transferId, senderId, originTransferId, packageSize, chunkCount, out IncomingAssetBundle incoming))
        {
            RejectAssetBundle(senderId, originTransferId, "The host rejected an invalid project asset transfer.");
            return;
        }
        if (_incomingClientAssetBundles.ContainsKey(senderId))
        {
            RejectAssetBundle(senderId, originTransferId, "A project asset transfer is already in progress.");
            return;
        }
        _incomingClientAssetBundles.Add(senderId, incoming);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveClientAssetBundleChunk(long transferId, int index, byte[] chunk)
    {
        long senderId = Multiplayer.GetRemoteSenderId();
        if (!IsHost || !Multiplayer.IsServer() || senderId <= 1 || !_projectReadyPeers.Contains(senderId)
            || !_incomingClientAssetBundles.TryGetValue(senderId, out IncomingAssetBundle? incoming)
            || incoming.TransferId != transferId)
            return;
        if (!TryReceiveAssetBundleChunk(incoming, index, chunk, out byte[]? package))
        {
            _incomingClientAssetBundles.Remove(senderId);
            RejectAssetBundle(senderId, incoming.OriginTransferId, "The host rejected an incomplete or malformed project asset transfer.");
            return;
        }
        if (package is null)
            return;
        _incomingClientAssetBundles.Remove(senderId);
        try
        {
            string bundleProjectPath = LegacyProjectPackage.ExtractAssetBundle(package);
            ClientAssetBundleReceived?.Invoke(senderId, incoming.OriginTransferId, bundleProjectPath);
        }
        catch (Exception exception)
        {
            RejectAssetBundle(senderId, incoming.OriginTransferId, $"The host could not unpack the project assets: {exception.Message}");
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginHostAssetBundle(
        long transferId,
        long originPeerId,
        long originTransferId,
        int packageSize,
        int chunkCount)
    {
        if (!IsClient
            || !TryCreateIncomingAssetBundle(transferId, originPeerId, originTransferId, packageSize, chunkCount, out IncomingAssetBundle incoming))
        {
            _incomingHostAssetBundle = null;
            AssetBundleRejected?.Invoke("The host sent an invalid project asset transfer.");
            return;
        }
        if (_incomingHostAssetBundle is not null)
        {
            AssetBundleRejected?.Invoke("The host sent overlapping project asset transfers.");
            return;
        }
        _incomingHostAssetBundle = incoming;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHostAssetBundleChunk(long transferId, int index, byte[] chunk)
    {
        if (!IsClient || _incomingHostAssetBundle is not IncomingAssetBundle incoming || incoming.TransferId != transferId)
            return;
        if (!TryReceiveAssetBundleChunk(incoming, index, chunk, out byte[]? package))
        {
            _incomingHostAssetBundle = null;
            AssetBundleRejected?.Invoke("The host sent an incomplete or malformed project asset transfer.");
            return;
        }
        if (package is null)
            return;
        _incomingHostAssetBundle = null;
        try
        {
            string bundleProjectPath = LegacyProjectPackage.ExtractAssetBundle(package);
            AssetBundleReceived?.Invoke(incoming.OriginPeerId, incoming.OriginTransferId, bundleProjectPath);
        }
        catch (Exception exception)
        {
            AssetBundleRejected?.Invoke($"Could not unpack the host's project assets: {exception.Message}");
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveAssetBundleRejected(long transferId, string message)
    {
        if (_pendingAssetBundleTransferIds.Remove(transferId))
            StateChanged?.Invoke();
        AssetBundleRejected?.Invoke(message);
    }

    private static bool TryCreateIncomingAssetBundle(
        long transferId,
        long originPeerId,
        long originTransferId,
        int packageSize,
        int chunkCount,
        out IncomingAssetBundle incoming)
    {
        incoming = null!;
        if (transferId <= 0
            || originPeerId <= 0
            || originTransferId < 0
            || (originPeerId == 1) != (originTransferId == 0)
            || packageSize is < 1 or > MaximumAssetBundleBytes
            || chunkCount is < 1 or > MaximumAssetBundleChunkCount
            || chunkCount != (packageSize + PackageChunkBytes - 1) / PackageChunkBytes)
            return false;
        incoming = new IncomingAssetBundle(transferId, originPeerId, originTransferId, packageSize, chunkCount);
        return true;
    }

    private static bool TryReceiveAssetBundleChunk(
        IncomingAssetBundle incoming,
        int index,
        byte[] chunk,
        out byte[]? package)
    {
        package = null;
        if (index < 0 || index >= incoming.ReceivedChunks.Length)
            return false;
        int offset = index * PackageChunkBytes;
        int expectedLength = Math.Min(PackageChunkBytes, incoming.Package.Length - offset);
        if (chunk.Length != expectedLength)
            return false;
        if (incoming.ReceivedChunks[index])
            return true;
        Buffer.BlockCopy(chunk, 0, incoming.Package, offset, chunk.Length);
        incoming.ReceivedChunks[index] = true;
        incoming.ReceivedChunkCount++;
        if (incoming.ReceivedChunkCount != incoming.ReceivedChunks.Length)
            return true;
        package = incoming.Package;
        return true;
    }

    public bool ConfirmAssetBundleApplied(long originPeerId, long originTransferId)
    {
        if (!IsClient || originPeerId != Multiplayer.GetUniqueId() || !_pendingAssetBundleTransferIds.Remove(originTransferId))
            return false;
        StateChanged?.Invoke();
        return true;
    }

    private static bool TryCreateIncomingDocumentPatch(
        long transferId,
        int characterCount,
        int chunkCount,
        out IncomingDocumentPatch incoming)
    {
        incoming = null!;
        if (transferId <= 0
            || characterCount is < 2 or > MaximumDocumentPatchBytes
            || chunkCount is < 1 or > MaximumDocumentPatchChunkCount
            || chunkCount != (characterCount + DocumentPatchChunkCharacters - 1) / DocumentPatchChunkCharacters)
            return false;
        incoming = new IncomingDocumentPatch(transferId, characterCount, chunkCount);
        return true;
    }

    private static bool TryReceiveDocumentPatchChunk(
        IncomingDocumentPatch incoming,
        int index,
        string chunk,
        out string? payload)
    {
        payload = null;
        if (index < 0 || index >= incoming.ReceivedChunks.Length)
            return false;
        int offset = index * DocumentPatchChunkCharacters;
        int expectedLength = Math.Min(DocumentPatchChunkCharacters, incoming.Payload.Length - offset);
        if (chunk.Length != expectedLength)
            return false;
        if (incoming.ReceivedChunks[index])
            return true;

        chunk.CopyTo(0, incoming.Payload, offset, chunk.Length);
        incoming.ReceivedChunks[index] = true;
        incoming.ReceivedChunkCount++;
        if (incoming.ReceivedChunkCount != incoming.ReceivedChunks.Length)
            return true;

        string json = new(incoming.Payload);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentPatchBytes)
            return false;
        payload = json;
        return true;
    }
    private void SendRoster()
    {
        RefreshPlayers();
        string json = JsonSerializer.Serialize(Players.OrderBy(player => player.SlotNumber));
        Rpc(nameof(ReceiveRoster), json);
    }

    private void SendProjectPackage(long peerId)
    {
        if (_hostProjectPath is null)
        {
            RpcId(peerId, nameof(ReceiveTransferError), "The host project is no longer available.");
            return;
        }
        _packageTransferQueue.Enqueue(peerId);
    }

    private long _activeTransferPeer;
    private int _nextPackageChunk;

    private void PumpProjectTransfer()
    {
        if (!IsHost)
            return;
        if (_activeTransferPeer == 0)
        {
            while (_packageTransferQueue.Count > 0)
            {
                long nextPeer = _packageTransferQueue.Dequeue();
                if (!_playerNames.ContainsKey(nextPeer) || _hostProjectPath is null)
                    continue;
                try
                {
                    _activeTransferPackage = LegacyProjectPackage.Create(_hostProjectPath);
                    _activeTransferPeer = nextPeer;
                    _nextPackageChunk = 0;
                    int queuedChunkCount = (_activeTransferPackage.Length + PackageChunkBytes - 1) / PackageChunkBytes;
                    RpcId(nextPeer, nameof(BeginProjectTransfer), Path.GetFileName(_hostProjectPath), _activeTransferPackage.Length, queuedChunkCount);
                }
                catch (Exception exception)
                {
                    RpcId(nextPeer, nameof(ReceiveTransferError), $"Could not package the current host project: {exception.Message}");
                }
                break;
            }
        }
        if (_activeTransferPeer == 0 || _activeTransferPackage is null)
            return;

        long peerId = _activeTransferPeer;
        int chunkCount = (_activeTransferPackage.Length + PackageChunkBytes - 1) / PackageChunkBytes;
        for (int sent = 0; sent < 16 && _nextPackageChunk < chunkCount; sent++, _nextPackageChunk++)
        {
            int offset = _nextPackageChunk * PackageChunkBytes;
            int length = Math.Min(PackageChunkBytes, _activeTransferPackage.Length - offset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(_activeTransferPackage, offset, chunk, 0, length);
            RpcId(peerId, nameof(ReceiveProjectChunk), _nextPackageChunk, chunk);
        }
        if (_nextPackageChunk < chunkCount)
            return;

        RpcId(peerId, nameof(ReceivePlayMode), _hostPlayMode);
        SetStatus($"Shared {_activeTransferPackage.Length / (1024 * 1024)} MiB with {_playerNames.GetValueOrDefault(peerId, $"player {peerId}")}.");
        _activeTransferPeer = 0;
        _activeTransferPackage = null;
        _nextPackageChunk = 0;
    }
    private void SendActorStructure(long peerId, IReadOnlyList<GBNG.World.WorldActorNode> actors)
    {
        string[] actorData = actors.Select(actor => actor.State.Data.ToJsonString()).ToArray();
        string json = JsonSerializer.Serialize(actorData);
        if (Encoding.UTF8.GetByteCount(json) > MaximumActorStructureBytes)
        {
            _actorStructureTransfers.Remove(peerId);
            RpcId(peerId, nameof(ReceiveTransferError), "Live actor state exceeds the 16 MiB synchronization limit.");
            return;
        }

        int chunkCount = (json.Length + ActorStructureChunkCharacters - 1) / ActorStructureChunkCharacters;
        var transfer = new ActorStructureTransfer(json, chunkCount);
        _actorStructureTransfers[peerId] = transfer;
        RpcId(peerId, nameof(BeginActorStructure), json.Length, chunkCount);
    }

    private void PumpActorStructureTransfers()
    {
        if (!IsHost)
            return;

        foreach (KeyValuePair<long, ActorStructureTransfer> entry in _actorStructureTransfers)
        {
            ActorStructureTransfer transfer = entry.Value;
            for (int sent = 0; sent < ActorStructureChunksPerFrame && transfer.NextChunk < transfer.ChunkCount; sent++)
            {
                int index = transfer.NextChunk++;
                int offset = index * ActorStructureChunkCharacters;
                int length = Math.Min(ActorStructureChunkCharacters, transfer.Payload.Length - offset);
                RpcId(entry.Key, nameof(ReceiveActorStructureChunk), index, transfer.Payload.Substring(offset, length));
            }
            if (transfer.NextChunk == transfer.ChunkCount)
                transfer.Payload = string.Empty;
        }
    }

    private void PumpDocumentPatchTransfers()
    {
        foreach (KeyValuePair<long, OutgoingDocumentPatch> entry in _outgoingDocumentPatches.ToArray())
        {
            long peerId = entry.Key;
            OutgoingDocumentPatch transfer = entry.Value;
            if (IsHost ? !_projectReadyPeers.Contains(peerId) : !IsClient || peerId != 1)
            {
                _outgoingDocumentPatches.Remove(peerId);
                continue;
            }

            if (transfer.Payload is null)
            {
                if (transfer.Pending.Count == 0)
                {
                    _outgoingDocumentPatches.Remove(peerId);
                    continue;
                }
                transfer.Payload = transfer.Pending.Dequeue();
                transfer.TransferId = ++_nextDocumentPatchTransferId;
                if (transfer.TransferId <= 0)
                    transfer.TransferId = _nextDocumentPatchTransferId = 1;
                transfer.ChunkCount = (transfer.Payload.Length + DocumentPatchChunkCharacters - 1) / DocumentPatchChunkCharacters;
                transfer.NextChunk = 0;
                string beginMethod = IsHost ? nameof(BeginHostDocumentPatch) : nameof(BeginClientDocumentPatch);
                RpcId(peerId, beginMethod, transfer.TransferId, transfer.Payload.Length, transfer.ChunkCount);
            }

            for (int sent = 0; sent < DocumentPatchChunksPerFrame && transfer.NextChunk < transfer.ChunkCount; sent++)
            {
                int index = transfer.NextChunk++;
                int offset = index * DocumentPatchChunkCharacters;
                int length = Math.Min(DocumentPatchChunkCharacters, transfer.Payload.Length - offset);
                string method = IsHost ? nameof(ReceiveHostDocumentPatchChunk) : nameof(ReceiveClientDocumentPatchChunk);
                RpcId(peerId, method, transfer.TransferId, index, transfer.Payload.Substring(offset, length));
            }

            if (transfer.NextChunk == transfer.ChunkCount)
            {
                transfer.Payload = null;
                transfer.TransferId = 0;
                transfer.ChunkCount = 0;
                transfer.NextChunk = 0;
            }
        }
    }

    private void PumpAssetBundleTransfers()
    {
        foreach (KeyValuePair<long, OutgoingAssetBundle> entry in _outgoingAssetBundles.ToArray())
        {
            long peerId = entry.Key;
            OutgoingAssetBundle transfer = entry.Value;
            if (IsHost ? !_projectReadyPeers.Contains(peerId) : !IsClient || peerId != 1)
            {
                _outgoingAssetBundles.Remove(peerId);
                continue;
            }

            if (transfer.Current is null)
            {
                if (transfer.Pending.Count == 0)
                {
                    _outgoingAssetBundles.Remove(peerId);
                    continue;
                }
                transfer.Current = transfer.Pending.Dequeue();
                transfer.TransferId = NextAssetBundleTransferId();
                transfer.NextChunk = 0;
                int chunkCount = (transfer.Current.Bytes.Length + PackageChunkBytes - 1) / PackageChunkBytes;
                if (IsHost)
                {
                    RpcId(peerId, nameof(BeginHostAssetBundle), transfer.TransferId,
                        transfer.Current.OriginPeerId, transfer.Current.OriginTransferId,
                        transfer.Current.Bytes.Length, chunkCount);
                }
                else
                {
                    RpcId(peerId, nameof(BeginClientAssetBundle), transfer.TransferId,
                        transfer.Current.OriginTransferId, transfer.Current.Bytes.Length, chunkCount);
                }
            }

            AssetBundlePayload payload = transfer.Current!;
            int totalChunkCount = (payload.Bytes.Length + PackageChunkBytes - 1) / PackageChunkBytes;
            for (int sent = 0; sent < AssetBundleChunksPerFrame && transfer.NextChunk < totalChunkCount; sent++)
            {
                int index = transfer.NextChunk++;
                int offset = index * PackageChunkBytes;
                int length = Math.Min(PackageChunkBytes, payload.Bytes.Length - offset);
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(payload.Bytes, offset, chunk, 0, length);
                string method = IsHost ? nameof(ReceiveHostAssetBundleChunk) : nameof(ReceiveClientAssetBundleChunk);
                RpcId(peerId, method, transfer.TransferId, index, chunk);
            }

            if (transfer.NextChunk == totalChunkCount)
            {
                transfer.Current = null;
                transfer.TransferId = 0;
                transfer.NextChunk = 0;
            }
        }
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
