using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;

var options = SessionOptions.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8765");
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.KnownProxies.Add(IPAddress.Loopback);
    forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SessionRegistry>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/v1/session", async (HttpContext context, SessionRegistry registry) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Use WSS for non-local matchmaking.", context.RequestAborted);
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    var peer = new SessionPeer(socket, context.Connection.RemoteIpAddress);
    await registry.RunPeerAsync(peer, context.RequestAborted);
});
app.Run();

internal sealed record SessionOptions(string[] StunUrls, string[] TurnUrls, string TurnSharedSecret)
{
    public static SessionOptions FromEnvironment()
    {
        string[] stunUrls = ReadUrls("GBNG_STUN_URLS");
        string[] turnUrls = ReadUrls("GBNG_TURN_URLS");
        string turnSecret = Environment.GetEnvironmentVariable("GBNG_TURN_SHARED_SECRET") ?? string.Empty;
        if (turnUrls.Length > 0 && Encoding.UTF8.GetByteCount(turnSecret) < 32)
            throw new InvalidOperationException("GBNG_TURN_SHARED_SECRET must contain at least 32 UTF-8 bytes when TURN is enabled.");
        return new SessionOptions(stunUrls, turnUrls, turnSecret);
    }

    private static string[] ReadUrls(string variable)
        => (Environment.GetEnvironmentVariable(variable) ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme.Equals("stun", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("stuns", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("turn", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("turns", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed class SessionRegistry(SessionOptions options)
{
    private const int MaximumMessageBytes = 64 * 1024;
    private const int MaximumPlayersPerRoom = 8;
    private const int MaximumActiveRooms = 4096;
    private const int MaximumTrackedJoinSources = 65_536;
    private const int MaximumJoinAttemptsPerMinute = 30;
    private const string RoomCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly long JoinAttemptWindowTicks = Stopwatch.Frequency * 60L;
    private readonly ConcurrentDictionary<string, MatchRoom> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptWindow> _joinAttempts = new(StringComparer.Ordinal);
    private readonly object _joinAttemptsSync = new();
    private readonly object _roomCreationLock = new();
    private long _nextJoinAttemptCleanupAt;
    public async Task RunPeerAsync(SessionPeer peer, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        try
        {
            while (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await peer.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        if (peer.Socket.State == WebSocketState.CloseReceived)
                            await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        return;
                    }
                    if (received.MessageType != WebSocketMessageType.Text)
                    {
                        await ClosePeerAsync(peer, WebSocketCloseStatus.InvalidMessageType, "Text messages are required.");
                        return;
                    }
                    if (message.Length + received.Count > MaximumMessageBytes)
                    {
                        await ClosePeerAsync(peer, WebSocketCloseStatus.MessageTooBig, "Message exceeds the 64 KiB limit.");
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                if (!peer.TryConsumeMessage())
                {
                    await ClosePeerAsync(peer, WebSocketCloseStatus.PolicyViolation, "Message rate limit exceeded.");
                    return;
                }

                using JsonDocument request = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
                await HandleRequestAsync(peer, request.RootElement, cancellationToken);
                if (peer.Socket.State is WebSocketState.Closed or WebSocketState.Aborted)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The HTTP server is stopping or the client disconnected.
        }
        catch (WebSocketException)
        {
            // Disconnect cleanup below removes the room membership.
        }
        catch (JsonException)
        {
            await ClosePeerAsync(peer, WebSocketCloseStatus.InvalidPayloadData, "Invalid JSON message.");
        }
        finally
        {
            await DisconnectAsync(peer);
        }
    }

    private async Task HandleRequestAsync(SessionPeer peer, JsonElement request, CancellationToken cancellationToken)
    {
        string? type = ReadString(request, "type", 24);
        if (peer.Room is null)
        {
            if (type == "host")
            {
                await CreateRoomAsync(peer, request, cancellationToken);
                return;
            }
            if (type == "join")
            {
                await JoinRoomAsync(peer, request, cancellationToken);
                return;
            }
            await SendErrorAsync(peer, "start_required", "Start a private room or join with an invite code.", cancellationToken);
            return;
        }

        switch (type)
        {
            case "signal":
                await ForwardSignalAsync(peer, request, cancellationToken);
                break;
            case "leave":
                await ClosePeerAsync(peer, WebSocketCloseStatus.NormalClosure, "Left room.");
                break;
            case "ping":
                await peer.SendAsync(new { type = "pong" }, cancellationToken);
                break;
            default:
                await SendErrorAsync(peer, "invalid_message", "Only signal, leave, and ping are accepted after joining.", cancellationToken);
                break;
        }
    }

    private async Task CreateRoomAsync(SessionPeer peer, JsonElement request, CancellationToken cancellationToken)
    {
        string displayName = NormalizeDisplayName(ReadString(request, "displayName", 64));
        int requestedCapacity = request.TryGetProperty("maxPlayers", out JsonElement capacityValue)
            && capacityValue.TryGetInt32(out int capacity)
            ? Math.Clamp(capacity, 2, MaximumPlayersPerRoom)
            : MaximumPlayersPerRoom;

        MatchRoom? room = null;
        lock (_roomCreationLock)
        {
            if (_rooms.Count < MaximumActiveRooms)
            {
                for (int attempt = 0; attempt < 8 && room is null; attempt++)
                {
                    string code = CreateRoomCode();
                    var candidate = new MatchRoom(code, requestedCapacity);
                    if (_rooms.TryAdd(code, candidate))
                        room = candidate;
                }
            }
        }
        if (room is null)
        {
            await SendErrorAsync(peer, "capacity", "The matchmaking service cannot create another room right now.", cancellationToken);
            return;
        }

        peer.Room = room;
        peer.PeerId = 1;
        peer.DisplayName = displayName;
        room.Peers.TryAdd(peer.PeerId, peer);
        await peer.SendAsync(new
        {
            type = "hosted",
            roomCode = room.Code,
            peerId = peer.PeerId,
            maxPlayers = room.MaxPlayers,
            iceServers = CreateIceServers(room.Code)
        }, cancellationToken);
    }

    private async Task JoinRoomAsync(SessionPeer peer, JsonElement request, CancellationToken cancellationToken)
    {
        string code = NormalizeRoomCode(ReadString(request, "roomCode", 16));
        if (code.Length != 8 || !ConsumeJoinAttempt(peer.RemoteAddress))
        {
            await SendErrorAsync(peer, "invite_invalid", "That invite could not be used. Check the code and try again.", cancellationToken);
            return;
        }
        if (!_rooms.TryGetValue(code, out MatchRoom? room))
        {
            await SendErrorAsync(peer, "invite_invalid", "That invite could not be used. Check the code and try again.", cancellationToken);
            return;
        }

        int peerId = 0;
        SessionPeer? host = null;
        bool joined = false;
        lock (room.Sync)
        {
            if (!room.Closed && room.Peers.Count < room.MaxPlayers && room.Peers.TryGetValue(1, out host))
            {
                do
                {
                    peerId = RandomNumberGenerator.GetInt32(2, int.MaxValue);
                }
                while (room.Peers.ContainsKey(peerId));
                peer.Room = room;
                peer.PeerId = peerId;
                peer.DisplayName = NormalizeDisplayName(ReadString(request, "displayName", 64));
                room.Peers.TryAdd(peerId, peer);
                joined = true;
            }
        }
        if (!joined)
        {
            await SendErrorAsync(peer, "room_full", "The host is unavailable or the room is full.", cancellationToken);
            return;
        }

        await peer.SendAsync(new
        {
            type = "joined",
            roomCode = room.Code,
            peerId,
            maxPlayers = room.MaxPlayers,
            iceServers = CreateIceServers(room.Code)
        }, cancellationToken);
        await host!.SendAsync(new { type = "peer_joined", peerId, displayName = peer.DisplayName }, cancellationToken);
    }

    private async Task ForwardSignalAsync(SessionPeer sender, JsonElement request, CancellationToken cancellationToken)
    {
        if (sender.Room is not { } room
            || !request.TryGetProperty("toPeerId", out JsonElement destinationValue)
            || !destinationValue.TryGetInt32(out int destinationId)
            || !request.TryGetProperty("signal", out JsonElement signal)
            || !IsValidSignal(signal))
        {
            await SendErrorAsync(sender, "invalid_signal", "The connection signal is invalid.", cancellationToken);
            return;
        }

        SessionPeer? destination;
        lock (room.Sync)
        {
            if (room.Closed || destinationId == sender.PeerId || !room.Peers.TryGetValue(destinationId, out destination))
                destination = null;
        }
        if (destination is null)
        {
            await SendErrorAsync(sender, "peer_unavailable", "The other player is no longer in the room.", cancellationToken);
            return;
        }
        await destination.SendAsync(new { type = "signal", fromPeerId = sender.PeerId, signal = signal.Clone() }, cancellationToken);
    }

    private static bool IsValidSignal(JsonElement signal)
    {
        if (signal.ValueKind != JsonValueKind.Object || !signal.TryGetProperty("kind", out JsonElement kindValue))
            return false;
        string? kind = kindValue.GetString();
        if (kind == "description")
        {
            string? sdpType = ReadString(signal, "sdpType", 16);
            string? sdp = ReadString(signal, "sdp", 48 * 1024);
            return (sdpType is "offer" or "answer") && !string.IsNullOrWhiteSpace(sdp);
        }
        if (kind == "candidate")
        {
            string? mid = ReadString(signal, "mid", 128);
            string? candidate = ReadString(signal, "candidate", 4096);
            return !string.IsNullOrWhiteSpace(mid)
                && !string.IsNullOrWhiteSpace(candidate)
                && signal.TryGetProperty("index", out JsonElement indexValue)
                && indexValue.TryGetInt32(out int index)
                && index is >= 0 and <= 255;
        }
        return false;
    }

    private async Task DisconnectAsync(SessionPeer peer)
    {
        MatchRoom? room = peer.Room;
        if (room is null)
            return;

        SessionPeer[] notify;
        bool hostLeft;
        lock (room.Sync)
        {
            room.Peers.TryRemove(peer.PeerId, out _);
            peer.Room = null;
            hostLeft = peer.PeerId == 1;
            if (hostLeft)
            {
                room.Closed = true;
                notify = room.Peers.Values.ToArray();
            }
            else
            {
                notify = room.Peers.TryGetValue(1, out SessionPeer? host) ? [host] : Array.Empty<SessionPeer>();
            }
        }

        if (hostLeft)
        {
            _rooms.TryRemove(room.Code, out _);
            foreach (SessionPeer participant in notify)
                await participant.TrySendAsync(new { type = "host_left" }, CancellationToken.None);
        }
        else
        {
            foreach (SessionPeer participant in notify)
                await participant.TrySendAsync(new { type = "peer_left", peerId = peer.PeerId }, CancellationToken.None);
        }
    }

    private bool ConsumeJoinAttempt(IPAddress? address)
    {
        string key = address?.ToString() ?? "unknown";
        lock (_joinAttemptsSync)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextJoinAttemptCleanupAt)
            {
                foreach (KeyValuePair<string, AttemptWindow> entry in _joinAttempts)
                {
                    if (now - entry.Value.StartedAt >= JoinAttemptWindowTicks)
                        _joinAttempts.Remove(entry.Key);
                }
                _nextJoinAttemptCleanupAt = now + JoinAttemptWindowTicks;
            }

            if (!_joinAttempts.TryGetValue(key, out AttemptWindow? window))
            {
                if (_joinAttempts.Count >= MaximumTrackedJoinSources)
                    return false;
                window = new AttemptWindow { StartedAt = now };
                _joinAttempts.Add(key, window);
            }
            else if (now - window.StartedAt >= JoinAttemptWindowTicks)
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            if (window.Count >= MaximumJoinAttemptsPerMinute)
                return false;
            window.Count++;
            return true;
        }
    }

    private object[] CreateIceServers(string roomCode)
    {
        var iceServers = new List<object>();
        if (options.StunUrls.Length > 0)
            iceServers.Add(new { urls = options.StunUrls });
        if (options.TurnUrls.Length > 0)
        {
            long expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            string username = $"{expiry}:{roomCode}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}";
            byte[] password = HMACSHA1.HashData(Encoding.UTF8.GetBytes(options.TurnSharedSecret), Encoding.UTF8.GetBytes(username));
            iceServers.Add(new
            {
                urls = options.TurnUrls,
                username,
                credential = Convert.ToBase64String(password)
            });
        }
        return iceServers.ToArray();
    }

    private static string CreateRoomCode()
    {
        Span<char> characters = stackalloc char[8];
        for (int index = 0; index < characters.Length; index++)
            characters[index] = RoomCodeAlphabet[RandomNumberGenerator.GetInt32(RoomCodeAlphabet.Length)];
        return new string(characters);
    }

    private static string NormalizeRoomCode(string? value)
        => string.Concat((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant();

    private static string NormalizeDisplayName(string? value)
    {
        string normalized = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return normalized.Length is > 0 and <= 32 ? normalized : "Family player";
    }

    private static string? ReadString(JsonElement element, string name, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
            return null;
        string? text = value.GetString();
        return text is not null && text.Length <= maximumLength ? text : null;
    }

    private static Task SendErrorAsync(SessionPeer peer, string code, string message, CancellationToken cancellationToken)
        => peer.SendAsync(new { type = "error", code, message }, cancellationToken);

    private static async Task ClosePeerAsync(SessionPeer peer, WebSocketCloseStatus status, string reason)
    {
        if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await peer.Socket.CloseOutputAsync(status, reason, CancellationToken.None);
    }
}

internal sealed class MatchRoom(string code, int maxPlayers)
{
    public object Sync { get; } = new();
    public string Code { get; } = code;
    public int MaxPlayers { get; } = maxPlayers;
    public bool Closed { get; set; }
    public ConcurrentDictionary<int, SessionPeer> Peers { get; } = new();
}

internal sealed class SessionPeer(WebSocket socket, IPAddress? remoteAddress)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _messageWindowLock = new();
    private long _messageWindowStartedAt;
    private const int MaximumMessagesPerSecond = 160;
    private int _messagesInWindow;

    public WebSocket Socket { get; } = socket;
    public IPAddress? RemoteAddress { get; } = remoteAddress;
    public MatchRoom? Room { get; set; }
    public int PeerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public bool TryConsumeMessage()
    {
        lock (_messageWindowLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _messageWindowStartedAt >= Stopwatch.Frequency)
            {
                _messageWindowStartedAt = now;
                _messagesInWindow = 0;
            }
            if (_messagesInWindow >= MaximumMessagesPerSecond)
                return false;
            _messagesInWindow++;
            return true;
        }
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task TrySendAsync<T>(T message, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Disconnect cleanup owns the membership of a closing peer.
        }
    }
}

internal sealed class AttemptWindow
{
    public long StartedAt { get; set; }
    public int Count { get; set; }
}

