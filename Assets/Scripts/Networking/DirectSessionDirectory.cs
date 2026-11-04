#if !USE_PUN
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

public sealed class DirectSessionDirectory : MonoBehaviour
{
  const float HeartbeatInterval = 15f;
  const float SessionRefreshInterval = 10f;
  const int RequestTimeoutSeconds = 10;
  const int MaxSessionNameBytes = 96;

[Serializable]
  public sealed class SessionInfo
  {
    public string SessionId;
    public string Name;
    public string Address;
    public int Port;
    public int PlayerCount;
    public int MaxPlayers;
    public string ServerCertificatePem;
    public int Generation;
    public bool IsPublic;
    public int AddressFamily;
  }

  public struct MigrationTicket
  {
    public string SessionId;
    public string Token;
    public string Name;
    public int Generation;
    public bool IsPublic;
  }

  [Serializable]
  sealed class SessionList
  {
    public SessionInfo[] Sessions;
  }

  [Serializable]
  sealed class SessionRequest
  {
    public string Name;
    public int PlayerCount;
    public int MaxPlayers;
    public int Port;
    public int AddressFamily;
    public string ServerCertificatePem;
    public bool IsPublic;
  }

  [Serializable]
  sealed class RegistrationResponse
  {
    public string SessionId;
    public string Token;
    public string MigrationToken;
    public int Generation;
  }

  [Serializable]
  sealed class AddressResponse
  {
    public string Address;
  }

  static DirectSessionDirectory instance;
  static bool endpointResolved;
  static string endpoint;
  static string endpointError;
  static int revision;
  static string hostPublicAddress;

  NetworkManager manager;
  SessionInfo[] publicSessions = new SessionInfo[0];
  string statusMessage;
  string sessionId;
  string sessionToken;
  string migrationToken;
  int sessionGeneration;
  bool migrationPending;
  bool migrationUnavailable;
  bool browsing;
  bool wasPublishing;
  bool sessionRequestInFlight;
  bool listRequestInFlight;
  bool previousListRequestFailed;
  float nextHostRequestTime;
  float nextListRequestTime;
  int hostGeneration;
  bool addressRequestInFlight;
  bool hostAddressResolved;
  float nextHostAddressRequestTime;

  static string clientMigrationSessionId;
  static string clientMigrationToken;
  static string clientMigrationName;
  static int clientMigrationGeneration;
  static bool clientMigrationIsPublic;
  public static int Revision { get { return revision; } }
  public static string HostPublicAddress { get { return hostPublicAddress; } }
  public static bool IsHostAddressResolved
  {
    get { return string.IsNullOrEmpty(GetEndpoint()) || (instance != null && instance.hostAddressResolved); }
  }

  public static bool IsConfigured
  {
    get { return !string.IsNullOrEmpty(GetEndpoint()); }
  }

  public static string StatusMessage
  {
    get
    {
      string configured = GetEndpoint();
      if (string.IsNullOrEmpty(configured))
        return endpointError ?? "Public session directory is not configured.";
      return EnsureInstance().statusMessage;
    }
  }

  public static void EnsureAvailable()
  {
    EnsureInstance();
  }

  public static void BeginBrowsing()
  {
    DirectSessionDirectory service = EnsureInstance();
    service.browsing = true;
    service.nextListRequestTime = 0f;
  }

  public static void RefreshNow()
  {
    DirectSessionDirectory service = EnsureInstance();
    service.browsing = true;
    service.nextListRequestTime = 0f;
  }

  public static SessionInfo[] GetPublicSessions()
  {
    SessionInfo[] current = EnsureInstance().publicSessions;
    SessionInfo[] copy = new SessionInfo[current.Length];
    Array.Copy(current, copy, current.Length);
    return copy;
  }
  public static bool TryGetMigrationTicket(out MigrationTicket ticket)
  {
    ticket = new MigrationTicket
    {
      SessionId = clientMigrationSessionId,
      Token = clientMigrationToken,
      Name = clientMigrationName,
      Generation = clientMigrationGeneration,
      IsPublic = clientMigrationIsPublic
    };
    return !string.IsNullOrEmpty(ticket.SessionId)
      && !string.IsNullOrEmpty(ticket.Token)
      && ticket.Generation > 0;
  }

  internal static void SetMigrationTicket(
    string id,
    string token,
    int generation,
    bool isPublic,
    string name)
  {
    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token) || generation < 1)
      throw new ArgumentException("A direct-session migration ticket is incomplete.");
    clientMigrationSessionId = id;
    clientMigrationToken = token;
    clientMigrationName = name;
    clientMigrationGeneration = generation;
    clientMigrationIsPublic = isPublic;
  }

  internal static void ClearMigrationTicket()
  {
    clientMigrationSessionId = null;
    clientMigrationToken = null;
    clientMigrationName = null;
    clientMigrationGeneration = 0;
    clientMigrationIsPublic = false;
  }

  public static void BeginMigrationWatch(
    MigrationTicket ticket,
    Action<SessionInfo> onMigrated,
    Action onFailed)
  {
    DirectSessionDirectory service = EnsureInstance();
    service.StartCoroutine(service.WatchForMigrationRoutine(ticket, onMigrated, onFailed));
  }

  static DirectSessionDirectory EnsureInstance()
  {
    if (instance != null) return instance;
    GameObject serviceObject = new GameObject("DirectSessionDirectory");
    DontDestroyOnLoad(serviceObject);
    return serviceObject.AddComponent<DirectSessionDirectory>();
  }

  static string GetEndpoint()
  {
    if (endpointResolved) return endpoint;
    endpointResolved = true;

    string configured = null;
    string[] args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length; i++)
    {
      const string argumentPrefix = "--session-directory=";
      if (args[i].StartsWith(argumentPrefix, StringComparison.OrdinalIgnoreCase))
      {
        configured = args[i].Substring(argumentPrefix.Length).Trim();
        break;
      }
    }
    if (string.IsNullOrWhiteSpace(configured))
      configured = Environment.GetEnvironmentVariable("GAMEBUILDER_SESSION_DIRECTORY");
    if (string.IsNullOrWhiteSpace(configured)) return null;

    Uri parsed;
    if (!Uri.TryCreate(configured.Trim().TrimEnd('/'), UriKind.Absolute, out parsed)
      || (parsed.Scheme != Uri.UriSchemeHttps
        && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
    {
      endpointError = "Public session directory must use HTTPS; HTTP is allowed only for localhost smoke tests.";
      Debug.LogWarning(endpointError);
      return null;
    }
    endpoint = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
    return endpoint;
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
    if (GetEndpoint() != null)
      statusMessage = "Connecting to public session directory...";
  }

  void Update()
  {
    if (manager == null) manager = NetworkManager.Singleton;
    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    GameBuilderApplication.PlayOptions playOptions = options.playOptions;
    bool shouldPublish = ShouldPublishHost(
      manager != null && manager.IsListening,
      manager != null && manager.IsServer,
      playOptions);
    if (shouldPublish)
    {
      if (!wasPublishing)
      {
        migrationUnavailable = false;
        wasPublishing = true;
        hostPublicAddress = null;
        hostAddressResolved = false;
        hostGeneration++;
        nextHostRequestTime = 0f;
        if (!string.IsNullOrEmpty(options.directHostMigrationSessionId))
        {
          sessionId = options.directHostMigrationSessionId;
          migrationToken = options.directHostMigrationToken;
          sessionGeneration = options.directHostMigrationGeneration;
          migrationPending = !string.IsNullOrEmpty(migrationToken);
          if (migrationPending)
            SetMigrationTicket(
              sessionId, migrationToken, sessionGeneration, playOptions.startAsPublic,
              options.directHostMigrationName);
        }
      }
      if (!string.IsNullOrEmpty(GetEndpoint())
        && !addressRequestInFlight
        && Time.realtimeSinceStartup >= nextHostAddressRequestTime)
      {
        StartCoroutine(RefreshHostAddressRoutine(hostGeneration));
      }
      if (!migrationUnavailable
        && !string.IsNullOrEmpty(GetEndpoint())
        && DirectHostPortMapper.IsMappingResolved
        && !sessionRequestInFlight
        && Time.realtimeSinceStartup >= nextHostRequestTime)
      {
        StartCoroutine(PublishHostRoutine(hostGeneration));
      }
    }
    else if (wasPublishing)
    {
      wasPublishing = false;
      hostPublicAddress = null;
      hostAddressResolved = true;
      nextHostAddressRequestTime = 0f;
      hostGeneration++;
      nextHostRequestTime = 0f;
      if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(sessionToken))
      {
        StartCoroutine(UnregisterRoutine(sessionId, sessionToken));
        sessionId = null;
        sessionToken = null;
        migrationToken = null;
        migrationPending = false;
      }
    }

    if (browsing && !string.IsNullOrEmpty(GetEndpoint())
      && !listRequestInFlight
      && Time.realtimeSinceStartup >= nextListRequestTime)
    {
      StartCoroutine(RefreshPublicSessionsRoutine());
    }
  }
  static bool ShouldPublishHost(
    bool isListening,
    bool isServer,
    GameBuilderApplication.PlayOptions playOptions)
  {
    return isListening && isServer && playOptions.isMultiplayer;
  }


  IEnumerator PublishHostRoutine(int generation)
  {
    sessionRequestInFlight = true;
    nextHostRequestTime = Time.realtimeSinceStartup + HeartbeatInterval;
    string baseUrl = GetEndpoint();
    SessionRequest data = GetCurrentSessionRequest();
    bool creating = string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(sessionToken);
    bool migrating = migrationPending;
    string url = migrating
      ? baseUrl + "/v1/sessions/" + Uri.EscapeDataString(sessionId) + "/migrate"
      : creating
        ? baseUrl + "/v1/sessions"
        : baseUrl + "/v1/sessions/" + Uri.EscapeDataString(sessionId);
    UnityWebRequest request = CreateJsonRequest(
      migrating || creating ? "POST" : "PUT", url, JsonUtility.ToJson(data));
    string requestToken = migrating ? migrationToken : sessionToken;
    if (!string.IsNullOrEmpty(requestToken))
      request.SetRequestHeader("Authorization", "Bearer " + requestToken);
    yield return request.SendWebRequest();

    bool successful = request.result == UnityWebRequest.Result.Success
      && (migrating ? request.responseCode == 200
        : creating ? request.responseCode == 201 : request.responseCode == 204);
    string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
    long responseCode = request.responseCode;
    string requestError = request.error;
    request.Dispose();

    if (successful && (creating || migrating))
    {
      RegistrationResponse response = null;
      try { response = JsonUtility.FromJson<RegistrationResponse>(responseBody); }
      catch (Exception e) { Debug.LogWarning("Could not parse direct session registration response: " + e.Message); }
      if (response == null || string.IsNullOrEmpty(response.SessionId)
        || string.IsNullOrEmpty(response.Token) || string.IsNullOrEmpty(response.MigrationToken)
        || response.Generation < 1)
      {
        Debug.LogWarning("Session directory returned an invalid registration or migration response.");
        nextHostRequestTime = Time.realtimeSinceStartup + HeartbeatInterval;
      }
      else
      {
        sessionId = response.SessionId;
        sessionToken = response.Token;
        migrationToken = response.MigrationToken;
        sessionGeneration = response.Generation;
        migrationPending = false;
        SetMigrationTicket(
          sessionId, migrationToken, sessionGeneration,
          GameBuilderApplication.CurrentGameOptions.playOptions.startAsPublic,
          data.Name);
        Debug.Log(migrating
          ? "Migrated direct-host session through the directory."
          : "Registered direct-host session in the directory.");
      }
    }
    else if (!successful)
    {
      if (migrating && responseCode == 409)
      {
        nextHostRequestTime = Time.realtimeSinceStartup + 5f;
      }
      else if (migrating && responseCode == 404)
      {
        sessionToken = null;
        migrationPending = false;
        migrationUnavailable = true;
        ClearMigrationTicket();
        Debug.LogWarning("The previous direct session expired before host migration completed.");
      }
      else if (responseCode == 404)
      {
        sessionId = null;
        sessionToken = null;
        migrationToken = null;
        migrationPending = false;
        nextHostRequestTime = Time.realtimeSinceStartup + 5f;
      }
      if (requestError != "" || responseCode != 0)
      {
        string failureMessage = !string.IsNullOrEmpty(responseBody)
          ? responseBody
          : requestError ?? "HTTP " + responseCode;
        Debug.LogWarning("Could not update direct session directory entry: " + failureMessage);
      }
    }

    bool hostStillPublishing = generation == hostGeneration
      && ShouldPublishHost(
        manager != null && manager.IsListening,
        manager != null && manager.IsServer,
        GameBuilderApplication.CurrentGameOptions.playOptions);
    if (!hostStillPublishing && !string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(sessionToken))
    {
      string endedSessionId = sessionId;
      string endedToken = sessionToken;
      sessionId = null;
      sessionToken = null;
      migrationToken = null;
      migrationPending = false;
      yield return UnregisterRoutine(endedSessionId, endedToken);
    }
    sessionRequestInFlight = false;
  }

  IEnumerator RefreshHostAddressRoutine(int generation)
  {
    addressRequestInFlight = true;
    nextHostAddressRequestTime = Time.realtimeSinceStartup + HeartbeatInterval;
    UnityWebRequest request = UnityWebRequest.Get(GetEndpoint() + "/v1/address");
    request.downloadHandler = new DownloadHandlerBuffer();
    request.timeout = RequestTimeoutSeconds;
    request.redirectLimit = 0;
    yield return request.SendWebRequest();

    bool successful = request.result == UnityWebRequest.Result.Success
      && request.responseCode == 200;
    string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
    request.Dispose();
    addressRequestInFlight = false;
    if (!successful || generation != hostGeneration || !wasPublishing)
    {
      if (generation == hostGeneration && wasPublishing) hostAddressResolved = true;
      yield break;
    }

    AddressResponse response = null;
    try { response = JsonUtility.FromJson<AddressResponse>(responseBody); }
    catch (Exception) { }
    IPAddress address;
    int expectedFamily = GameBuilderApplication.CurrentGameOptions.directHostUsesIpv6 ? 6 : 4;
    if (response != null
      && IPAddress.TryParse(response.Address, out address)
      && (address.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4) == expectedFamily
      && !address.IsIPv6Multicast
      && !address.IsIPv4MappedToIPv6)
      hostPublicAddress = address.ToString();
    else
      hostPublicAddress = null;
    hostAddressResolved = true;

  }


  IEnumerator UnregisterRoutine(string id, string token)
  {
    string baseUrl = GetEndpoint();
    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) yield break;
    UnityWebRequest request = new UnityWebRequest(
      baseUrl + "/v1/sessions/" + Uri.EscapeDataString(id), "DELETE");
    request.downloadHandler = new DownloadHandlerBuffer();
    request.timeout = RequestTimeoutSeconds;
    request.redirectLimit = 0;
    request.SetRequestHeader("Authorization", "Bearer " + token);
    yield return request.SendWebRequest();
    if (request.result != UnityWebRequest.Result.Success && request.responseCode != 404)
      Debug.LogWarning("Could not remove direct-host session from the public directory: " + request.error);
    request.Dispose();
  }
  IEnumerator WatchForMigrationRoutine(
    MigrationTicket ticket,
    Action<SessionInfo> onMigrated,
    Action onFailed)
  {
    string baseUrl = GetEndpoint();
    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(ticket.SessionId)
      || string.IsNullOrEmpty(ticket.Token) || ticket.Generation < 1)
    {
      onFailed?.Invoke();
      yield break;
    }

    float deadline = Time.realtimeSinceStartup + 240f;
    string url = baseUrl + "/v1/sessions/" + Uri.EscapeDataString(ticket.SessionId) + "/migration";
    while (Time.realtimeSinceStartup < deadline)
    {
      UnityWebRequest request = UnityWebRequest.Get(url);
      request.downloadHandler = new DownloadHandlerBuffer();
      request.timeout = RequestTimeoutSeconds;
      request.redirectLimit = 0;
      request.SetRequestHeader("Authorization", "Bearer " + ticket.Token);
      yield return request.SendWebRequest();

      bool successful = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
      string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
      long responseCode = request.responseCode;
      request.Dispose();
      if (successful)
      {
        SessionInfo migrated = null;
        try { migrated = JsonUtility.FromJson<SessionInfo>(responseBody); }
        catch (Exception e) { Debug.LogWarning("Could not parse migrated session details: " + e.Message); }
        IPAddress address;
        bool valid = migrated != null
          && migrated.SessionId == ticket.SessionId
          && migrated.Generation > ticket.Generation
          && IPAddress.TryParse(migrated.Address, out address)
          && migrated.Port > 0 && migrated.Port <= 65535
          && migrated.PlayerCount >= 1 && migrated.MaxPlayers >= migrated.PlayerCount
          && migrated.MaxPlayers <= PhotonNetwork.MaximumDirectPlayers
          && migrated.AddressFamily == (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 6 : 4)
          && DirectHostTls.IsUsablePinnedCertificate(migrated.ServerCertificatePem);
        if (valid)
        {
          onMigrated?.Invoke(migrated);
          yield break;
        }
      }
      else if (responseCode == 404)
      {
        onFailed?.Invoke();
        yield break;
      }
      yield return new WaitForSecondsRealtime(3f);
    }
    onFailed?.Invoke();
  }

  IEnumerator RefreshPublicSessionsRoutine()
  {
    listRequestInFlight = true;
    nextListRequestTime = Time.realtimeSinceStartup + SessionRefreshInterval;
    UnityWebRequest request = UnityWebRequest.Get(GetEndpoint() + "/v1/sessions");
    request.downloadHandler = new DownloadHandlerBuffer();
    request.timeout = RequestTimeoutSeconds;
    request.redirectLimit = 0;
    yield return request.SendWebRequest();

    bool successful = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
    string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
    string requestError = request.error;
    long responseCode = request.responseCode;
    request.Dispose();
    if (successful)
    {
      SessionList result = null;
      try { result = JsonUtility.FromJson<SessionList>(responseBody); }
      catch (Exception e) { Debug.LogWarning("Could not parse public session list: " + e.Message); }
      if (result == null || result.Sessions == null)
      {
        ClearPublicSessions();
        SetStatus("Public session directory returned an invalid list.");
      }
      else
      {
        List<SessionInfo> validSessions = new List<SessionInfo>(result.Sessions.Length);
        foreach (SessionInfo item in result.Sessions)
        {
          IPAddress address;
          if (item == null || string.IsNullOrEmpty(item.SessionId) || string.IsNullOrEmpty(item.Name)
            || !IPAddress.TryParse(item.Address, out address)
            || (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
              && address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            || item.Port < 1 || item.Port > 65535
            || item.PlayerCount < 1 || item.PlayerCount >= item.MaxPlayers
            || item.MaxPlayers < item.PlayerCount || item.MaxPlayers > PhotonNetwork.MaximumDirectPlayers
            || !DirectHostTls.IsUsablePinnedCertificate(item.ServerCertificatePem))
            continue;
          item.AddressFamily = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 6 : 4;
          item.IsPublic = true;
          validSessions.Add(item);
        }
        validSessions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        SessionInfo[] updatedSessions = validSessions.ToArray();
        bool changed = !SessionsEqual(publicSessions, updatedSessions);
        publicSessions = updatedSessions;
        if (changed) revision++;
        previousListRequestFailed = false;
        SetStatus("Public directory connected.");
      }
    }
    else
    {
      ClearPublicSessions();
      if (!previousListRequestFailed)
        Debug.LogWarning("Could not refresh public sessions: " + (requestError ?? "HTTP " + responseCode));
      previousListRequestFailed = true;
      SetStatus("Public session directory unavailable; retrying.");
    }
    listRequestInFlight = false;
  }

  void ClearPublicSessions()
  {
    if (publicSessions.Length == 0) return;
    publicSessions = new SessionInfo[0];
    revision++;
  }


  void SetStatus(string value)
  {
    if (statusMessage == value) return;
    statusMessage = value;
    revision++;
  }

  static bool SessionsEqual(SessionInfo[] left, SessionInfo[] right)
  {
    if (left.Length != right.Length) return false;
    for (int i = 0; i < left.Length; i++)
    {
      SessionInfo a = left[i];
      SessionInfo b = right[i];
      if (a.SessionId != b.SessionId || a.Name != b.Name || a.Address != b.Address
        || a.Port != b.Port || a.PlayerCount != b.PlayerCount || a.MaxPlayers != b.MaxPlayers
        || a.ServerCertificatePem != b.ServerCertificatePem)
        return false;
    }
    return true;
  }

  SessionRequest GetCurrentSessionRequest()
  {
    string name = GameBuilderApplication.CurrentGameOptions.displayName;
    if (string.IsNullOrWhiteSpace(name))
    {
      string nickname = PhotonNetwork.player == null ? null : PhotonNetwork.player.NickName;
      name = string.IsNullOrWhiteSpace(nickname) ? "Game Builder game" : nickname + "'s game";
    }
    name = LimitUtf8Bytes(name, MaxSessionNameBytes);
    int playerCount = manager == null || manager.ConnectedClientsIds == null
      ? 1
      : Mathf.Clamp(manager.ConnectedClientsIds.Count, 1, PhotonNetwork.MaximumDirectPlayers);
    return new SessionRequest
    {
      Name = name,
      PlayerCount = playerCount,
      MaxPlayers = PhotonNetwork.MaximumDirectPlayers,
      AddressFamily = GameBuilderApplication.CurrentGameOptions.directHostUsesIpv6 ? 6 : 4,
      Port = DirectHostPortMapper.PublicPort,
      ServerCertificatePem = DirectHostTls.GetHostCredentials().CertificatePem,
      IsPublic = GameBuilderApplication.CurrentGameOptions.playOptions.startAsPublic,
    };
  }

  static string LimitUtf8Bytes(string value, int maxBytes)
  {
    if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
    StringBuilder result = new StringBuilder(Math.Min(value.Length, maxBytes));
    int byteCount = 0;
    for (int i = 0; i < value.Length; i++)
    {
      char current = value[i];
      bool isPair = char.IsHighSurrogate(current)
        && i + 1 < value.Length
        && char.IsLowSurrogate(value[i + 1]);
      int currentBytes = isPair ? 4
        : char.IsSurrogate(current) ? 3
        : current <= 0x7f ? 1
        : current <= 0x7ff ? 2
        : 3;
      if (byteCount + currentBytes > maxBytes) break;
      result.Append(current);
      if (isPair) result.Append(value[++i]);
      byteCount += currentBytes;
    }
    return result.ToString();
  }

  static UnityWebRequest CreateJsonRequest(string method, string url, string body)
  {
    UnityWebRequest request = new UnityWebRequest(url, method);
    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
    request.downloadHandler = new DownloadHandlerBuffer();
    request.timeout = RequestTimeoutSeconds;
    request.redirectLimit = 0;
    request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
    return request;
  }

  void OnDestroy()
  {
    if (instance == this)
    {
      hostPublicAddress = null;
      instance = null;
    }
  }
}
#endif
