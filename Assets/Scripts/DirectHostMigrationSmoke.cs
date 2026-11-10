#if !USE_STEAMWORKS
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DirectHostMigrationSmoke : MonoBehaviour
{
  const string RoleArgumentPrefix = "--direct-host-migration-smoke=";
  const string CoordinationArgumentPrefix = "--direct-host-migration-smoke-coordination=";
  const string PortArgumentPrefix = "--direct-host-migration-smoke-port=";
  const string JoinAddressArgumentPrefix = "--direct-host-migration-smoke-address=";
  const string RelayArgument = "--direct-host-migration-smoke-relay";
  const string Marker = "DIRECT_HOST_MIGRATION";

  static bool launched;

  string role;
  string coordinationPath;
  string joinAddress;
  bool useRelay;
  string relaySessionId;
  string relayAddress;
  ushort relayPort;
  ushort port;
  bool completed;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    if (launched || Application.isEditor) return;

    string requestedRole = GetArgument(RoleArgumentPrefix);
    if (requestedRole != "host" && requestedRole != "candidate" && requestedRole != "follower") return;

    string requestedCoordinationPath = GetArgument(CoordinationArgumentPrefix);
    if (string.IsNullOrWhiteSpace(requestedCoordinationPath))
    {
      Debug.LogError(Marker + "_FAIL|The coordination path argument is required.");
      return;
    }

    if (!ushort.TryParse(GetArgument(PortArgumentPrefix), out ushort requestedPort) || requestedPort == 0)
    {
      Debug.LogError(Marker + "_FAIL|A non-zero UDP port argument is required.");
      return;
    }

    string requestedJoinAddress = GetArgument(JoinAddressArgumentPrefix);
    if (string.IsNullOrWhiteSpace(requestedJoinAddress)) requestedJoinAddress = "127.0.0.1";

    launched = true;
    Application.runInBackground = true;
    GameObject runner = new GameObject("DirectHostMigrationSmoke");
    DontDestroyOnLoad(runner);
    DirectHostMigrationSmoke smoke = runner.AddComponent<DirectHostMigrationSmoke>();
    smoke.role = requestedRole;
    smoke.coordinationPath = Path.GetFullPath(requestedCoordinationPath);
    smoke.joinAddress = requestedJoinAddress;
    smoke.useRelay = HasArgument(RelayArgument);
    smoke.port = requestedPort;
    Debug.Log(Marker + "_START|role=" + requestedRole + "|address=" + requestedJoinAddress
      + "|scene=" + SceneManager.GetActiveScene().name);
  }

  static string GetArgument(string prefix)
  {
    foreach (string argument in Environment.GetCommandLineArgs())
      if (argument.StartsWith(prefix, StringComparison.Ordinal)) return argument.Substring(prefix.Length);
    return null;
  }

  static bool HasArgument(string value)
  {
    foreach (string argument in Environment.GetCommandLineArgs())
      if (argument == value) return true;
    return false;
  }

  IEnumerator Start()
  {
    if (role == "host") yield return RunHost();
    else if (role == "candidate") yield return RunCandidate();
    else yield return RunFollower();
  }

  IEnumerator RunHost()
  {
    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (completed) yield break;

    scenes.RestartAndLoadMinimalScene(new GameBuilderApplication.GameOptions
    {
      displayName = "Direct host migration smoke",
      joinPort = port,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    });

    NetworkingController networking = null;
    DirectSessionDirectory.MigrationTicket ticket = default(DirectSessionDirectory.MigrationTicket);
    string hostRelaySessionId = null;
    string hostRelayToken = null;
    string hostRelayAddress = null;
    ushort hostRelayPort = 0;
    yield return WaitFor(() =>
    {
      networking = FindObjectOfType<NetworkingController>();
      bool registered = networking != null && networking.IsDirectHost()
        && DirectSessionDirectory.TryGetMigrationTicket(out ticket) && ticket.Generation == 1;
      return registered && (!useRelay || (DirectHostUdpRelay.IsTunnelReady
        && DirectSessionDirectory.TryGetHostRelayCredentials(
          out hostRelaySessionId, out hostRelayToken, out hostRelayAddress, out hostRelayPort)));
    }, useRelay
      ? "The initial host did not register a migration ticket and relay tunnel."
      : "The initial host did not register a migration ticket.", 90f);
    if (completed) yield break;

    try
    {
      WriteCoordinator(
        DirectHostTls.GetHostCredentials().CertificatePem,
        hostRelaySessionId,
        hostRelayAddress,
        hostRelayPort);
    }
    catch (Exception exception)
    {
      Fail("The initial host could not publish connection details: " + exception.Message);
      yield break;
    }

    yield return WaitFor(() => File.Exists(CandidateReadyPath()),
      "The migration candidate did not complete its initial connection.", 60f);
    if (completed) yield break;
    yield return WaitFor(() => File.Exists(FollowerReadyPath()),
      "The follower did not complete its initial connection.", 60f);
    if (completed) yield break;

    if (!HasInitialPlayers())
    {
      Fail("The initial host did not retain both connected player identities.");
      yield break;
    }

    File.WriteAllText(HostKillReadyPath(), "ready");
    Debug.Log(Marker + "_HOST_READY_TO_KILL|session=" + ticket.SessionId);
    yield return WaitFor(() => false, "", float.PositiveInfinity);
  }

  IEnumerator RunCandidate()
  {
    yield return WaitFor(() => File.Exists(coordinationPath),
      "The initial host did not publish connection details.", 90f);
    if (completed) yield break;

    if (!TryReadCoordinator(out string certificate))
    {
      Fail("The initial host wrote invalid connection details.");
      yield break;
    }

    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (completed) yield break;

    scenes.RestartAndLoadMinimalScene(CreateJoinOptions(certificate));
    NetworkingController networking = null;
    yield return WaitFor(() => IsConnectedPlayer(ref networking, 2, 1),
      "The migration candidate did not receive player ID 2 and a migration ticket.", 60f);
    if (completed) yield break;
    File.WriteAllText(CandidateReadyPath(), "ready");

    DirectSessionDirectory.MigrationTicket migratedTicket = default(DirectSessionDirectory.MigrationTicket);
    yield return WaitFor(() =>
    {
      networking = FindObjectOfType<NetworkingController>();
      return networking != null && networking.IsDirectHost() && PhotonNetwork.player != null
        && PhotonNetwork.player.ID == 1
        && DirectSessionDirectory.TryGetMigrationTicket(out migratedTicket)
        && migratedTicket.Generation >= 2;
    }, "The candidate did not claim and host the migrated session.", 150f);
    if (completed) yield break;

    File.WriteAllText(CandidateMigratedPath(), migratedTicket.Generation.ToString());
    yield return WaitFor(() => File.Exists(FollowerPassPath()) || File.Exists(FollowerFailPath()),
      "The follower did not reconnect to the migrated host.", 210f);
    if (completed) yield break;
    if (File.Exists(FollowerFailPath()))
    {
      Fail("The follower reported failure: " + File.ReadAllText(FollowerFailPath()));
      yield break;
    }

    Pass("role=candidate|generation=" + migratedTicket.Generation);
  }

  IEnumerator RunFollower()
  {
    yield return WaitFor(() => File.Exists(coordinationPath) && File.Exists(CandidateReadyPath()),
      "The initial host and migration candidate did not become ready.", 120f);
    if (completed) yield break;

    if (!TryReadCoordinator(out string certificate))
    {
      Fail("The initial host wrote invalid connection details.");
      yield break;
    }

    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (completed) yield break;

    scenes.RestartAndLoadMinimalScene(CreateJoinOptions(certificate));
    NetworkingController networking = null;
    yield return WaitFor(() => IsConnectedPlayer(ref networking, 3, 1),
      "The follower did not receive player ID 3 and a migration ticket.", 60f);
    if (completed) yield break;
    File.WriteAllText(FollowerReadyPath(), "ready");

    DirectSessionDirectory.MigrationTicket migratedTicket = default(DirectSessionDirectory.MigrationTicket);
    yield return WaitFor(() =>
    {
      networking = FindObjectOfType<NetworkingController>();
      return networking != null && networking.GetIsInMultiplayer() && !networking.IsDirectHost()
        && PhotonNetwork.player != null && PhotonNetwork.player.ID == 3
        && PhotonNetwork.HasDirectPlayerIdentity(1)
        && DirectSessionDirectory.TryGetMigrationTicket(out migratedTicket)
        && migratedTicket.Generation >= 2;
    }, "The follower did not preserve player ID 3 after host migration.", 180f,
      DescribeFollowerRecovery);
    if (completed) yield break;

    File.WriteAllText(FollowerPassPath(), migratedTicket.Generation.ToString());
    Pass("role=follower|generation=" + migratedTicket.Generation + "|player=3");
  }

  GameBuilderApplication.GameOptions CreateJoinOptions(string certificate)
  {
    return new GameBuilderApplication.GameOptions
    {
      joinAddress = joinAddress,
      joinPort = port,
      trustedHostCertificatePem = certificate,
      directRelaySessionId = useRelay ? relaySessionId : null,
      directRelayAddress = useRelay ? relayAddress : null,
      directRelayPort = useRelay ? relayPort : (ushort)0,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
  }

  bool IsConnectedPlayer(ref NetworkingController networking, int expectedPlayerId, int minimumGeneration)
  {
    networking = FindObjectOfType<NetworkingController>();
    DirectSessionDirectory.MigrationTicket ticket;
    return networking != null && networking.GetIsInMultiplayer()
      && networking.HasReceivedInitialWorldState() && !networking.IsDirectHost()
      && PhotonNetwork.player != null && PhotonNetwork.player.ID == expectedPlayerId
      && PhotonNetwork.HasDirectPlayerIdentity(1)
      && DirectSessionDirectory.TryGetMigrationTicket(out ticket)
      && ticket.Generation >= minimumGeneration;
  }

  bool HasInitialPlayers()
  {
    bool candidate = false;
    bool follower = false;
    foreach (PhotonPlayer player in PhotonNetwork.playerList)
    {
      if (player == null) continue;
      candidate |= player.ID == 2 && PhotonNetwork.HasDirectPlayerIdentity(2);
      follower |= player.ID == 3 && PhotonNetwork.HasDirectPlayerIdentity(3);
    }
    return candidate && follower;
  }

  void WriteCoordinator(
    string certificate,
    string hostRelaySessionId,
    string hostRelayAddress,
    ushort hostRelayPort)
  {
    if (useRelay && (string.IsNullOrEmpty(hostRelaySessionId)
      || string.IsNullOrWhiteSpace(hostRelayAddress) || hostRelayPort == 0))
      throw new InvalidOperationException("The relay connection details are incomplete.");
    string directory = Path.GetDirectoryName(coordinationPath);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    string temporaryPath = coordinationPath + ".tmp";
    string[] contents = useRelay
      ? new[]
      {
        port.ToString(),
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(certificate)),
        hostRelaySessionId,
        hostRelayAddress,
        hostRelayPort.ToString()
      }
      : new[]
      {
        port.ToString(),
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(certificate))
      };
    File.WriteAllLines(temporaryPath, contents);
    if (File.Exists(coordinationPath)) File.Delete(coordinationPath);
    File.Move(temporaryPath, coordinationPath);
  }

  bool TryReadCoordinator(out string certificate)
  {
    certificate = null;
    relaySessionId = null;
    relayAddress = null;
    relayPort = 0;
    try
    {
      string[] lines = File.ReadAllLines(coordinationPath);
      int expectedLineCount = useRelay ? 5 : 2;
      if (lines.Length != expectedLineCount
        || !ushort.TryParse(lines[0], out ushort storedPort) || storedPort != port)
        return false;
      certificate = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[1]));
      if (!DirectHostTls.IsUsablePinnedCertificate(certificate)) return false;
      if (!useRelay) return true;

      relaySessionId = lines[2];
      relayAddress = lines[3];
      return relaySessionId.Length == 24 && !string.IsNullOrWhiteSpace(relayAddress)
        && ushort.TryParse(lines[4], out relayPort) && relayPort != 0;
    }
    catch (Exception)
    {
      return false;
    }
  }

  string CandidateReadyPath() { return coordinationPath + ".candidate-ready"; }
  string FollowerReadyPath() { return coordinationPath + ".follower-ready"; }
  string HostKillReadyPath() { return coordinationPath + ".host-kill-ready"; }
  string CandidateMigratedPath() { return coordinationPath + ".candidate-migrated"; }
  string CandidateFailPath() { return coordinationPath + ".candidate-fail"; }
  string FollowerPassPath() { return coordinationPath + ".follower-pass"; }
  string FollowerFailPath() { return coordinationPath + ".follower-fail"; }
  string DescribeFollowerRecovery()
  {
    NetworkingController networking = FindObjectOfType<NetworkingController>();
    DirectSessionDirectory.MigrationTicket ticket;
    bool hasTicket = DirectSessionDirectory.TryGetMigrationTicket(out ticket);
    int playerId = PhotonNetwork.player == null ? 0 : PhotonNetwork.player.ID;
    return "network=" + (networking != null)
      + "|inMultiplayer=" + (networking != null && networking.GetIsInMultiplayer())
      + "|isHost=" + (networking != null && networking.IsDirectHost())
      + "|player=" + playerId
      + "|hostIdentity=" + PhotonNetwork.HasDirectPlayerIdentity(1)
      + "|ticket=" + hasTicket
      + "|generation=" + (hasTicket ? ticket.Generation : 0);
  }


  IEnumerator WaitFor(Func<bool> condition, string failureMessage, float timeoutSeconds,
    Func<string> diagnostics = null)
  {
    float deadline = Time.realtimeSinceStartup + timeoutSeconds;
    while (!condition() && Time.realtimeSinceStartup < deadline)
      yield return new WaitForSecondsRealtime(0.1f);
    if (!condition() && !string.IsNullOrEmpty(failureMessage))
      Fail(failureMessage + (diagnostics == null ? string.Empty : " " + diagnostics()));
  }

  void Pass(string details)
  {
    if (completed) return;
    completed = true;
    Debug.Log(Marker + "_PASS|" + details);
    StartCoroutine(QuitSoon());
  }

  void Fail(string message)
  {
    if (completed) return;
    completed = true;
    try
    {
      if (role == "candidate") File.WriteAllText(CandidateFailPath(), message);
      else if (role == "follower") File.WriteAllText(FollowerFailPath(), message);
    }
    catch (Exception) { }
    Debug.LogError(Marker + "_FAIL|role=" + role + "|" + message);
    StartCoroutine(QuitSoon());
  }

  IEnumerator QuitSoon()
  {
    yield return new WaitForSecondsRealtime(0.5f);
    UserMain userMain = FindObjectOfType<UserMain>();
    MethodInfo onQuitConfirmed = typeof(UserMain).GetMethod("OnQuitConfirmed",
      BindingFlags.Instance | BindingFlags.NonPublic);
    if (userMain != null && onQuitConfirmed != null) onQuitConfirmed.Invoke(userMain, null);
    else Application.Quit();
  }
}
#endif
