#if !USE_PUN
using System;
using System.Collections;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public sealed class DirectHostDiscoverySmokeTemp : MonoBehaviour
{
  const string HostArgument = "--direct-discovery-host-smoke";
  const string ClientArgument = "--direct-discovery-client-smoke";
  const string TestGameName = "Direct Discovery Smoke";
  static bool launched;
  static bool requestedHost;
  NetworkManager smokeManager;
  bool isHost;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    string[] args = Environment.GetCommandLineArgs();
    bool host = args.Contains(HostArgument);
    bool client = args.Contains(ClientArgument);
    if (launched || Application.isEditor || host == client) return;

    launched = true;
    requestedHost = host;
    Application.runInBackground = true;
    GameObject runner = new GameObject("DirectHostDiscoverySmokeTemp");
    DontDestroyOnLoad(runner);
    runner.AddComponent<DirectHostDiscoverySmokeTemp>();
  }

  void Awake()
  {
    isHost = requestedHost;
    GameBuilderApplication.GameOptions options = GameBuilderApplication.CurrentGameOptions;
    options.displayName = TestGameName;
    GameBuilderApplication.Get().SetGameOptionsDoNotUseExceptFromSceneController(options);
    PhotonNetwork.player.NickName = "DiscoverySmoke";
    if (!isHost)
    {
      DirectHostDiscovery.BeginBrowsing();
      return;
    }

    if (NetworkManager.Singleton != null)
    {
      Fail("A NetworkManager already existed in the startup scene.");
      return;
    }

    GameObject networkObject = new GameObject("DirectDiscoverySmokeNetwork");
    networkObject.SetActive(false);
    UnityTransport transport = networkObject.AddComponent<UnityTransport>();
    smokeManager = networkObject.AddComponent<NetworkManager>();
    smokeManager.NetworkConfig = new NetworkConfig
    {
      NetworkTransport = transport,
      EnableSceneManagement = false
    };
    networkObject.SetActive(true);
    transport.SetConnectionData("127.0.0.1", NetworkingController.DefaultDirectHostPort, "0.0.0.0");
    DirectHostDiscovery.EnsureAvailable();
    if (!smokeManager.StartHost())
    {
      Fail("NetworkManager.StartHost returned false.");
      return;
    }
    Debug.Log("DIRECT_DISCOVERY_HOST_STARTED");
  }

  IEnumerator Start()
  {
    float deadline = Time.realtimeSinceStartup + 12f;
    if (isHost)
    {
      while (Time.realtimeSinceStartup < deadline) yield return null;
      if (smokeManager != null && smokeManager.IsListening && smokeManager.IsServer)
      {
        Debug.Log("DIRECT_DISCOVERY_HOST_PASS|server-listening=true");
        Application.Quit(0);
      }
      else Fail("The host stopped listening before the smoke completed.");
      yield break;
    }

    while (Time.realtimeSinceStartup < deadline)
    {
      foreach (DirectHostDiscovery.HostInfo host in DirectHostDiscovery.GetDiscoveredHosts())
      {
        if (host.Name != TestGameName || host.Port != NetworkingController.DefaultDirectHostPort
          || host.PlayerCount != 1 || host.MaxPlayers != PhotonNetwork.MaximumDirectPlayers) continue;
        Debug.Log($"DIRECT_DISCOVERY_CLIENT_PASS|address={host.Address}|players={host.PlayerCount}/{host.MaxPlayers}|port={host.Port}");
        Application.Quit(0);
        yield break;
      }
      yield return null;
    }
    Fail("No matching LAN host announcement arrived within 12 seconds.");
  }

  void Fail(string message)
  {
    Debug.LogError("DIRECT_DISCOVERY_SMOKE_FAIL|" + message);
    Application.Quit(1);
  }
}
#endif
