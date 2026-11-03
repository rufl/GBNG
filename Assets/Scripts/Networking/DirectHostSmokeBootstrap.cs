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
      if (arg == "--direct-host-smoke")
      {
        Debug.Log("DIRECT_HOST_SMOKE|host-requested");
        FindObjectOfType<GameBuilderSceneController>().RestartAndLoadMinimalScene(
          new GameBuilderApplication.GameOptions
          {
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
}
