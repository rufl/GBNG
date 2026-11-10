#if !USE_STEAMWORKS
using System;
using System.Collections;
using System.IO;
using GameBuilder;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DirectWorkshopAssetTransferSmoke : MonoBehaviour
{
  const string RoleArgumentPrefix = "--direct-workshop-asset-smoke=";
  const string CoordinationArgumentPrefix = "--direct-workshop-asset-smoke-coordination=";
  const string PortArgumentPrefix = "--direct-workshop-asset-smoke-port=";
  const string Marker = "DIRECT_WORKSHOP_ASSET_TRANSFER";
  const string Payload = "direct workshop asset payload";
  const string NestedPayload = "nested direct workshop asset payload";
  const int ChunkPayloadLength = 128 * 1024;
  const int DirectAssetChunkBytes = 48 * 1024;

  static bool launched;

  string role;
  string coordinationPath;
  ushort port;
  bool completed;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    if (launched || Application.isEditor) return;

    string requestedRole = GetArgument(RoleArgumentPrefix);
    if (requestedRole != "host" && requestedRole != "client") return;

    string requestedCoordinationPath = GetArgument(CoordinationArgumentPrefix);
    if (string.IsNullOrWhiteSpace(requestedCoordinationPath))
    {
      Debug.LogError(Marker + "_FAIL|The coordination path argument is required.");
      return;
    }

    ushort requestedPort;
    if (!ushort.TryParse(GetArgument(PortArgumentPrefix), out requestedPort) || requestedPort == 0)
    {
      Debug.LogError(Marker + "_FAIL|A non-zero UDP port argument is required.");
      return;
    }

    launched = true;
    Application.runInBackground = true;
    GameObject runner = new GameObject("DirectWorkshopAssetTransferSmoke");
    DontDestroyOnLoad(runner);
    DirectWorkshopAssetTransferSmoke smoke = runner.AddComponent<DirectWorkshopAssetTransferSmoke>();
    smoke.role = requestedRole;
    smoke.coordinationPath = Path.GetFullPath(requestedCoordinationPath);
    smoke.port = requestedPort;
    Debug.Log(Marker + "_START|role=" + requestedRole + "|scene=" + SceneManager.GetActiveScene().name);
  }

  static string GetArgument(string prefix)
  {
    foreach (string argument in Environment.GetCommandLineArgs())
      if (argument.StartsWith(prefix, StringComparison.Ordinal)) return argument.Substring(prefix.Length);
    return null;
  }

  IEnumerator Start()
  {
    if (role == "host") yield return RunHost();
    else yield return RunClient();
  }

  IEnumerator RunHost()
  {
    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (completed) yield break;

    scenes.RestartAndLoadMinimalScene(new GameBuilderApplication.GameOptions
    {
      displayName = "Direct workshop asset smoke",
      joinPort = port,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    });

    WorkshopAssetSource source = null;
    NetworkingController networking = null;
    yield return WaitFor(() =>
    {
      source = FindObjectOfType<WorkshopAssetSource>();
      networking = FindObjectOfType<NetworkingController>();
      return source != null && networking != null && networking.IsDirectHost();
    }, "The direct host did not start.", 60f);
    if (completed) yield break;

    string contentDirectory = Path.Combine(Path.GetTempPath(), "GameBuilder-DirectWorkshopAsset-" + Guid.NewGuid().ToString("N"));
    try
    {
      Directory.CreateDirectory(Path.Combine(contentDirectory, "nested"));
      File.WriteAllText(Path.Combine(contentDirectory, "payload.txt"), Payload);
      File.WriteAllText(Path.Combine(contentDirectory, "nested", "payload.txt"), NestedPayload);
      File.WriteAllBytes(Path.Combine(contentDirectory, "payload.bin"), CreateChunkPayload());
    }
    catch (Exception exception)
    {
      DeleteDirectory(contentDirectory);
      Fail("The host could not prepare its workshop asset: " + exception.Message);
      yield break;
    }

    bool putCompleted = false;
    Util.Maybe<ulong> assetResult = default(Util.Maybe<ulong>);
    try
    {
      source.Put(contentDirectory, "Direct asset smoke", "", SteamUtil.GameBuilderTags.FBX, result =>
      {
        assetResult = result;
        putCompleted = true;
      });
    }
    catch (Exception exception)
    {
      DeleteDirectory(contentDirectory);
      Fail("The host could not create its local workshop asset: " + exception.Message);
      yield break;
    }

    yield return WaitFor(() => putCompleted, "The host did not create its local workshop asset.", 30f);
    DeleteDirectory(contentDirectory);
    if (completed) yield break;
    if (assetResult.IsEmpty())
    {
      Fail("The host could not create its local workshop asset: " + assetResult.GetErrorMessage());
      yield break;
    }

    int archiveLength;
    try
    {
      byte[] archive;
      string error;
      if (!source.TryCreateDirectAssetArchive(assetResult.Value, out archive, out error))
      {
        Fail("The host could not archive its local workshop asset: " + error);
        yield break;
      }
      archiveLength = archive.Length;
      if (archiveLength <= DirectAssetChunkBytes)
      {
        Fail("The direct workshop asset archive did not exceed one transport chunk.");
        yield break;
      }
      WriteCoordinator(assetResult.Value, DirectHostTls.GetHostCredentials().CertificatePem);
    }
    catch (Exception exception)
    {
      Fail("The host could not publish connection details: " + exception.Message);
      yield break;
    }

    string clientPassPath = coordinationPath + ".client-pass";
    string clientFailPath = coordinationPath + ".client-fail";
    yield return WaitFor(() => File.Exists(clientPassPath) || File.Exists(clientFailPath),
      "The client did not finish its direct workshop asset request.", 90f);
    if (completed) yield break;

    if (File.Exists(clientFailPath))
    {
      string failure = File.ReadAllText(clientFailPath);
      Fail("The client reported failure: " + failure);
      yield break;
    }

    DeleteCoordinationFiles();
    Pass("role=host|archiveBytes=" + archiveLength);
  }

  IEnumerator RunClient()
  {
    yield return WaitFor(() => File.Exists(coordinationPath), "The host did not publish connection details.", 60f);
    if (completed) yield break;

    ulong assetId;
    string certificate;
    if (!TryReadCoordinator(out assetId, out certificate))
    {
      Fail("The host wrote invalid connection details.");
      yield break;
    }

    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (completed) yield break;

    scenes.RestartAndLoadMinimalScene(new GameBuilderApplication.GameOptions
    {
      joinAddress = "127.0.0.1",
      joinPort = port,
      trustedHostCertificatePem = certificate,
      playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
    });

    WorkshopAssetSource source = null;
    NetworkingController networking = null;
    yield return WaitFor(() =>
    {
      source = FindObjectOfType<WorkshopAssetSource>();
      networking = FindObjectOfType<NetworkingController>();
      return source != null && networking != null && networking.GetIsInMultiplayer()
        && PhotonNetwork.player != null && PhotonNetwork.player.ID > 1
        && PhotonNetwork.HasDirectPlayerIdentity(1);
    }, "The direct client did not complete its identity handshake.", 60f);
    if (completed) yield break;

    bool getCompleted = false;
    Util.Maybe<string> pathResult = default(Util.Maybe<string>);
    source.Get(assetId, result =>
    {
      pathResult = result;
      getCompleted = true;
    });
    yield return WaitFor(() => getCompleted, "The host did not answer the workshop asset request.", 60f);
    if (completed) yield break;
    if (pathResult.IsEmpty())
    {
      Fail("The client did not receive the workshop asset: " + pathResult.GetErrorMessage());
      yield break;
    }

    try
    {
      string assetDirectory = pathResult.Value;
      if (File.ReadAllText(Path.Combine(assetDirectory, "payload.txt")) != Payload
        || File.ReadAllText(Path.Combine(assetDirectory, "nested", "payload.txt")) != NestedPayload
        || !HasExpectedChunkPayload(Path.Combine(assetDirectory, "payload.bin")))
      {
        Fail("The received workshop asset content did not match the host source.");
        yield break;
      }
    }
    catch (Exception exception)
    {
      Fail("The client could not read the received workshop asset: " + exception.Message);
      yield break;
    }

    File.WriteAllText(coordinationPath + ".client-pass", "ok");
    Pass("role=client|assetId=" + assetId);
  }

  void WriteCoordinator(ulong assetId, string certificate)
  {
    string directory = Path.GetDirectoryName(coordinationPath);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    string temporaryPath = coordinationPath + ".tmp";
    File.WriteAllLines(temporaryPath, new[]
    {
      port.ToString(),
      assetId.ToString(),
      Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(certificate))
    });
    if (File.Exists(coordinationPath)) File.Delete(coordinationPath);
    File.Move(temporaryPath, coordinationPath);
  }

  bool TryReadCoordinator(out ulong assetId, out string certificate)
  {
    assetId = 0;
    certificate = null;
    try
    {
      string[] lines = File.ReadAllLines(coordinationPath);
      if (lines.Length != 3 || !ushort.TryParse(lines[0], out ushort storedPort) || storedPort != port
        || !ulong.TryParse(lines[1], out assetId)) return false;
      certificate = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]));
      return DirectHostTls.IsUsablePinnedCertificate(certificate);
    }
    catch (Exception)
    {
      return false;
    }
  }

  static byte[] CreateChunkPayload()
  {
    byte[] payload = new byte[ChunkPayloadLength];
    uint state = 0x1f123bb5;
    for (int index = 0; index < payload.Length; index++)
    {
      state = state * 1664525u + 1013904223u;
      payload[index] = (byte)(state >> 24);
    }
    return payload;
  }

  static bool HasExpectedChunkPayload(string path)
  {
    byte[] actual = File.ReadAllBytes(path);
    byte[] expected = CreateChunkPayload();
    if (actual.Length != expected.Length) return false;
    for (int index = 0; index < expected.Length; index++)
      if (actual[index] != expected[index]) return false;
    return true;
  }

  IEnumerator WaitFor(Func<bool> condition, string failureMessage, float timeoutSeconds)
  {
    float deadline = Time.realtimeSinceStartup + timeoutSeconds;
    while (!condition() && Time.realtimeSinceStartup < deadline)
      yield return new WaitForSecondsRealtime(0.1f);
    if (!condition()) Fail(failureMessage);
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
    if (role == "client" && !string.IsNullOrEmpty(coordinationPath))
    {
      try { File.WriteAllText(coordinationPath + ".client-fail", message); }
      catch (Exception) { }
    }
    Debug.LogError(Marker + "_FAIL|role=" + role + "|" + message);
    StartCoroutine(QuitSoon());
  }

  IEnumerator QuitSoon()
  {
    yield return new WaitForSecondsRealtime(0.5f);
    Application.Quit();
  }

  void DeleteDirectory(string path)
  {
    try
    {
      if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    catch (Exception) { }
  }

  void DeleteCoordinationFiles()
  {
    string[] paths = { coordinationPath, coordinationPath + ".tmp", coordinationPath + ".client-pass", coordinationPath + ".client-fail" };
    foreach (string path in paths)
      if (File.Exists(path)) File.Delete(path);
  }
}
#endif
