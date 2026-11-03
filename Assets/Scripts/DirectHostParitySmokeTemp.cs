using System;
using System.Collections;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DirectHostParitySmokeTemp : MonoBehaviour
{
  const string HostArgument = "--direct-parity-host-smoke";
  const string ClientArgument = "--direct-parity-client-smoke";
  const string GuestVirtualPlayerId = "DirectParitySmokeGuest";
  const ushort SmokePort = 47777;

  static bool launched;
  bool isHost;
  bool failed;
  int mainSceneLoads;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    if (launched || Application.isEditor) return;
    string[] args = Environment.GetCommandLineArgs();
    bool host = args.Contains(HostArgument);
    if (!host && !args.Contains(ClientArgument)) return;

    launched = true;
    Application.runInBackground = true;
    GameObject runner = new GameObject("DirectHostParitySmokeTemp");
    DontDestroyOnLoad(runner);
    runner.AddComponent<DirectHostParitySmokeTemp>().isHost = host;
  }

  void Awake()
  {
    SceneManager.sceneLoaded += OnSceneLoaded;
  }

  void OnDestroy()
  {
    SceneManager.sceneLoaded -= OnSceneLoaded;
  }

  void OnSceneLoaded(Scene scene, LoadSceneMode mode)
  {
    if (scene.name == "main") mainSceneLoads++;
  }

  IEnumerator Start()
  {
    yield return RunSmoke();
  }

  IEnumerator RunSmoke()
  {
    Debug.Log($"DIRECT_PARITY_START|host={isHost}");
    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "GameBuilderSceneController did not load.", 60f);
    if (failed) yield break;

    GameBuilderApplication.GameOptions options = new GameBuilderApplication.GameOptions
    {
      joinAddress = isHost ? null : "127.0.0.1",
      joinPort = SmokePort,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    };
    if (isHost) scenes.RestartAndLoadMinimalScene(options);
    else scenes.LoadMainSceneAsync(options);

    yield return WaitFor(() =>
    {
      NetworkingController networking = FindObjectOfType<NetworkingController>();
      return mainSceneLoads >= 1 && networking != null && networking.GetIsInMultiplayer();
    }, "Direct host/client did not enter the multiplayer session.", 90f);
    if (failed) yield break;

    string expectedNickname = GetExpectedNickname();
    NetworkingController controller = FindObjectOfType<NetworkingController>();
    if (!isHost)
    {
      yield return WaitFor(() => PhotonNetwork.player.ID > 1
        && HasPlayer(1, expectedNickname)
        && PhotonNetwork.playerList.Length >= 2,
        "Client did not receive the host identity and roster.", 60f);
      if (failed) yield break;

      VirtualPlayerManager manager = FindObjectOfType<VirtualPlayerManager>();
      if (manager == null)
      {
        Fail("VirtualPlayerManager was missing on the client.");
        yield break;
      }
      manager.RegisterVirtualPlayer(GuestVirtualPlayerId);
      yield return WaitFor(() => manager.GetVirtualPlayerById(GuestVirtualPlayerId).HasValue,
        "MasterClient-targeted virtual-player registration did not replicate.", 30f);
      if (failed) yield break;
    }
    else
    {
      yield return WaitFor(() => PhotonNetwork.playerList.Length >= 2 && HasPlayer(2, expectedNickname),
        "Host did not receive the joining client's identity and roster.", 90f);
      if (failed) yield break;

      VirtualPlayerManager manager = FindObjectOfType<VirtualPlayerManager>();
      if (manager == null)
      {
        Fail("VirtualPlayerManager was missing on the host.");
        yield break;
      }
      yield return WaitFor(() => manager.GetVirtualPlayerById(GuestVirtualPlayerId).HasValue,
        "Host did not process the client's MasterClient-targeted registration.", 30f);
      if (failed) yield break;
      Debug.Log("DIRECT_PARITY_MASTER_RPC_PASS");

      if (!NetworkingController.CanDoMultiplayerMapSwitch())
      {
        Fail("The direct host could not switch maps while retaining its session.");
        yield break;
      }

      GameBuilderApplication.GameOptions mapOptions = GameBuilderApplication.CurrentGameOptions;
      mapOptions.playOptions.isMultiplayer = true;
      scenes.LoadMainSceneAsync(mapOptions);
      yield return WaitFor(() => mainSceneLoads >= 2
        && FindObjectOfType<NetworkingController>() != null
        && FindObjectOfType<NetworkingController>().GetIsInMultiplayer()
        && PhotonNetwork.playerList.Length >= 2
        && HasPlayer(2, expectedNickname),
        "Direct host map switch did not reload the client state and preserve the session.", 90f);
      if (failed) yield break;
      Debug.Log("DIRECT_PARITY_MAP_SWITCH_PASS");

      controller = FindObjectOfType<NetworkingController>();
      controller.KickPlayer(PhotonPlayer.Find(2));
      yield return WaitFor(() => controller != null && controller.GetNumOtherPlayers() == 0,
        "Host kick did not disconnect the direct client.", 30f);
      if (failed) yield break;
      Debug.Log("DIRECT_PARITY_HOST_PASS");
      Application.Quit(0);
    }

    if (!isHost)
    {
      yield return WaitFor(() => mainSceneLoads >= 2
        && FindObjectOfType<NetworkingController>() != null
        && FindObjectOfType<NetworkingController>().GetIsInMultiplayer()
        && HasPlayer(1, expectedNickname),
        "Direct map switch did not reload the client and restore the host roster.", 90f);
      if (failed) yield break;
      Debug.Log("DIRECT_PARITY_MAP_SWITCH_PASS");
      yield return WaitFor(() => !PhotonNetwork.connected,
        "Host kick did not disconnect the client cleanly.", 45f);
      if (failed) yield break;
      Debug.Log("DIRECT_PARITY_CLIENT_PASS");
      Application.Quit(0);
    }
  }

  static string GetExpectedNickname()
  {
    string nickname = PlayerPrefs.GetString("PlayerNickName");
    if (string.IsNullOrWhiteSpace(nickname)) nickname = "Player";
    nickname = nickname.Trim();
    return nickname.Length <= 32 ? nickname : nickname.Substring(0, 32);
  }

  static bool HasPlayer(int playerId, string nickname)
  {
    PhotonPlayer player = PhotonPlayer.Find(playerId);
    return player != null && player.NickName == nickname;
  }

  IEnumerator WaitFor(Func<bool> condition, string errorMessage, float timeoutSeconds)
  {
    float deadline = Time.realtimeSinceStartup + timeoutSeconds;
    while (!condition() && Time.realtimeSinceStartup < deadline)
      yield return new WaitForSecondsRealtime(0.1f);
    if (!condition()) Fail(errorMessage);
  }

  void Fail(string errorMessage)
  {
    if (failed) return;
    failed = true;
    Debug.LogError("DIRECT_PARITY_FAIL|" + errorMessage);
    Application.Quit(1);
  }
}
