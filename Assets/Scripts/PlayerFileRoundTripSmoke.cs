using System;
using System.IO;
using System.Linq;
using System.Collections;
using CommandTerminal;
using UnityEngine;

public sealed class PlayerFileRoundTripSmoke : MonoBehaviour
{
  static bool launched;

  HeadlessTerminal terminal;
  string expectedSavePath;
  bool saved;
  bool requestedSaveRead;
  bool loadedSavedGame;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    if (launched || Application.isEditor)
    {
      return;
    }

    Application.runInBackground = true;
    launched = true;
    Debug.Log("PLAYER_FILE_ROUNDTRIP_START|scene=" + SceneManager.GetActiveScene().name);
    GameObject runner = new GameObject("PlayerFileRoundTripSmoke");
    DontDestroyOnLoad(runner);
    runner.AddComponent<PlayerFileRoundTripSmoke>();
  }

  void Awake()
  {
    Application.logMessageReceived += OnLogMessage;
  }

  void OnDestroy()
  {
    Application.logMessageReceived -= OnLogMessage;
  }

  void OnLogMessage(string message, string stackTrace, LogType type)
  {
    if (message.Contains("OK saved to ") && expectedSavePath != null && message.Contains(expectedSavePath))
    {
      saved = true;
    }

    if (message.Contains("Loading world from ") && expectedSavePath != null && message.Contains(expectedSavePath))
    {
      requestedSaveRead = true;
    }

    if (message.Contains("Loading saved game!"))
    {
      loadedSavedGame = true;
    }
  }

  IEnumerator Start()
  {
    yield return RunRoundTrip();
  }

  IEnumerator RunRoundTrip()
  {
    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "The splash scene controller did not become available.", 60f);
    if (failed)
    {
      yield break;
    }

    loadedSavedGame = false;
    scenes.RestartAndLoadMinimalScene();
    yield return WaitFor(() => loadedSavedGame,
      "The built-in empty project did not finish loading.", 60f);
    if (failed)
    {
      yield break;
    }

    yield return new WaitForSecondsRealtime(2f);
    yield return WaitFor(() =>
    {
      terminal = FindObjectOfType<HeadlessTerminal>();
      return terminal != null && FindObjectOfType<SaveLoadController>() != null;
    }, "The game console and save controller did not become available.", 60f);
    if (failed)
    {
      yield break;
    }

    VoosEngine engine = FindObjectOfType<VoosEngine>();
    if (engine == null)
    {
      Fail("VOOS engine was not available after loading the built-in project.");
      yield break;
    }

    string[] templateActorNames = engine.GetPersistedState().actors
      .Select(actor => actor.name)
      .OrderBy(name => name, StringComparer.Ordinal)
      .ToArray();
    if (templateActorNames.Length == 0)
    {
      Fail("The built-in project loaded without persisted actors.");
      yield break;
    }

    expectedSavePath = Path.Combine(Path.GetTempPath(), "GameBuilder-PlayerRoundTrip-" + Guid.NewGuid().ToString("N") + ".voos");
    saved = false;
    Issue("save \"" + expectedSavePath + "\"");
    yield return WaitFor(() => saved && File.Exists(expectedSavePath) && new FileInfo(expectedSavePath).Length > 0,
      "The save command did not complete with a non-empty VOOS file.", 60f);
    if (failed)
    {
      yield break;
    }

    SaveLoadController.SaveGame savedGame = default(SaveLoadController.SaveGame);
    try
    {
      savedGame = SaveLoadController.ReadSaveGame(expectedSavePath);
    }
    catch (Exception exception)
    {
      Fail("The saved VOOS file could not be read: " + exception);
    }
    if (failed)
    {
      yield break;
    }

    string[] savedActorNames = savedGame.voosEngineState.actors
      .Select(actor => actor.name)
      .OrderBy(name => name, StringComparer.Ordinal)
      .ToArray();
    if (!templateActorNames.SequenceEqual(savedActorNames, StringComparer.Ordinal))
    {
      Fail("The saved file did not preserve the built-in project's actor identities.");
      yield break;
    }

    loadedSavedGame = false;
    requestedSaveRead = false;
    Issue("load \"" + expectedSavePath + "\"");
    yield return WaitFor(() => requestedSaveRead && loadedSavedGame,
      "The load command did not read the saved project and restart the scene.", 60f);
    if (failed)
    {
      yield break;
    }

    yield return new WaitForSecondsRealtime(2f);
    engine = FindObjectOfType<VoosEngine>();
    if (engine == null)
    {
      Fail("VOOS engine was not available after reloading the saved project.");
      yield break;
    }

    string[] reloadedActorNames = engine.GetPersistedState().actors
      .Select(actor => actor.name)
      .OrderBy(name => name, StringComparer.Ordinal)
      .ToArray();
    if (!savedActorNames.SequenceEqual(reloadedActorNames, StringComparer.Ordinal))
    {
      Fail("Reloading the saved file did not restore the same actor identities.");
      yield break;
    }

    File.Delete(expectedSavePath);
    Debug.Log("PLAYER_FILE_ROUNDTRIP_PASS|templateActors=" + templateActorNames.Length + "|savedActors=" + reloadedActorNames.Length);
    Application.Quit(0);
  }

  void Issue(string command)
  {
    try
    {
      terminal.command_text = command;
      terminal.EnterCommand();
    }
    catch (Exception exception)
    {
      Fail("The console command failed: " + exception);
    }
  }

  IEnumerator WaitFor(Func<bool> condition, string failure, float timeoutSeconds)
  {
    float deadline = Time.realtimeSinceStartup + timeoutSeconds;
    while (!condition() && Time.realtimeSinceStartup < deadline)
    {
      yield return new WaitForSecondsRealtime(0.1f);
    }

    if (!condition())
    {
      Fail(failure);
    }
  }

  void Fail(string message)
  {
    if (failed)
    {
      return;
    }

    failed = true;
    Debug.LogError("PLAYER_FILE_ROUNDTRIP_FAIL|" + message);
    if (expectedSavePath != null && File.Exists(expectedSavePath))
    {
      File.Delete(expectedSavePath);
    }
    Application.Quit(1);
  }
