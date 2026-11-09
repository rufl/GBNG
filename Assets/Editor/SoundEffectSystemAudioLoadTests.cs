#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public class SoundEffectSystemAudioLoadTests
{
  [UnityTest]
  public IEnumerator SuccessfulWavRequestCachesDecodedClip()
  {
    GameObject host = new GameObject("Sound effect load test");
    host.SetActive(false);
    try
    {
      SoundEffectSystem system = host.AddComponent<SoundEffectSystem>();
      SoundEffect effect = NewEffect("successful-load");
      string path = Path.GetFullPath(Path.Combine(Application.dataPath, "SFX", "UI", "Button_Click_V1.wav"));
      Assert.IsTrue(File.Exists(path), "Missing WAV test fixture: " + path);

      IEnumerator load = StartLoad(system, effect, path);
      Assert.IsTrue(load.MoveNext());
      UnityWebRequestAsyncOperation operation = load.Current as UnityWebRequestAsyncOperation;
      Assert.IsNotNull(operation);
      yield return operation;
      Assert.IsFalse(load.MoveNext());

      InvokeUpdate(system);
      Assert.IsNotNull(system.GetAudioClip(effect.id));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(host);
    }
  }

  [UnityTest]
  public IEnumerator HttpFailureIsNotCachedAsAnAudioClip()
  {
    using (UnityTestHttpServer server = new UnityTestHttpServer(500))
    {
      server.Start();
      GameObject host = new GameObject("Failed sound effect load test");
      host.SetActive(false);
      try
      {
        SoundEffectSystem system = host.AddComponent<SoundEffectSystem>();
        SoundEffect effect = NewEffect("failed-http-load");
        LogAssert.Expect(LogType.Error, new Regex("Failed to load sound at .*"));

        IEnumerator load = StartLoad(system, effect, server.Url);
        Assert.IsTrue(load.MoveNext());
        UnityWebRequestAsyncOperation operation = load.Current as UnityWebRequestAsyncOperation;
        Assert.IsNotNull(operation);
        for (int frame = 0; frame < 120 && !server.RequestReceived; frame++) yield return null;
        Assert.IsTrue(server.RequestReceived, "The local endpoint did not receive the audio request.");
        server.AllowResponse();

        yield return operation;
        Assert.IsFalse(load.MoveNext());
        InvokeUpdate(system);

        Dictionary<string, AudioClip> cache = (Dictionary<string, AudioClip>)typeof(SoundEffectSystem)
          .GetField("audioClipCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(system);
        Assert.IsFalse(cache.ContainsKey(effect.id), "An HTTP error must not be cached as a successful audio load.");
        for (int frame = 0; frame < 120 && !server.Finished; frame++) yield return null;
        Assert.IsTrue(server.Succeeded);
      }
      finally
      {
        server.AllowResponse();
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
      }
    }
  }

  static IEnumerator StartLoad(SoundEffectSystem system, SoundEffect effect, string path)
  {
    MethodInfo load = typeof(SoundEffectSystem).GetMethod("LoadAudioClip", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.IsNotNull(load);
    return (IEnumerator)load.Invoke(system, new object[] { effect, path, AudioType.WAV });
  }

  static SoundEffect NewEffect(string id)
  {
    return new SoundEffect(id, id, SoundEffectContent.NewWithSteamWorkshopId(1));
  }

  static void InvokeUpdate(SoundEffectSystem system)
  {
    typeof(SoundEffectSystem).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(system, null);
  }
}
#endif
