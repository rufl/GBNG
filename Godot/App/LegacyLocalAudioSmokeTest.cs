using System.Text.Json.Nodes;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.Networking;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunLocalAudioSmoke()
    {
        int exitCode = 0;
        string originalProjectPath = _document?.SourcePath
            ?? throw new InvalidOperationException("The local-audio smoke project did not load.");
        string temporaryDirectory = Path.Combine(
            ProjectSettings.GlobalizePath("user://"),
            $"local-audio-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string projectPath = Path.Combine(temporaryDirectory, "audio-smoke.voos");
        string saveAsPath = Path.Combine(temporaryDirectory, "audio-smoke-copy.voos");
        string? configuredAudioSource = GetLocalAudioSmokeSource();
        string sourceAudioPath = configuredAudioSource is null
            ? Path.Combine(temporaryDirectory, "local-audio-smoke-tone.wav")
            : Path.GetFullPath(configuredAudioSource);
        string sourceAudioExtension = Path.GetExtension(sourceAudioPath).TrimStart('.').ToLowerInvariant();
        bool originalNativeDialog = _importAudioDialog.UseNativeDialog;
        string? extractedBundleProjectPath = null;
        try
        {
            _document.SaveCopyTo(projectPath);
            _catalog.CopyProjectAssets(GetProjectAssetSourcePath(), projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable local-audio smoke project did not load.");

            if (configuredAudioSource is null)
                WriteSmokeWave(sourceAudioPath);
            else if (!File.Exists(sourceAudioPath) || sourceAudioExtension is not ("wav" or "ogg"))
                throw new InvalidDataException($"The optional audio smoke source must be an existing WAV or Ogg Vorbis file (path='{sourceAudioPath}', extension='{sourceAudioExtension}', exists={File.Exists(sourceAudioPath)}).");
            _importAudioDialog.UseNativeDialog = false;
            _soundEffectWindow.RefreshFromDocument();
            _soundEffectWindow.PopupCenteredClamped(new Vector2I(1040, 760));
            await WaitForUiFrames(2);
            var importButton = (Button)_soundEffectWindow.FindChild("LocalAudioImportButton", true, false);
            importButton.EmitSignal(Button.SignalName.Pressed);
            if (!_importAudioDialog.Visible)
                throw new InvalidOperationException("The sound editor did not open its local audio file picker.");
            ImportSoundAudioFromPath(sourceAudioPath);
            _importAudioDialog.Hide();
            await WaitForUiFrames(2);

            string newSoundName = Path.GetFileNameWithoutExtension(sourceAudioPath);
            JsonObject importedSound = FindSoundEffectByName(newSoundName)
                ?? throw new InvalidOperationException("Importing audio did not add a sound effect to the project.");
            string importedId = LegacySaveDocument.ReadString(importedSound["id"]);
            JsonObject importedContent = ReadSoundContent(importedSound);
            string firstAssetId = LegacySaveDocument.ReadString(importedContent["localAssetId"]);
            string firstAssetPath = Path.Combine(
                temporaryDirectory,
                "audio-smoke.assets",
                "sounds",
                $"{firstAssetId}.{sourceAudioExtension}");
            if (LegacySaveDocument.ReadInt(importedContent["effectType"], -1) != 2
                || LegacySaveDocument.ReadString(importedContent["localAssetExtension"]) != sourceAudioExtension
                || !File.Exists(firstAssetPath))
                throw new InvalidOperationException("The imported audio was not stored as a project-local sound asset.");

            var previewButton = (Button)_soundEffectWindow.FindChild("SynthPreviewButton", true, false);
            previewButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(2);
            var preview = (AudioStreamPlayer)_soundEffectWindow.FindChild("SynthSoundPreview", true, false);
            if (!preview.Playing || preview.Bus != GBNG.Audio.LegacyAudioSettings.SfxBusName
                || !IsPlayableAudioStream(preview.Stream))
                throw new InvalidOperationException("The imported audio could not be previewed on the SFX bus.");
            AssertLocalAudioPlayback(importedId, spatialized: true);

            string workshopId = Guid.NewGuid().ToString("N");
            var workshopSound = new JsonObject
            {
                ["id"] = workshopId,
                ["name"] = "Legacy workshop audio smoke",
                ["contentType"] = "SoundEffect",
                ["content"] = new JsonObject
                {
                    ["effectType"] = 1,
                    ["steamWorkshopId"] = "123456789",
                    ["spatialized"] = true
                }.ToJsonString()
            };
            SaveSoundEffect(null, workshopSound, "Add legacy workshop audio smoke effect");
            _soundEffectWindow.FocusSoundEffect(workshopId);
            var replaceButton = (Button)_soundEffectWindow.FindChild("ReplaceAudioButton", true, false);
            replaceButton.EmitSignal(Button.SignalName.Pressed);
            if (!_importAudioDialog.Visible)
                throw new InvalidOperationException("A legacy workshop sound did not offer local audio replacement.");
            ImportSoundAudioFromPath(sourceAudioPath);
            _importAudioDialog.Hide();
            await WaitForUiFrames(2);

            JsonObject replacedSound = FindSoundEffect(workshopId)
                ?? throw new InvalidOperationException("Replacing workshop audio changed or removed the sound effect ID.");
            JsonObject replacedContent = ReadSoundContent(replacedSound);
            string replacementAssetId = LegacySaveDocument.ReadString(replacedContent["localAssetId"]);
            if (LegacySaveDocument.ReadString(replacedSound["name"]) != "Legacy workshop audio smoke"
                || LegacySaveDocument.ReadInt(replacedContent["effectType"], -1) != 2
                || replacementAssetId.Length == 0
                || replacementAssetId == firstAssetId)
                throw new InvalidOperationException("Local audio replacement did not preserve the workshop effect identity and name.");

            _soundEffectWindow.FocusSoundEffect(workshopId);
            var nameField = (LineEdit)_soundEffectWindow.FindChild("SynthSoundName", true, false);
            var spatialized = (CheckBox)_soundEffectWindow.FindChild("SynthSpatialized", true, false);
            var saveButton = (Button)_soundEffectWindow.FindChild("SynthSaveButton", true, false);
            nameField.Text = "Renamed local sound";
            spatialized.ButtonPressed = false;
            int historyPosition = _historyPosition;
            saveButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(2);
            if (_historyPosition != historyPosition + 1)
                throw new InvalidOperationException("Editing an imported sound did not create one Undo step.");
            JsonObject editedSound = FindSoundEffect(workshopId)
                ?? throw new InvalidOperationException("Editing the imported sound removed it from the project.");
            JsonObject editedContent = ReadSoundContent(editedSound);
            if (LegacySaveDocument.ReadString(editedSound["name"]) != "Renamed local sound"
                || LegacySaveDocument.ReadInt(editedContent["effectType"], -1) != 2
                || LegacySaveDocument.ReadBool(editedContent["spatialized"], true)
                || LegacySaveDocument.ReadString(editedContent["localAssetId"]) != replacementAssetId)
                throw new InvalidOperationException("The imported sound editor did not preserve its local asset while saving name and spatialization.");
            UndoEdit();
            JsonObject? undoneSound = FindSoundEffect(workshopId);
            if (LegacySaveDocument.ReadString(undoneSound?["name"]) != "Legacy workshop audio smoke"
                || !LegacySaveDocument.ReadBool(ReadSoundContent(undoneSound)["spatialized"], false))
                throw new InvalidOperationException("Undo did not restore the imported sound's prior name and spatialization.");
            RedoEdit();
            AssertLocalAudioPlayback(workshopId, spatialized: false);

            _document!.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            JsonObject roundTrippedSound = FindSoundEffect(workshopId)
                ?? throw new InvalidOperationException("The imported sound did not survive saving and reloading the .voos project.");
            JsonObject roundTrippedContent = ReadSoundContent(roundTrippedSound);
            if (LegacySaveDocument.ReadString(roundTrippedSound["name"]) != "Renamed local sound"
                || LegacySaveDocument.ReadString(roundTrippedContent["localAssetId"]) != replacementAssetId
                || !IsPlayableAudioStream(LegacyAudioAssetCatalog.LoadStream(projectPath, roundTrippedContent)))
                throw new InvalidOperationException("The local audio reference or file did not survive a .voos save and reload.");

            _catalog.CopyProjectAssets(projectPath, saveAsPath);
            _document.SaveCopyTo(saveAsPath);
            LegacySaveDocument copiedDocument = LegacySaveDocument.Load(saveAsPath);
            JsonObject copiedSound = FindSoundEffect(copiedDocument.Root["sojoDatabase"]?["sojos"] as JsonArray, workshopId)
                ?? throw new InvalidOperationException("Save As did not preserve the imported sound effect.");
            JsonObject copiedContent = ReadSoundContent(copiedSound);
            if (!IsPlayableAudioStream(LegacyAudioAssetCatalog.LoadStream(saveAsPath, copiedContent))
                || !File.Exists(Path.Combine(temporaryDirectory, "audio-smoke-copy.assets", "manifest.json")))
                throw new InvalidOperationException("Save As did not copy the local audio sidecar and manifest.");

            byte[] bundle = LegacyProjectPackage.CreateAssetBundle(projectPath);
            extractedBundleProjectPath = LegacyProjectPackage.ExtractAssetBundle(bundle);
            if (!IsPlayableAudioStream(LegacyAudioAssetCatalog.LoadStream(extractedBundleProjectPath, roundTrippedContent)))
                throw new InvalidOperationException("The local audio file did not survive project asset bundle transfer.");
            DeleteExtractedAssetBundle(extractedBundleProjectPath);
            extractedBundleProjectPath = null;

            LoadProject(saveAsPath);
            await WaitForUiFrames(2);
            AssertLocalAudioPlayback(workshopId, spatialized: false);
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/local-audio-smoke.png");
            if (_soundEffectWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the imported sound editor screenshot.");
            GD.Print($"LOCAL_AUDIO_SMOKE_OK audio={sourceAudioExtension} import=true replaceWorkshop=true preview=true spatializedPlayback=true flatPlayback=true undoRedo=true saveReload=true saveAs=true bundle=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"LOCAL_AUDIO_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (extractedBundleProjectPath is not null)
                DeleteExtractedAssetBundle(extractedBundleProjectPath);
            if (_importAudioDialog is not null && IsInstanceValid(_importAudioDialog))
            {
                _importAudioDialog.Hide();
                _importAudioDialog.UseNativeDialog = originalNativeDialog;
            }
            if (_soundEffectWindow is not null && IsInstanceValid(_soundEffectWindow))
            {
                var preview = _soundEffectWindow.FindChild("SynthSoundPreview", true, false) as AudioStreamPlayer;
                preview?.Stop();
                _soundEffectWindow.Hide();
            }
            try
            {
                LoadProject(originalProjectPath);
                await WaitForUiFrames(2);
            }
            catch (Exception exception)
            {
                exitCode = 1;
                GD.PushError($"LOCAL_AUDIO_SMOKE_RESTORE_FAILED: {exception}");
            }
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private void AssertLocalAudioPlayback(string soundId, bool spatialized)
    {
        if (_behaviorRuntime is null)
            throw new InvalidOperationException("The behavior runtime is not available for audio playback.");
        WorldActorNode actor = _actors.FirstOrDefault()
            ?? throw new InvalidOperationException("The smoke project has no actor for positional sound playback.");
        string result = _behaviorRuntime.PlaySound(new JsonObject
        {
            ["soundId"] = soundId,
            ["actorName"] = actor.LegacyName
        });
        if (result != "true")
            throw new InvalidOperationException("The behavior runtime could not play the imported audio effect.");

        if (spatialized)
        {
            AudioStreamPlayer3D? player = actor.GetChildren().OfType<AudioStreamPlayer3D>().LastOrDefault();
            if (player is not { Playing: true } || !IsPlayableAudioStream(player.Stream))
                throw new InvalidOperationException("Spatialized imported audio did not use a playing 3D audio player.");
        }
        else
        {
            AudioStreamPlayer? player = actor.GetChildren().OfType<AudioStreamPlayer>().LastOrDefault();
            if (player is not { Playing: true } || !IsPlayableAudioStream(player.Stream))
                throw new InvalidOperationException("Non-spatialized imported audio did not use a playing 2D audio player.");
        }
    }

    private JsonObject? FindSoundEffectByName(string name)
    {
        JsonArray? sojos = _document?.Root["sojoDatabase"]?["sojos"] as JsonArray;
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

    private static JsonObject? FindSoundEffect(JsonArray? sojos, string id)
    {
        if (sojos is null)
            return null;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo
                && LegacySaveDocument.ReadString(sojo["id"]) == id
                && LegacySaveDocument.ReadString(sojo["contentType"]) == "SoundEffect")
                return sojo;
        }
        return null;
    }


    private static JsonObject ReadSoundContent(JsonObject? sound)
    {
        string content = LegacySaveDocument.ReadString(sound?["content"]);
        return content.Length > 0
            ? JsonNode.Parse(content)?.AsObject() ?? throw new InvalidDataException("Sound effect content is not a JSON object.")
            : throw new InvalidDataException("Sound effect content is missing.");
    }

    private static bool IsPlayableAudioStream(AudioStream? stream)
        => stream is AudioStreamWav { Data.Length: > 0 }
            || stream is AudioStreamOggVorbis ogg && ogg.GetLength() > 0d;

    private static string? GetLocalAudioSmokeSource()
    {
        const string prefix = "--smoke-local-audio-source=";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                return argument[prefix.Length..];
        }
        return null;
    }

    private static void WriteSmokeWave(string path)
    {
        const int sampleRate = 22_050;
        const int sampleCount = sampleRate / 2;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;
        const int dataSize = sampleCount * bytesPerSample;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        WriteFourCc(writer, "RIFF");
        writer.Write(36 + dataSize);
        WriteFourCc(writer, "WAVE");
        WriteFourCc(writer, "fmt ");
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write(bitsPerSample);
        WriteFourCc(writer, "data");
        writer.Write(dataSize);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double phase = 2d * Math.PI * 440d * sample / sampleRate;
            writer.Write((short)(Math.Sin(phase) * short.MaxValue * 0.35d));
        }
    }

    private static void WriteFourCc(BinaryWriter writer, string value)
    {
        foreach (char character in value)
            writer.Write((byte)character);
    }
}
