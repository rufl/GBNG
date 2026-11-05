using System.Text.Json.Nodes;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunSynthSoundSmoke()
    {
        int exitCode = 0;
        LegacySaveDocument document = _document ?? throw new InvalidOperationException("The synthesized sound smoke project did not load.");
        bool hadSoundDatabase = document.Root.ContainsKey("sojoDatabase");
        JsonNode? originalSoundDatabase = document.Root["sojoDatabase"]?.DeepClone();
        string? temporaryProjectPath = null;
        try
        {
            await WaitForUiFrames(2);
            _soundEffectWindow.RefreshFromDocument();
            _soundEffectWindow.PopupCenteredClamped(new Vector2I(1040, 760));
            await WaitForUiFrames(2);
            if (!_soundEffectWindow.Visible)
                throw new InvalidOperationException("The synthesized sound editor did not open.");

            var newButton = (Button)_soundEffectWindow.FindChild("SynthNewButton", true, false);
            var nameField = (LineEdit)_soundEffectWindow.FindChild("SynthSoundName", true, false);
            var waveShape = (OptionButton)_soundEffectWindow.FindChild("SynthWaveShape", true, false);
            var speed = (HSlider)_soundEffectWindow.FindChild("SynthPlaybackSpeed", true, false);
            var spatialized = (CheckBox)_soundEffectWindow.FindChild("SynthSpatialized", true, false);
            var volumeEnvelope = (LegacySynthEnvelopeEditor)_soundEffectWindow.FindChild("SynthVolumeEnvelope", true, false);
            var pitchEnvelope = (LegacySynthEnvelopeEditor)_soundEffectWindow.FindChild("SynthPitchEnvelope", true, false);
            var previewButton = (Button)_soundEffectWindow.FindChild("SynthPreviewButton", true, false);
            var saveButton = (Button)_soundEffectWindow.FindChild("SynthSaveButton", true, false);
            newButton.EmitSignal(Button.SignalName.Pressed);
            nameField.Text = "Synth authoring smoke";
            waveShape.Select(3);
            speed.Value = 16;
            spatialized.ButtonPressed = false;
            volumeEnvelope.SetSliceValue(0, 7);
            pitchEnvelope.SetSliceValue(31, 40);
            previewButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(2);
            var preview = (AudioStreamPlayer)_soundEffectWindow.FindChild("SynthSoundPreview", true, false);
            if (!preview.Playing || preview.Bus != GBNG.Audio.LegacyAudioSettings.SfxBusName
                || preview.Stream is not AudioStreamWav { Data.Length: > 0 })
                throw new InvalidOperationException("The draft synthesized sound could not be previewed on the SFX bus.");

            int historyPositionBeforeSave = _historyPosition;
            saveButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(2);
            if (_historyPosition != historyPositionBeforeSave + 1)
                throw new InvalidOperationException("Saving a synthesized sound was not recorded in the editor's Undo history.");
            UndoEdit();
            if (FindSynthSound(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Synth authoring smoke") is not null)
                throw new InvalidOperationException("Undo did not remove the newly created synthesized sound.");
            RedoEdit();
            JsonObject? savedSound = FindSynthSound(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Synth authoring smoke");
            if (savedSound is null)
                throw new InvalidOperationException("Redo did not restore the newly created synthesized sound.");
            string savedId = LegacySaveDocument.ReadString(savedSound["id"]);
            if (savedId.Length == 0)
                throw new InvalidOperationException("The saved synthesized sound has no stable ID.");
            JsonObject content = JsonNode.Parse(LegacySaveDocument.ReadString(savedSound["content"]))?.AsObject()
                ?? throw new InvalidDataException("The saved synthesized sound has invalid content.");
            JsonObject synth = content["synthParams"]?.AsObject()
                ?? throw new InvalidDataException("The saved synthesized sound has no synth parameters.");
            JsonArray volumes = synth["volume"]?.AsArray() ?? throw new InvalidDataException("The saved sound has no volume envelope.");
            JsonArray pitches = synth["pitch"]?.AsArray() ?? throw new InvalidDataException("The saved sound has no pitch envelope.");
            if (LegacySaveDocument.ReadInt(content["effectType"], -1) != 0
                || LegacySaveDocument.ReadInt(synth["waveShape"], -1) != 3
                || LegacySaveDocument.ReadInt(synth["speed"], -1) != 16
                || LegacySaveDocument.ReadBool(content["spatialized"], true)
                || volumes.Count != 32 || LegacySaveDocument.ReadInt(volumes[0], -1) != 7
                || pitches.Count != 32 || LegacySaveDocument.ReadInt(pitches[31], -1) != 40)
                throw new InvalidOperationException("The synthesized sound editor did not preserve the selected waveform and 32-slice settings.");

            temporaryProjectPath = Path.Combine(ProjectSettings.GlobalizePath("user://"), $"synth-sound-smoke-{Guid.NewGuid():N}.voos");
            document.SaveCopyTo(temporaryProjectPath);
            LegacySaveDocument roundTrip = LegacySaveDocument.Load(temporaryProjectPath);
            JsonObject? roundTrippedSound = FindSynthSound(roundTrip.Root["sojoDatabase"]?["sojos"] as JsonArray, "Synth authoring smoke");
            if (roundTrippedSound is null || LegacySaveDocument.ReadString(roundTrippedSound["id"]) != savedId)
                throw new InvalidOperationException("The new synthesized sound did not survive a .voos save and reload.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/synth-sound-smoke.png");
            if (_soundEffectWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the synthesized sound editor screenshot.");
            GD.Print($"SYNTH_SOUND_SMOKE_OK created=true waveform=noise speed=16 spatialized=false volume0=7 pitch31=40 preview=true roundTrip=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"SYNTH_SOUND_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (_soundEffectWindow is not null && IsInstanceValid(_soundEffectWindow))
            {
                var preview = _soundEffectWindow.FindChild("SynthSoundPreview", true, false) as AudioStreamPlayer;
                preview?.Stop();
                _soundEffectWindow.Hide();
            }
            if (hadSoundDatabase)
                document.Root["sojoDatabase"] = originalSoundDatabase?.DeepClone();
            else
                document.Root.Remove("sojoDatabase");
            if (temporaryProjectPath is not null && File.Exists(temporaryProjectPath))
                File.Delete(temporaryProjectPath);
            GetTree().Quit(exitCode);
        }
    }

    private static JsonObject? FindSynthSound(JsonArray? sojos, string name)
    {
        if (sojos is null)
            return null;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo
                && LegacySaveDocument.ReadString(sojo["contentType"]) == "SoundEffect"
                && LegacySaveDocument.ReadString(sojo["name"]) == name)
                return sojo;
        }
        return null;
    }
}
