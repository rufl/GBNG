using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class PlayerLocalFilePickerSmoke : MonoBehaviour
{
  const string SmokeArgument = "--player-local-file-picker-smoke";
  const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
  static bool launched;

  string tempRoot;
  bool failed;

  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
  static void Bootstrap()
  {
    if (launched || Application.isEditor || !Environment.GetCommandLineArgs().Contains(SmokeArgument)) return;
    launched = true;
    Application.runInBackground = true;
    GameObject runner = new GameObject("PlayerLocalFilePickerSmoke");
    DontDestroyOnLoad(runner);
    runner.AddComponent<PlayerLocalFilePickerSmoke>();
  }

  IEnumerator Start()
  {
    tempRoot = Path.Combine(Path.GetTempPath(), "GameBuilder-LocalPickerSmoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    Directory.CreateDirectory(Path.Combine(tempRoot, "nested"));
    string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
    string soundAName = "PickerSmokeA-" + suffix;
    string soundBName = "PickerSmokeB-" + suffix;
    string soundAPath = Path.Combine(tempRoot, soundAName + ".wav");
    string soundBPath = Path.Combine(tempRoot, soundBName + ".wav");
    WriteWav(soundAPath, 440f);
    WriteWav(soundBPath, 660f);
    File.WriteAllText(Path.Combine(tempRoot, "ignored.txt"), "not an audio file");
    string existingSave = Path.Combine(tempRoot, "existing.voos");
    byte[] originalSaveBytes = System.Text.Encoding.UTF8.GetBytes("preserve-existing-save");
    File.WriteAllBytes(existingSave, originalSaveBytes);

    GameBuilderSceneController scenes = null;
    yield return WaitFor(() => (scenes = FindObjectOfType<GameBuilderSceneController>()) != null,
      "Splash scene controller did not become available.", 60f);
    if (failed) yield break;

    scenes.RestartAndLoadMinimalScene();
    yield return WaitFor(() => FindObjectOfType<VoosEngine>() != null &&
      FindObjectOfType<CreationLibrarySounds>(true) != null &&
      FindObjectOfType<SoundEffectSystem>() != null,
      "Minimal game scene, sound library, or sound system did not become available.", 60f);
    if (failed) yield break;

    CreationLibrarySounds sounds = FindObjectOfType<CreationLibrarySounds>(true);
    SoundEffectSystem soundSystem = FindObjectOfType<SoundEffectSystem>();
    LocalFilePicker.OpenFiles("Import sound", null, new[] { ".wav", ".ogg" }, true, paths => { });
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    try
    {
      Invoke(picker, "NavigateToPath", tempRoot);
    }
    catch (Exception exception)
    {
      Fail("Could not open the batch sound picker: " + exception);
      yield break;
    }

    Debug.Log("PLAYER_LOCAL_PICKER_DIALOG_READY|mode=multi-audio|files=2");
    yield return new WaitForSecondsRealtime(30f);

    try
    {
      Invoke(picker, "CancelCurrent");
      sounds.Open();
      Invoke(sounds, "OnImportButtonClicked");
      picker = FindObjectOfType<LocalFilePicker>(true);
      Invoke(picker, "NavigateToPath", tempRoot);
      Invoke(picker, "OnEntryClicked", soundAPath, false);
      Invoke(picker, "OnEntryClicked", soundBPath, false);
      List<string> selectedAudio = GetField<List<string>>(picker, "selectedFiles");
      if (selectedAudio.Count != 2 || !selectedAudio.Contains(soundAPath) || !selectedAudio.Contains(soundBPath))
      {
        throw new InvalidOperationException("Multi-select did not retain both WAV files.");
      }
      Invoke(picker, "ConfirmSelection");
    }
    catch (Exception exception)
    {
      Fail("Batch sound selection failed: " + exception);
      yield break;
    }

    yield return WaitFor(() =>
    {
      SoundEffect a = soundSystem.GetSoundEffectByName(Path.GetFileNameWithoutExtension(soundAPath));
      SoundEffect b = soundSystem.GetSoundEffectByName(Path.GetFileNameWithoutExtension(soundBPath));
      return a != null && b != null && soundSystem.GetAudioClip(a.id) != null && soundSystem.GetAudioClip(b.id) != null;
    }, "Batch sound import did not store and load both local WAV files.", 60f);
    if (failed) yield break;
    Debug.Log("PLAYER_LOCAL_PICKER_BATCH_AUDIO_PASS|externalWorkshopPublish=false|count=2");

    string failure = null;
    try
    {
      TestOpenSingleVoos(existingSave);
      TestFolderSelection(tempRoot);
      TestSaveOverwriteCancel(existingSave, originalSaveBytes);
      TestSaveOverwriteAccept(existingSave);
      TestSaveExtension(tempRoot);
    }
    catch (Exception exception)
    {
      failure = exception.ToString();
    }
    if (failure != null)
    {
      Fail("Local file picker behavior failed: " + failure);
      yield break;
    }

    Debug.Log("PLAYER_LOCAL_PICKER_PASS|openVoos=true|folder=true|overwriteCancelHash=true|overwriteAccept=true|extension=true|multiSelect=true");
    Cleanup();
    Application.Quit();
  }

  void TestOpenSingleVoos(string path)
  {
    string selected = null;
    LocalFilePicker.OpenFiles("Load scene", tempRoot, new[] { ".voos" }, false, paths =>
    {
      if (paths != null && paths.Length == 1) selected = paths[0];
    });
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    Invoke(picker, "OnEntryClicked", path, false);
    Invoke(picker, "ConfirmSelection");
    Require(string.Equals(selected, path, StringComparison.Ordinal), "Single .voos selection returned an unexpected path.");
  }

  void TestFolderSelection(string directory)
  {
    string selected = null;
    LocalFilePicker.OpenFolder("Select save location", directory, path => selected = path);
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    string nested = Path.Combine(directory, "nested");
    Invoke(picker, "NavigateToPath", nested);
    Invoke(picker, "ConfirmSelection");
    Require(string.Equals(selected, nested, StringComparison.Ordinal), "Folder selection returned an unexpected path.");
  }

  void TestSaveOverwriteCancel(string path, byte[] expectedBytes)
  {
    string selected = null;
    LocalFilePicker.SaveFile("Save scene", tempRoot, Path.GetFileName(path), ".voos", value => selected = value);
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    Invoke(picker, "SaveSelection");
    GameObject canvas = GetField<GameObject>(picker, "canvasObject");
    Button cancel = canvas.transform.Find("Overwrite Confirmation/Keep Existing").GetComponent<Button>();
    cancel.onClick.Invoke();
    Require(selected == null, "Overwrite cancellation unexpectedly returned a save path.");
    Require(File.ReadAllBytes(path).SequenceEqual(expectedBytes), "Overwrite cancellation changed the existing file bytes.");
    TMP_Text status = GetField<TMP_Text>(picker, "statusText");
    Require(status.text.Contains("Save cancelled"), "Overwrite cancellation did not report the cancelled save.");
  }

  void TestSaveOverwriteAccept(string path)
  {
    string selected = null;
    LocalFilePicker.SaveFile("Save scene", tempRoot, Path.GetFileName(path), ".voos", value => selected = value);
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    Invoke(picker, "SaveSelection");
    GameObject canvas = GetField<GameObject>(picker, "canvasObject");
    canvas.transform.Find("Overwrite Confirmation/Overwrite").GetComponent<Button>().onClick.Invoke();
    Require(string.Equals(selected, path, StringComparison.Ordinal), "Overwrite confirmation returned an unexpected save path.");
  }

  void TestSaveExtension(string directory)
  {
    string selected = null;
    LocalFilePicker.SaveFile("Save scene", directory, "new-project", ".voos", value => selected = value);
    LocalFilePicker picker = FindObjectOfType<LocalFilePicker>(true);
    Invoke(picker, "SaveSelection");
    Require(string.Equals(selected, Path.Combine(directory, "new-project.voos"), StringComparison.Ordinal),
      "Save picker did not append the .voos extension.");
  }

  IEnumerator WaitFor(Func<bool> condition, string message, float timeoutSeconds)
  {
    float deadline = Time.realtimeSinceStartup + timeoutSeconds;
    while (!condition() && Time.realtimeSinceStartup < deadline) yield return new WaitForSecondsRealtime(0.1f);
    if (!condition()) Fail(message);
  }

  static void Invoke(object target, string methodName, params object[] args)
  {
    MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
    if (method == null) throw new MissingMethodException(target.GetType().Name, methodName);
    method.Invoke(target, args);
  }

  static T GetField<T>(object target, string fieldName)
  {
    FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
    if (field == null) throw new MissingFieldException(target.GetType().Name, fieldName);
    return (T)field.GetValue(target);
  }

  static void Require(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  void Fail(string message)
  {
    if (failed) return;
    failed = true;
    Debug.LogError("PLAYER_LOCAL_PICKER_FAIL|" + message);
    Cleanup();
    Application.Quit();
  }

  void Cleanup()
  {
    if (!string.IsNullOrEmpty(tempRoot) && Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
  }

  static void WriteWav(string path, float frequency)
  {
    const int sampleRate = 22050;
    const short channels = 1;
    const short bitsPerSample = 16;
    int sampleCount = sampleRate / 5;
    int dataBytes = sampleCount * channels * bitsPerSample / 8;
    using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
    {
      writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
      writer.Write(36 + dataBytes);
      writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
      writer.Write(16);
      writer.Write((short)1);
      writer.Write(channels);
      writer.Write(sampleRate);
      writer.Write(sampleRate * channels * bitsPerSample / 8);
      writer.Write((short)(channels * bitsPerSample / 8));
      writer.Write(bitsPerSample);
      writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
      writer.Write(dataBytes);
      for (int i = 0; i < sampleCount; i++)
      {
        short sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.1);
        writer.Write(sample);
      }
    }
  }
}
