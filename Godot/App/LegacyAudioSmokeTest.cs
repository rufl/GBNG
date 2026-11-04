using System.IO;
using Godot;
using GBNG.Audio;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunAudioSmoke()
    {
        int exitCode = 0;
        string settingsPath = ProjectSettings.GlobalizePath(EditorSettingsPath);
        byte[]? originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await WaitForUiFrames(2);
            LegacyAudioSettingsWindow window = _audioSettingsWindow;
            if (!IsInstanceValid(window))
                throw new InvalidOperationException("The audio preferences window was not created.");
            OpenAudioSettings();
            await WaitForUiFrames(2);

            var volume = (HSlider)window.FindChild("AudioSfxVolume", true, false);
            var value = (Label)window.FindChild("AudioSfxVolumeValue", true, false);
            var reset = (Button)window.FindChild("AudioResetDefaults", true, false);
            if (!window.Visible
                || !Mathf.IsEqualApprox((float)volume.MinValue, 0f)
                || !Mathf.IsEqualApprox((float)volume.MaxValue, 1f))
                throw new InvalidOperationException("The audio window did not expose the full sound-effects volume range.");

            volume.Value = 0.25;
            var savedPreferences = new ConfigFile();
            int busIndex = AudioServer.GetBusIndex(LegacyAudioSettings.SfxBusName);
            if (!Mathf.IsEqualApprox(_sfxVolume, 0.25f)
                || busIndex < 0
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(busIndex), LegacyAudioSettings.ToDecibels(0.25f))
                || savedPreferences.Load(EditorSettingsPath) != Error.Ok
                || !Mathf.IsEqualApprox((float)savedPreferences.GetValue("audio", "sfx_volume", -1.0).AsDouble(), 0.25f))
                throw new InvalidOperationException("Changing SFX volume did not update the mixer bus and persist the preference.");

            volume.Value = 0.0;
            if (!Mathf.IsEqualApprox(_sfxVolume, 0f)
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(busIndex), -80f)
                || value.Text != "Muted")
                throw new InvalidOperationException("Setting SFX volume to zero did not mute the mixer bus.");

            reset.EmitSignal("pressed");
            await WaitForUiFrames(1);
            var resetPreferences = new ConfigFile();
            if (!Mathf.IsEqualApprox(_sfxVolume, LegacyAudioSettings.DefaultSfxVolume)
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(busIndex), LegacyAudioSettings.ToDecibels(LegacyAudioSettings.DefaultSfxVolume))
                || resetPreferences.Load(EditorSettingsPath) != Error.Ok
                || !Mathf.IsEqualApprox((float)resetPreferences.GetValue("audio", "sfx_volume", -1.0).AsDouble(), LegacyAudioSettings.DefaultSfxVolume))
                throw new InvalidOperationException("Reset to default did not restore and persist the legacy SFX volume.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/audio-preferences-smoke.png");
            if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the audio preferences screenshot.");
            GD.Print($"AUDIO_SMOKE_OK sfxVolume={_sfxVolume:0.00} mute=true persistence=true mixerBus=true reset=true screenshot={screenshot}");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"AUDIO_SMOKE_FAILED: {exception}");
        }
        finally
        {
            try
            {
                if (originalSettings is not null)
                    File.WriteAllBytes(settingsPath, originalSettings);
                else if (File.Exists(settingsPath))
                    File.Delete(settingsPath);
            }
            catch (System.Exception exception)
            {
                exitCode = 1;
                GD.PushError($"Could not restore audio preferences after smoke: {exception}");
            }
            GetTree().Quit(exitCode);
        }
    }
}
