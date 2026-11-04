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

            var sfxVolume = (HSlider)window.FindChild("AudioSfxVolume", true, false);
            var sfxValue = (Label)window.FindChild("AudioSfxVolumeValue", true, false);
            var musicVolume = (HSlider)window.FindChild("AudioMusicVolume", true, false);
            var musicValue = (Label)window.FindChild("AudioMusicVolumeValue", true, false);
            var reset = (Button)window.FindChild("AudioResetDefaults", true, false);
            if (!window.Visible
                || !Mathf.IsEqualApprox((float)sfxVolume.MinValue, 0f)
                || !Mathf.IsEqualApprox((float)sfxVolume.MaxValue, 1f)
                || !Mathf.IsEqualApprox((float)musicVolume.MinValue, 0f)
                || !Mathf.IsEqualApprox((float)musicVolume.MaxValue, 1f))
                throw new InvalidOperationException("The audio window did not expose full sound-effects and music volume ranges.");

            sfxVolume.Value = 0.25;
            int sfxBusIndex = AudioServer.GetBusIndex(LegacyAudioSettings.SfxBusName);
            var savedPreferences = new ConfigFile();
            if (!Mathf.IsEqualApprox(_sfxVolume, 0.25f)
                || sfxBusIndex < 0
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(sfxBusIndex), LegacyAudioSettings.ToDecibels(0.25f))
                || savedPreferences.Load(EditorSettingsPath) != Error.Ok
                || !Mathf.IsEqualApprox((float)savedPreferences.GetValue("audio", "sfx_volume", -1.0).AsDouble(), 0.25f))
                throw new InvalidOperationException("Changing SFX volume did not update its mixer bus and persist the preference.");

            musicVolume.Value = 0.25;
            int musicBusIndex = AudioServer.GetBusIndex(LegacyAudioSettings.MusicBusName);
            savedPreferences = new ConfigFile();
            if (!Mathf.IsEqualApprox(_musicVolume, 0.25f)
                || musicBusIndex < 0
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(musicBusIndex), LegacyAudioSettings.ToDecibels(0.25f))
                || savedPreferences.Load(EditorSettingsPath) != Error.Ok
                || !Mathf.IsEqualApprox((float)savedPreferences.GetValue("audio", "music_volume", -1.0).AsDouble(), 0.25f))
                throw new InvalidOperationException("Changing music volume did not update its mixer bus and persist the preference.");

            await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
            var firstMusicPlayer = (AudioStreamPlayer)FindChild("MusicPlayerA", true, false);
            var secondMusicPlayer = (AudioStreamPlayer)FindChild("MusicPlayerB", true, false);
            AudioStreamPlayer activeMusicPlayer = firstMusicPlayer.Playing
                ? firstMusicPlayer
                : secondMusicPlayer;
            if ((!firstMusicPlayer.Playing && !secondMusicPlayer.Playing)
                || activeMusicPlayer.Stream is null
                || activeMusicPlayer.GetPlaybackPosition() <= 0d)
                throw new InvalidOperationException("The original adaptive music tracks did not start playback.");
            AudioStream activeMusicStream = activeMusicPlayer.Stream
                ?? throw new InvalidOperationException("The active legacy music stream was not loaded.");
            double trackDuration = activeMusicStream.GetLength();
            if (trackDuration <= 0d)
                throw new InvalidOperationException("The active legacy music stream has no duration.");
            double untilNextTrack = Math.Max(0.25d, trackDuration - activeMusicPlayer.GetPlaybackPosition() + 0.25d);
            await ToSignal(GetTree().CreateTimer(untilNextTrack), SceneTreeTimer.SignalName.Timeout);
            AudioStreamPlayer nextMusicPlayer = ReferenceEquals(activeMusicPlayer, firstMusicPlayer)
                ? secondMusicPlayer
                : firstMusicPlayer;
            if (!nextMusicPlayer.Playing
                || nextMusicPlayer.Stream is null
                || nextMusicPlayer.GetPlaybackPosition() <= 0d)
                throw new InvalidOperationException("Background music did not advance to its next adaptive track.");

            musicVolume.Value = 0.0;
            if (!Mathf.IsEqualApprox(_musicVolume, 0f)
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(musicBusIndex), -80f)
                || musicValue.Text != "Muted")
                throw new InvalidOperationException("Setting music volume to zero did not mute its mixer bus.");

            sfxVolume.Value = 0.0;
            if (!Mathf.IsEqualApprox(_sfxVolume, 0f)
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(sfxBusIndex), -80f)
                || sfxValue.Text != "Muted")
                throw new InvalidOperationException("Setting SFX volume to zero did not mute its mixer bus.");

            reset.EmitSignal("pressed");
            await WaitForUiFrames(1);
            var resetPreferences = new ConfigFile();
            if (!Mathf.IsEqualApprox(_sfxVolume, LegacyAudioSettings.DefaultSfxVolume)
                || !Mathf.IsEqualApprox(_musicVolume, LegacyAudioSettings.DefaultMusicVolume)
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(sfxBusIndex), LegacyAudioSettings.ToDecibels(LegacyAudioSettings.DefaultSfxVolume))
                || !Mathf.IsEqualApprox(AudioServer.GetBusVolumeDb(musicBusIndex), LegacyAudioSettings.ToDecibels(LegacyAudioSettings.DefaultMusicVolume))
                || resetPreferences.Load(EditorSettingsPath) != Error.Ok
                || !Mathf.IsEqualApprox((float)resetPreferences.GetValue("audio", "sfx_volume", -1.0).AsDouble(), LegacyAudioSettings.DefaultSfxVolume)
                || !Mathf.IsEqualApprox((float)resetPreferences.GetValue("audio", "music_volume", -1.0).AsDouble(), LegacyAudioSettings.DefaultMusicVolume))
                throw new InvalidOperationException("Reset to default did not restore and persist both audio volume settings.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/audio-preferences-smoke.png");
            if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the audio preferences screenshot.");
            GD.Print($"AUDIO_SMOKE_OK sfxVolume={_sfxVolume:0.00} musicVolume={_musicVolume:0.00} musicPlayback=true musicTransition=true mute=true persistence=true mixerBuses=true reset=true screenshot={screenshot}");
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
