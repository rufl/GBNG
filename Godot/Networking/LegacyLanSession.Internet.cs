using System.Net;
using System.Text.Json;
using Godot;

namespace GBNG.Networking;

public sealed partial class LegacyLanSession
{
    private const int MatchmakingHandshakeTimeoutSeconds = 20;
    private const int MaximumPendingIceCandidates = 64;
    private readonly Dictionary<int, WebRtcConnection> _webRtcConnections = new();
    private readonly HashSet<int> _remoteDescriptionsSet = new();
    private readonly Dictionary<int, List<PendingIceCandidate>> _pendingIceCandidates = new();
    private readonly Dictionary<int, PendingWebRtcDescription> _pendingRemoteDescriptions = new();
    private MultiplayerPeer? _webRtcPeer;
    private WebSocketPeer? _matchmakingSocket;
    private Godot.Collections.Dictionary _iceConfiguration = new();
    private OnlineSessionIntent? _onlineIntent;
    private bool _matchmakingRequestSent;
    private long _matchmakingDeadline;
    private string _onlineRoomCode = string.Empty;

    public event Action<string>? InviteCodeChanged;
    public event Action<string>? OnlineRoomJoined;
    public string OnlineRoomCode => _onlineRoomCode;
    public bool IsBusy => HasPeer || _matchmakingSocket is not null;
    public bool WebRtcAvailable
        => ClassDB.ClassExists("WebRTCMultiplayerPeer")
            && ClassDB.ClassExists("WebRTCPeerConnection");

    public Error HostOnline(string projectPath, string playerName, string matchmakingUrl)
    {
        try
        {
            _ = LegacyProjectPackage.Create(projectPath);
        }
        catch (Exception exception)
        {
            return Fail($"Cannot share this project: {exception.Message}");
        }
        if (!WebRtcAvailable)
            return Fail("Internet hosting needs the WebRTC native extension. Run tools/networking/setup-webrtc.ps1 first.");
        if (!TryNormalizeMatchmakingUrl(matchmakingUrl, out string endpoint, out string error))
            return Fail(error);

        Leave();
        _localPlayerName = NormalizePlayerName(playerName);
        _roomName = "Family game";
        _hostProjectPath = projectPath;
        _isHost = true;
        _hostPort = 0;
        _snapshotElapsed = 0d;
        _playerNames[1] = _localPlayerName;

        _webRtcPeer = CreateWebRtcMultiplayerPeer();
        if (_webRtcPeer is null)
            return Fail("Could not create the WebRTC multiplayer peer. Check that the native extension is installed and enabled.");
        Error createError = _webRtcPeer.Call("create_server").As<Error>();
        if (createError != Error.Ok)
        {
            Leave();
            return Fail($"Could not start WebRTC hosting: {createError}.");
        }
        Multiplayer.MultiplayerPeer = _webRtcPeer;
        RefreshPlayers();

        Error connectError = ConnectMatchmaking(endpoint, new OnlineSessionIntent(true, string.Empty));
        if (connectError != Error.Ok)
        {
            Leave();
            return Fail($"Could not reach the matchmaking service: {connectError}.");
        }
        SetStatus("Starting a private family room…");
        return Error.Ok;
    }

    public Error JoinOnline(string roomCode, string playerName, string matchmakingUrl)
    {
        if (!WebRtcAvailable)
            return Fail("Internet joining needs the WebRTC native extension. Run tools/networking/setup-webrtc.ps1 first.");
        if (!TryNormalizeMatchmakingUrl(matchmakingUrl, out string endpoint, out string error))
            return Fail(error);
        string normalizedCode = new((roomCode ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray());
        normalizedCode = normalizedCode.ToUpperInvariant();
        if (normalizedCode.Length != 8)
            return Fail("Enter the 8-character invite code from the host.");

        Leave();
        _localPlayerName = NormalizePlayerName(playerName);
        _isHost = false;
        Error connectError = ConnectMatchmaking(endpoint, new OnlineSessionIntent(false, normalizedCode));
        if (connectError != Error.Ok)
        {
            Leave();
            return Fail($"Could not reach the matchmaking service: {connectError}.");
        }
        SetStatus("Connecting to the family room…");
        return Error.Ok;
    }

    private Error ConnectMatchmaking(string endpoint, OnlineSessionIntent intent)
    {
        var socket = new WebSocketPeer { HeartbeatInterval = 15f };
        Error error = socket.ConnectToUrl(endpoint);
        if (error != Error.Ok)
            return error;
        _matchmakingSocket = socket;
        _onlineIntent = intent;
        _matchmakingRequestSent = false;
        _matchmakingDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + System.Diagnostics.Stopwatch.Frequency * MatchmakingHandshakeTimeoutSeconds;
        return Error.Ok;
    }

    private void PollInternetTransport(double delta)
    {
        _matchmakingSocket?.Poll();
        foreach (WebRtcConnection connection in _webRtcConnections.Values)
        {
            Error pollError = connection.Native.Call("poll").As<Error>();
            if (pollError != Error.Ok)
            {
                FailOnlineSession($"The WebRTC connection could not be polled: {pollError}.");
                return;
            }
        }

        if (_matchmakingSocket is null)
            return;
        int state = (int)_matchmakingSocket.GetReadyState();
        if (state == 1)
        {
            if (!_matchmakingRequestSent)
            {
                object request = _onlineIntent is { IsHost: true }
                    ? new { type = "host", displayName = _localPlayerName, maxPlayers = 8 }
                    : new { type = "join", displayName = _localPlayerName, roomCode = _onlineIntent?.RoomCode ?? string.Empty };
                Error sendError = _matchmakingSocket.SendText(JsonSerializer.Serialize(request));
                if (sendError != Error.Ok)
                {
                    FailOnlineSession($"Could not send the room request: {sendError}.");
                    return;
                }
                _matchmakingRequestSent = true;
            }
            while (_matchmakingSocket.GetAvailablePacketCount() > 0)
            {
                byte[] packet = _matchmakingSocket.GetPacket();
                if (!_matchmakingSocket.WasStringPacket())
                {
                    FailOnlineSession("The matchmaking service sent an unsupported message.");
                    return;
                }
                try
                {
                    using JsonDocument message = JsonDocument.Parse(packet);
                    HandleMatchmakingMessage(message.RootElement);
                }
                catch (JsonException)
                {
                    FailOnlineSession("The matchmaking service sent an invalid response.");
                    return;
                }
                if (_matchmakingSocket is null)
                    return;
            }
        }
        else if (state == 3)
        {
            string closeReason = _matchmakingSocket.GetCloseReason();
            FailOnlineSession(closeReason.Length == 0
                ? "The matchmaking connection closed. Check the service address and TLS certificate."
                : $"Matchmaking connection closed: {closeReason}");
            return;
        }

        if (_matchmakingDeadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= _matchmakingDeadline)
            FailOnlineSession("The matchmaking service did not answer in time. Try again or check its address.");
    }

    private void HandleMatchmakingMessage(JsonElement message)
    {
        string? type = ReadJsonString(message, "type", 24);
        switch (type)
        {
            case "hosted":
                _onlineRoomCode = NormalizeRoomCode(ReadJsonString(message, "roomCode", 16));
                if (_onlineRoomCode.Length != 8 || !TryReadIceConfiguration(message, out _iceConfiguration))
                {
                    FailOnlineSession("The matchmaking service returned an invalid room response.");
                    return;
                }
                _roomId = _onlineRoomCode;
                _matchmakingDeadline = 0;
                InviteCodeChanged?.Invoke(_onlineRoomCode);
                SetStatus("Your private room is ready. Share this code with your family.");
                break;
            case "joined":
                string roomCode = NormalizeRoomCode(ReadJsonString(message, "roomCode", 16));
                if (roomCode.Length != 8
                    || !message.TryGetProperty("peerId", out JsonElement peerIdValue)
                    || !peerIdValue.TryGetInt32(out int peerId)
                    || peerId is <= 1 or int.MaxValue
                    || !TryReadIceConfiguration(message, out _iceConfiguration))
                {
                    FailOnlineSession("The matchmaking service returned an invalid join response.");
                    return;
                }
                _onlineRoomCode = roomCode;
                _roomId = roomCode;
                _matchmakingDeadline = 0;
                InviteCodeChanged?.Invoke(_onlineRoomCode);
                if (!StartWebRtcClient(peerId))
                    return;
                SetStatus("Invite accepted. Connecting directly to the host; relay is used only if the network requires it.");
                break;
            case "peer_joined":
                if (!_isHost
                    || !message.TryGetProperty("peerId", out JsonElement remotePeerValue)
                    || !remotePeerValue.TryGetInt32(out int remotePeerId)
                    || remotePeerId <= 1)
                    return;
                string displayName = ReadJsonString(message, "displayName", 64);
                _playerNames[remotePeerId] = NormalizePlayerName(displayName);
                RefreshPlayers();
                StartWebRtcHostConnection(remotePeerId);
                break;
            case "signal":
                if (message.TryGetProperty("fromPeerId", out JsonElement fromPeerValue)
                    && fromPeerValue.TryGetInt32(out int fromPeerId)
                    && message.TryGetProperty("signal", out JsonElement signal))
                    ApplyWebRtcSignal(fromPeerId, signal);
                break;
            case "peer_left":
                if (message.TryGetProperty("peerId", out JsonElement leftPeerValue)
                    && leftPeerValue.TryGetInt32(out int leftPeerId))
                    RemoveWebRtcPeer(leftPeerId);
                break;
            case "host_left":
                LeavePeerOnly();
                SetStatus("The host ended the family room.");
                break;
            case "error":
                string error = ReadJsonString(message, "message", 256);
                SetStatus(error.Length == 0 ? "The matchmaking service rejected the request." : error);
                break;
        }
    }

    private bool StartWebRtcClient(int clientPeerId)
    {
        _webRtcPeer = CreateWebRtcMultiplayerPeer();
        if (_webRtcPeer is null)
        {
            FailOnlineSession("Could not create the WebRTC multiplayer peer. Check the native extension installation.");
            return false;
        }
        Error createError = _webRtcPeer.Call("create_client", clientPeerId).As<Error>();
        if (createError != Error.Ok)
        {
            FailOnlineSession($"Could not create the WebRTC client: {createError}.");
            return false;
        }
        Multiplayer.MultiplayerPeer = _webRtcPeer;
        return AddWebRtcConnection(1, createOffer: false);
    }

    private void StartWebRtcHostConnection(int remotePeerId)
    {
        if (!_isHost || _webRtcPeer is null || !AddWebRtcConnection(remotePeerId, createOffer: true))
            FailOnlineSession("Could not prepare a secure connection to the joining player.");
    }

    private bool AddWebRtcConnection(int peerId, bool createOffer)
    {
        if (_webRtcPeer is null || _webRtcConnections.ContainsKey(peerId))
            return false;
        GodotObject? connection = ClassDB.Instantiate("WebRTCPeerConnection").AsGodotObject();
        if (connection is null)
        {
            FailOnlineSession("The WebRTC extension could not create a peer connection.");
            return false;
        }
        Error initializeError = connection.Call("initialize", _iceConfiguration).As<Error>();
        if (initializeError != Error.Ok)
        {
            connection.Call("close");
            FailOnlineSession($"The WebRTC extension could not initialize: {initializeError}.");
            return false;
        }

        var peerConnection = new WebRtcConnection(connection);
        peerConnection.DescriptionHandler = Callable.From<string, string>(
            (type, sdp) => OnSessionDescriptionCreated(peerId, peerConnection, type, sdp));
        peerConnection.CandidateHandler = Callable.From<string, int, string>(
            (media, index, candidate) => SendWebRtcSignal(peerId, new
            {
                kind = "candidate",
                mid = media,
                index,
                candidate
            }));
        Error descriptionConnectError = connection.Connect("session_description_created", peerConnection.DescriptionHandler);
        Error candidateConnectError = connection.Connect("ice_candidate_created", peerConnection.CandidateHandler);
        if (descriptionConnectError != Error.Ok || candidateConnectError != Error.Ok)
        {
            connection.Call("close");
            FailOnlineSession("The WebRTC extension could not connect its negotiation signals.");
            return false;
        }

        _webRtcConnections.Add(peerId, peerConnection);
        Error addError = _webRtcPeer.Call("add_peer", connection, peerId).As<Error>();
        if (addError != Error.Ok)
        {
            RemoveWebRtcPeer(peerId);
            FailOnlineSession($"Could not add the WebRTC peer: {addError}.");
            return false;
        }
        if (_pendingRemoteDescriptions.Remove(peerId, out PendingWebRtcDescription pendingDescription))
        {
            Error descriptionError = connection.Call("set_remote_description", pendingDescription.Type, pendingDescription.Sdp).As<Error>();
            if (descriptionError != Error.Ok)
            {
                FailOnlineSession($"Could not accept the peer's connection description: {descriptionError}.");
                return false;
            }
            _remoteDescriptionsSet.Add(peerId);
            ApplyQueuedIceCandidates(peerId, peerConnection);
        }
        if (createOffer)
        {
            Error offerError = connection.Call("create_offer").As<Error>();
            if (offerError != Error.Ok)
            {
                FailOnlineSession($"Could not start the secure peer connection: {offerError}.");
                return false;
            }
        }
        return true;
    }

    private void OnSessionDescriptionCreated(int peerId, WebRtcConnection connection, string type, string sdp)
    {
        Error error = connection.Native.Call("set_local_description", type, sdp).As<Error>();
        if (error != Error.Ok)
        {
            FailOnlineSession($"Could not apply the WebRTC session description: {error}.");
            return;
        }
        SendWebRtcSignal(peerId, new { kind = "description", sdpType = type, sdp });
    }

    private void ApplyWebRtcSignal(int fromPeerId, JsonElement signal)
    {
        if (!signal.TryGetProperty("kind", out JsonElement kindValue))
            return;
        string? kind = kindValue.GetString();
        if (kind == "description")
        {
            string sdpType = ReadJsonString(signal, "sdpType", 16);
            string sdp = ReadJsonString(signal, "sdp", 48 * 1024);
            if (sdpType is not ("offer" or "answer") || sdp.Length == 0)
                return;
            if (!_webRtcConnections.TryGetValue(fromPeerId, out WebRtcConnection? connection))
            {
                if (!_isHost && fromPeerId == 1)
                    _pendingRemoteDescriptions[fromPeerId] = new PendingWebRtcDescription(sdpType, sdp);
                return;
            }
            ApplyRemoteDescription(fromPeerId, connection, sdpType, sdp);
        }
        else if (kind == "candidate"
            && signal.TryGetProperty("mid", out JsonElement midValue)
            && midValue.ValueKind == JsonValueKind.String
            && signal.TryGetProperty("index", out JsonElement indexValue)
            && indexValue.TryGetInt32(out int index)
            && index is >= 0 and <= 255
            && signal.TryGetProperty("candidate", out JsonElement candidateValue)
            && candidateValue.ValueKind == JsonValueKind.String
            && candidateValue.GetString() is { Length: > 0 and <= 4096 } candidateText)
        {
            var candidate = new PendingIceCandidate(midValue.GetString() ?? string.Empty, index, candidateText);
            if (_webRtcConnections.TryGetValue(fromPeerId, out WebRtcConnection? connection)
                && _remoteDescriptionsSet.Contains(fromPeerId))
            {
                connection.Native.Call("add_ice_candidate", candidate.Mid, candidate.Index, candidate.Candidate);
            }
            else if (!_isHost && fromPeerId == 1)
            {
                QueueIceCandidate(fromPeerId, candidate);
            }
        }
    }

    private void ApplyRemoteDescription(int peerId, WebRtcConnection connection, string type, string sdp)
    {
        Error error = connection.Native.Call("set_remote_description", type, sdp).As<Error>();
        if (error != Error.Ok)
        {
            FailOnlineSession($"Could not accept the peer's connection description: {error}.");
            return;
        }
        _remoteDescriptionsSet.Add(peerId);
        ApplyQueuedIceCandidates(peerId, connection);
    }

    private void QueueIceCandidate(int peerId, PendingIceCandidate candidate)
    {
        if (!_pendingIceCandidates.TryGetValue(peerId, out List<PendingIceCandidate>? pending))
        {
            if (_pendingIceCandidates.Count >= 7)
                return;
            _pendingIceCandidates[peerId] = pending = new List<PendingIceCandidate>();
        }
        if (pending.Count < MaximumPendingIceCandidates)
            pending.Add(candidate);
    }

    private void ApplyQueuedIceCandidates(int peerId, WebRtcConnection connection)
    {
        if (!_pendingIceCandidates.Remove(peerId, out List<PendingIceCandidate>? pending))
            return;
        foreach (PendingIceCandidate candidate in pending)
            connection.Native.Call("add_ice_candidate", candidate.Mid, candidate.Index, candidate.Candidate);
    }
    private void SendWebRtcSignal(int peerId, object signal)
    {
        if (_matchmakingSocket is null || (int)_matchmakingSocket.GetReadyState() != 1)
            return;
        Error error = _matchmakingSocket.SendText(JsonSerializer.Serialize(new
        {
            type = "signal",
            toPeerId = peerId,
            signal
        }));
        if (error != Error.Ok)
            SetStatus($"Could not forward WebRTC connection setup: {error}.");
    }

    private void RemoveWebRtcPeer(int peerId)
    {
        _pendingIceCandidates.Remove(peerId);
        _pendingRemoteDescriptions.Remove(peerId);
        _remoteDescriptionsSet.Remove(peerId);
        if (_webRtcConnections.Remove(peerId, out WebRtcConnection? connection))
        {
            if (_webRtcPeer?.Call("has_peer", peerId).As<bool>() == true)
                _webRtcPeer.Call("remove_peer", peerId);
            connection.Native.Disconnect("session_description_created", connection.DescriptionHandler);
            connection.Native.Disconnect("ice_candidate_created", connection.CandidateHandler);
            connection.Native.Call("close");
        }
    }

    private void LeaveInternetTransport()
    {
        if (_matchmakingSocket is not null)
        {
            if ((int)_matchmakingSocket.GetReadyState() == 1)
                _matchmakingSocket.SendText(JsonSerializer.Serialize(new { type = "leave" }));
            _matchmakingSocket.Close();
            _matchmakingSocket = null;
        }
        foreach (WebRtcConnection connection in _webRtcConnections.Values)
        {
            connection.Native.Disconnect("session_description_created", connection.DescriptionHandler);
            connection.Native.Disconnect("ice_candidate_created", connection.CandidateHandler);
            connection.Native.Call("close");
        }
        _webRtcConnections.Clear();
        _pendingIceCandidates.Clear();
        _pendingRemoteDescriptions.Clear();
        _remoteDescriptionsSet.Clear();
        _webRtcPeer?.Close();
        _webRtcPeer = null;
        _iceConfiguration = new Godot.Collections.Dictionary();
        _onlineIntent = null;
        _matchmakingRequestSent = false;
        _matchmakingDeadline = 0;
        _onlineRoomCode = string.Empty;
        InviteCodeChanged?.Invoke(_onlineRoomCode);
    }

    private void FailOnlineSession(string message)
    {
        bool wasClient = IsClient;
        LeaveInternetTransport();
        _isHost = false;
        _hostPlayMode = false;
        if (_enetPeer is not null)
        {
            _enetPeer.Close();
            _enetPeer = null;
        }
        Multiplayer.MultiplayerPeer = null;
        _playerNames.Clear();
        _inputsByPeer.Clear();
        _lastInputSequences.Clear();
        _projectReadyPeers.Clear();
        _incomingPackage = null;
        _receivedChunks = null;
        _incomingChunkCount = 0;
        _receivedChunkCount = 0;
        RefreshPlayers();
        if (wasClient)
            HostPlayModeChanged?.Invoke(false);
        SetStatus(message);
    }

    private static bool TryReadIceConfiguration(JsonElement message, out Godot.Collections.Dictionary configuration)
    {
        configuration = new Godot.Collections.Dictionary();
        var iceServers = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (message.TryGetProperty("iceServers", out JsonElement serverValues))
        {
            if (serverValues.ValueKind != JsonValueKind.Array || serverValues.GetArrayLength() > 8)
                return false;
            foreach (JsonElement serverValue in serverValues.EnumerateArray())
            {
                if (serverValue.ValueKind != JsonValueKind.Object
                    || !serverValue.TryGetProperty("urls", out JsonElement urlValues)
                    || urlValues.ValueKind != JsonValueKind.Array
                    || urlValues.GetArrayLength() is < 1 or > 8)
                    return false;
                var urls = new Godot.Collections.Array<string>();
                foreach (JsonElement urlValue in urlValues.EnumerateArray())
                {
                    if (urlValue.ValueKind != JsonValueKind.String || urlValue.GetString() is not { Length: > 0 and <= 512 } url)
                        return false;
                    urls.Add(url);
                }
                var server = new Godot.Collections.Dictionary { ["urls"] = urls };
                if (ReadJsonString(serverValue, "username", 256) is { Length: > 0 } username)
                    server["username"] = username;
                if (ReadJsonString(serverValue, "credential", 512) is { Length: > 0 } credential)
                    server["credential"] = credential;
                iceServers.Add(server);
            }
        }
        configuration["iceServers"] = iceServers;
        return true;
    }

    private static bool TryNormalizeMatchmakingUrl(string url, out string endpoint, out string error)
    {
        endpoint = string.Empty;
        error = "Enter the secure matchmaking address ending in /v1/session.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
            return false;
        bool loopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) && !loopback)
        {
            error = "Use wss:// for an Internet matchmaking service. ws:// is allowed only on this computer.";
            return false;
        }
        UriBuilder builder = new(uri);
        if (builder.Path.TrimEnd('/').Length == 0)
            builder.Path = "/v1/session";
        else if (!builder.Path.TrimEnd('/').EndsWith("/v1/session", StringComparison.OrdinalIgnoreCase))
            return false;
        endpoint = builder.Uri.ToString();
        error = string.Empty;
        return true;
    }

    private static string NormalizeRoomCode(string? value)
        => new string((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string ReadJsonString(JsonElement element, string name, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        string? text = value.GetString();
        return text is not null && text.Length <= maximumLength ? text : string.Empty;
    }

    private readonly record struct OnlineSessionIntent(bool IsHost, string RoomCode);
    private static MultiplayerPeer? CreateWebRtcMultiplayerPeer()
    {
        if (!ClassDB.CanInstantiate("WebRTCMultiplayerPeer"))
            return null;
        return ClassDB.Instantiate("WebRTCMultiplayerPeer").As<MultiplayerPeer>();
    }

    private sealed class WebRtcConnection(GodotObject native)
    {
        public GodotObject Native { get; } = native;
        public Callable DescriptionHandler { get; set; }
        public Callable CandidateHandler { get; set; }
    }
    private readonly record struct PendingWebRtcDescription(string Type, string Sdp);
    private readonly record struct PendingIceCandidate(string Mid, int Index, string Candidate);
}
