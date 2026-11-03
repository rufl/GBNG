#if !USE_PUN
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
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
  }

  [Serializable]
  sealed class RegistrationResponse
  {
    public string SessionId;
    public string Token;
  }

  static DirectSessionDirectory instance;
  static bool endpointResolved;
  static string endpoint;
  static string endpointError;
  static int revision;

  NetworkManager manager;
  SessionInfo[] publicSessions = new SessionInfo[0];
  string statusMessage;
  string sessionId;
  string sessionToken;
  bool browsing;
  bool wasPublishing;
  bool sessionRequestInFlight;
  bool listRequestInFlight;
  bool previousListRequestFailed;
  float nextHostRequestTime;
  float nextListRequestTime;
  int hostGeneration;

  public static int Revision { get { return revision; } }

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
    GameBuilderApplication.PlayOptions playOptions = GameBuilderApplication.CurrentGameOptions.playOptions;
    bool shouldPublish = ShouldPublishHost(
      manager != null && manager.IsListening,
      manager != null && manager.IsServer,
      playOptions);
    if (shouldPublish)
    {
      if (!wasPublishing)
      {
        wasPublishing = true;
        hostGeneration++;
        nextHostRequestTime = 0f;
      }
      if (!string.IsNullOrEmpty(GetEndpoint())
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
      hostGeneration++;
      nextHostRequestTime = 0f;
      if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(sessionToken))
      {
        StartCoroutine(UnregisterRoutine(sessionId, sessionToken));
        sessionId = null;
        sessionToken = null;
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
    return isListening && isServer && playOptions.isMultiplayer && playOptions.startAsPublic;
  }


  IEnumerator PublishHostRoutine(int generation)
  {
    sessionRequestInFlight = true;
    nextHostRequestTime = Time.realtimeSinceStartup + HeartbeatInterval;
    string baseUrl = GetEndpoint();
    SessionRequest data = GetCurrentSessionRequest();
    bool creating = string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(sessionToken);
    string url = creating
      ? baseUrl + "/v1/sessions"
      : baseUrl + "/v1/sessions/" + Uri.EscapeDataString(sessionId);
    UnityWebRequest request = CreateJsonRequest(creating ? "POST" : "PUT", url, JsonUtility.ToJson(data));
    if (!creating) request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
    yield return request.SendWebRequest();

    bool successful = request.result == UnityWebRequest.Result.Success
      && (creating ? request.responseCode == 201 : request.responseCode == 204);
    string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
    long responseCode = request.responseCode;
    string requestError = request.error;
    request.Dispose();

    if (successful && creating)
    {
      RegistrationResponse response = null;
      try { response = JsonUtility.FromJson<RegistrationResponse>(responseBody); }
      catch (Exception e) { Debug.LogWarning("Could not parse public session registration response: " + e.Message); }
      if (response == null || string.IsNullOrEmpty(response.SessionId) || string.IsNullOrEmpty(response.Token))
      {
        Debug.LogWarning("Public session directory returned an invalid registration response.");
        nextHostRequestTime = Time.realtimeSinceStartup + HeartbeatInterval;
      }
      else
      {
        sessionId = response.SessionId;
        sessionToken = response.Token;
        Debug.Log("Registered direct-host session in the public directory.");
      }
    }
    else if (!successful)
    {
      if (!creating && responseCode == 404)
      {
        sessionId = null;
        sessionToken = null;
      }
      if (requestError != "" || responseCode != 0)
      {
        string failureMessage = !string.IsNullOrEmpty(responseBody)
          ? responseBody
          : requestError ?? "HTTP " + responseCode;
        Debug.LogWarning("Could not update public session directory entry: " + failureMessage);
      }
    }

    GameBuilderApplication.PlayOptions playOptions = GameBuilderApplication.CurrentGameOptions.playOptions;
    bool hostStillPublic = generation == hostGeneration
      && ShouldPublishHost(
        manager != null && manager.IsListening,
        manager != null && manager.IsServer,
        playOptions);
    if (!hostStillPublic && !string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(sessionToken))
    {
      string endedSessionId = sessionId;
      string endedToken = sessionToken;
      sessionId = null;
      sessionToken = null;
      yield return UnregisterRoutine(endedSessionId, endedToken);
    }
    sessionRequestInFlight = false;
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
            || item.MaxPlayers < item.PlayerCount || item.MaxPlayers > PhotonNetwork.MaximumDirectPlayers)
            continue;
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
        || a.Port != b.Port || a.PlayerCount != b.PlayerCount || a.MaxPlayers != b.MaxPlayers)
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
      Port = DirectHostPortMapper.PublicPort
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
    if (instance == this) instance = null;
  }
}
#endif
