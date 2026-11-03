using Unity.Netcode;
using System;
using System.Collections;
using UnityEngine;

public sealed class DirectHostSmokeBootstrap : MonoBehaviour
{
  IEnumerator Start()
  {
    Debug.Log("DIRECT_HOST_SMOKE|bootstrap-start");
    yield return null;
    string[] args = Environment.GetCommandLineArgs();
    foreach (string arg in args)
    {
      if (arg == "--direct-offline-smoke")
      {
        Debug.Log("DIRECT_HOST_SMOKE|offline-requested");
        StartObserver("offline");
        FindObjectOfType<GameBuilderSceneController>().RestartAndLoadMinimalScene(
          new GameBuilderApplication.GameOptions
          {
            playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = false }
          });
        yield break;
      }

      const string hostPrefix = "--direct-host-smoke=";
      bool hostRequested = arg == "--direct-host-smoke";
      ushort hostPort = 0;
      if (arg.StartsWith(hostPrefix, StringComparison.Ordinal))
      {
        hostRequested = true;
        if (!ushort.TryParse(arg.Substring(hostPrefix.Length), out hostPort))
        {
          Debug.LogError($"Invalid direct host smoke port: {arg}");
          yield break;
        }
      }
      if (hostRequested)
      {
        Debug.Log("DIRECT_HOST_SMOKE|host-requested");
        StartObserver("host");
        FindObjectOfType<GameBuilderSceneController>().RestartAndLoadMinimalScene(
          new GameBuilderApplication.GameOptions
          {
            joinPort = hostPort,
            playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
          });
        yield break;
      }

      const string joinPrefix = "--direct-client-smoke=";
      if (arg.StartsWith(joinPrefix, StringComparison.Ordinal))
      {
        string[] endpoint = arg.Substring(joinPrefix.Length).Split(':');
        if (endpoint.Length != 2 || !ushort.TryParse(endpoint[1], out ushort port))
        {
          Debug.LogError($"Invalid direct client smoke endpoint: {arg}");
          yield break;
        }
        Debug.Log("DIRECT_HOST_SMOKE|client-requested");
        StartObserver("client");
        FindObjectOfType<GameBuilderSceneController>().LoadMainSceneAsync(
          new GameBuilderApplication.GameOptions
          {
            joinAddress = endpoint[0],
            joinPort = port,
            playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
          });
        yield break;
      }
    }
  }
  void StartObserver(string role)
  {
    GameObject observerObject = new GameObject("DirectHostSmokeObserver");
    DontDestroyOnLoad(observerObject);
    DirectHostSmokeObserver observer = observerObject.AddComponent<DirectHostSmokeObserver>();
    observer.role = role;
  }
  }
public sealed class DirectHostSmokeObserver : MonoBehaviour
{
  public string role;

  IEnumerator Start()
  {
    float timeoutAt = Time.realtimeSinceStartup + 90f;
    NetworkManager manager = null;
    UserBody localBody = null;
    UserBody remoteBody = null;
    bool ready = false;
    while (Time.realtimeSinceStartup < timeoutAt)
    {
      manager = NetworkManager.Singleton;
      AutoSaveController autoSave = FindObjectOfType<AutoSaveController>();
      if (autoSave != null) autoSave.SetPaused(true);
      localBody = FindBody(true);
      remoteBody = FindBody(false);
      bool networkReady = role == "offline"
        ? manager == null || !manager.IsListening
        : manager != null && manager.IsListening
          && (role == "host" ? manager.IsHost : manager.IsConnectedClient);
      bool playersReady = role == "offline" ? localBody != null : localBody != null && remoteBody != null;
      if (networkReady && playersReady)
      {
        ready = true;
        break;
      }
      yield return new WaitForSecondsRealtime(0.25f);
    }

    if (!ready)
    {
      bool listening = manager != null && manager.IsListening;
      bool connected = manager != null && manager.IsConnectedClient;
      bool hosting = manager != null && manager.IsHost;
      Debug.Log($"DIRECT_HOST_SMOKE|{role}|fail|listening={listening}|connected={connected}|host={hosting}|local-body={localBody != null}|remote-body={remoteBody != null}");
      QuitSmoke(1);
      yield break;
    }

    if (role == "offline")
    {
      Debug.Log("DIRECT_HOST_SMOKE|offline|pass|no-network-session");
      QuitSmoke(0);
      yield break;
    }

    Vector3 avatarTarget = role == "host" ? new Vector3(5f, 1.5f, 3f) : new Vector3(-5f, 1.5f, 3f);
    Vector3 remoteAvatarTarget = role == "host" ? new Vector3(-5f, 1.5f, 3f) : new Vector3(5f, 1.5f, 3f);
    PhotonView localView = localBody.GetComponent<PhotonView>();
    UserMain localUserMain = FindObjectOfType<UserMain>();
    bool wasUserMainEnabled = localUserMain != null && localUserMain.enabled;
    if (localUserMain != null) localUserMain.enabled = false;
    NavigationControls navigationControls = FindObjectOfType<NavigationControls>();
    bool wereControlsEnabled = navigationControls != null && navigationControls.enabled;
    if (navigationControls != null) navigationControls.enabled = false;
    Transform originalParent = localBody.transform.parent;
    localBody.transform.SetParent(null, true);
    localBody.transform.position = avatarTarget;
    for (int i = 0; i < 10; i++)
    {
      DirectHostRpcRouter.SendUserBodyPose(localView, avatarTarget, Quaternion.identity);
      yield return new WaitForSecondsRealtime(0.1f);
    }
    yield return new WaitForSecondsRealtime(0.5f);
    bool avatarPoseSynced = remoteBody != null
      && Vector3.Distance(remoteBody.transform.position, remoteAvatarTarget) < 0.75f;

    Color playerTint = role == "host" ? Color.cyan : Color.magenta;
    localBody.SetTint(playerTint);
    Animator localAnimator = localBody.GetComponentInChildren<Animator>(true);
    if (localAnimator != null) localAnimator.SetBool("IsSprinting", true);
    yield return new WaitForSecondsRealtime(0.75f);
    Animator remoteAnimator = remoteBody != null ? remoteBody.GetComponentInChildren<Animator>(true) : null;
    Color remotePlayerTint = role == "host" ? Color.magenta : Color.cyan;
    bool playerTintSynced = remoteBody != null && remoteBody.currentTint == remotePlayerTint;
    bool sprintSynced = remoteAnimator != null && remoteAnimator.GetBool("IsSprinting");
    bool playerStateSynced = playerTintSynced && sprintSynced;
    Debug.Log($"DIRECT_HOST_SMOKE|{role}|player-state|local-tint={localBody.currentTint}|expected-remote-tint={remotePlayerTint}|remote-tint={(remoteBody == null ? "none" : remoteBody.currentTint.ToString())}|local-sprint={(localAnimator != null && localAnimator.GetBool("IsSprinting"))}|remote-sprint={(remoteAnimator != null && remoteAnimator.GetBool("IsSprinting"))}");
    Debug.Log($"DIRECT_HOST_SMOKE|{role}|player-state-check|tint={playerTintSynced}|sprint={sprintSynced}");
    TerrainManager terrainManager = FindObjectOfType<TerrainManager>();
    TerrainManager.Cell terrainCell = default;
    bool foundEmptyTerrainCell = false;
    int terrainX = role == "host" ? 0 : 1;
    int remoteTerrainX = role == "host" ? 1 : 0;
    if (terrainManager != null)
    {
      for (int y = 120; y >= 100; y--)
      {
        TerrainManager.Cell candidate = new TerrainManager.Cell(terrainX, y, 0);
        if (terrainManager.GetCellValue(candidate).IsOccupied()) continue;
        terrainCell = candidate;
        foundEmptyTerrainCell = true;
        break;
      }
    }
    if (foundEmptyTerrainCell)
    {
      terrainManager.SetCellValue(terrainCell, new TerrainManager.CellValue(
        TerrainManager.BlockShape.Corner, TerrainManager.BlockDirection.West, TerrainManager.BlockStyle.SolidColor15));
      yield return new WaitForSecondsRealtime(1f);
    }
    bool terrainReplicated = false;
    if (terrainManager != null)
    {
      for (int y = 120; y >= 100; y--)
      {
        TerrainManager.Cell candidate = new TerrainManager.Cell(remoteTerrainX, y, 0);
        TerrainManager.CellValue value = terrainManager.GetCellValue(candidate);
        if (value.blockType != TerrainManager.BlockShape.Corner
          || value.direction != TerrainManager.BlockDirection.West
          || value.style != TerrainManager.BlockStyle.SolidColor15) continue;
        terrainReplicated = true;
        break;
      }
    }


    localBody.transform.SetParent(originalParent, true);
    if (navigationControls != null) navigationControls.enabled = wereControlsEnabled;
    if (localUserMain != null) localUserMain.enabled = wasUserMainEnabled;
    VoosEngine engine = FindObjectOfType<VoosEngine>();
    string localActorName = "DirectSmoke_" + role;
    Vector3 actorStart = role == "host" ? new Vector3(10f, 2f, 0f) : new Vector3(-10f, 2f, 0f);
    Vector3 actorTarget = actorStart + new Vector3(3f, 0f, 0f);
    VoosActor localActor = engine != null
      ? engine.CreateActor(actorStart, Quaternion.identity, actor => { }, localActorName)
      : null;
    yield return new WaitForSecondsRealtime(1.5f);
    if (localActor != null) localActor.SetPosition(actorTarget);

    string remoteActorName = role == "host" ? "DirectSmoke_client" : "DirectSmoke_host";
    Vector3 remoteActorTarget = role == "host" ? new Vector3(-7f, 2f, 0f) : new Vector3(13f, 2f, 0f);
    VoosActor remoteActor = null;
    float actorTimeoutAt = Time.realtimeSinceStartup + 10f;
    while (Time.realtimeSinceStartup < actorTimeoutAt)
    {
      remoteActor = FindRemoteActor(remoteActorName);
      if (remoteActor != null && Vector3.Distance(remoteActor.transform.position, remoteActorTarget) < 0.75f) break;
      yield return new WaitForSecondsRealtime(0.1f);
    }

    bool actorReplicated = remoteActor != null
      && Vector3.Distance(remoteActor.transform.position, remoteActorTarget) < 0.75f;
    yield return new WaitForSecondsRealtime(0.5f);
    bool passed = avatarPoseSynced && playerStateSynced && terrainReplicated && actorReplicated;
    PhotonView remoteView = remoteBody != null ? remoteBody.GetComponent<PhotonView>() : null;
    string remotePosition = remoteBody != null ? remoteBody.transform.position.ToString() : "none";
    Debug.Log($"DIRECT_HOST_SMOKE|{role}|{(passed ? "pass" : "fail")}|avatar-pose={avatarPoseSynced}|player-state={playerStateSynced}|terrain={terrainReplicated}|actor-state={actorReplicated}|local-pos={localBody.transform.position}|remote-pos={remotePosition}|local-view={localView.viewID}/{localView.ownerId}|remote-view={(remoteView == null ? "none" : remoteView.viewID + "/" + remoteView.ownerId)}");
    yield return new WaitForSecondsRealtime(25f);
    if (localActor != null && engine != null) engine.DestroyActor(localActor);
    QuitSmoke(passed ? 0 : 1);
  }

  static UserBody FindBody(bool local)
  {
    foreach (UserBody body in FindObjectsOfType<UserBody>(true))
      if (body.GetIsLocalPlayer() == local) return body;
    return null;
  }

  static VoosActor FindRemoteActor(string name)
  {
    foreach (VoosActor actor in FindObjectsOfType<VoosActor>(true))
    {
      if (actor.GetName() != name) continue;
      PhotonView view = actor.GetComponent<PhotonView>();
      if (view != null && !view.isMine) return actor;
    }
    return null;
  }
  static void QuitSmoke(int exitCode)
  {
    UserMain userMain = FindObjectOfType<UserMain>();
    if (userMain != null) Application.wantsToQuit -= userMain.WantsToQuit;
    Application.Quit(exitCode);
  }
}
