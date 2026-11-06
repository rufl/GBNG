using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Domain;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunParticleEffectSmoke()
    {
        int exitCode = 0;
        LegacySaveDocument document = _document ?? throw new InvalidOperationException("The particle editor smoke project did not load.");
        bool hadDatabase = document.Root.ContainsKey("sojoDatabase");
        JsonNode? originalDatabase = document.Root["sojoDatabase"]?.DeepClone();
        string? temporaryProjectPath = null;
        try
        {
            await WaitForUiFrames(2);
            _particleEffectWindow.RefreshFromDocument();
            _particleEffectWindow.PopupCenteredClamped(new Vector2I(1220, 820));
            await WaitForUiFrames(3);
            if (!_particleEffectWindow.Visible)
                throw new InvalidOperationException("The particle effect editor did not open.");

            var newButton = (Button)_particleEffectWindow.FindChild("ParticleNewButton", true, false);
            var nameField = (LineEdit)_particleEffectWindow.FindChild("ParticleEffectName", true, false);
            var shape = (OptionButton)_particleEffectWindow.FindChild("ParticleShape", true, false);
            var duration = (SpinBox)_particleEffectWindow.FindChild("ParticleDuration", true, false);
            var speed = (SpinBox)_particleEffectWindow.FindChild("ParticleSpeed", true, false);
            var burstCount = (SpinBox)_particleEffectWindow.FindChild("ParticleBurstCount", true, false);
            var shapeSize = (SpinBox)_particleEffectWindow.FindChild("ParticleShapeSize", true, false);
            var startSize = (SpinBox)_particleEffectWindow.FindChild("ParticleStartSize", true, false);
            var endSize = (SpinBox)_particleEffectWindow.FindChild("ParticleEndSize", true, false);
            var gravity = (SpinBox)_particleEffectWindow.FindChild("ParticleGravity", true, false);
            var addColorStop = (Button)_particleEffectWindow.FindChild("ParticleAddColorStopButton", true, false);
            var saveButton = (Button)_particleEffectWindow.FindChild("ParticleSaveButton", true, false);

            JsonArray? sojos = document.Root["sojoDatabase"]?["sojos"] as JsonArray;
            JsonObject? existingEffect = sojos?.OfType<JsonObject>()
                .FirstOrDefault(sojo => LegacySaveDocument.ReadString(sojo["contentType"]) == "ParticleEffect");
            if (existingEffect is null)
                throw new InvalidOperationException("The smoke project has no legacy particle effect to edit.");
            string existingId = LegacySaveDocument.ReadString(existingEffect["id"]);
            string existingName = LegacySaveDocument.ReadString(existingEffect["name"]);
            JsonObject existingContent = ReadParticleContent(existingEffect);
            existingContent["smokePreservedField"] = "kept";
            existingContent["duration"] = 150f;
            existingContent["shapeType"] = 8;
            existingContent["startSize"] = 0.5f;
            existingContent["endSize"] = 1.5f;
            JsonArray existingStops = existingContent["colorOverLifetime"]?.AsArray()
                ?? throw new InvalidDataException("The existing particle effect has no color stops.");
            JsonObject existingStop = existingStops[0]?.AsObject()
                ?? throw new InvalidDataException("The existing particle effect has an invalid color stop.");
            string existingStopId = LegacySaveDocument.ReadString(existingStop["id"]);
            existingStop["position"] = 1.25f;
            existingStop["smokePreservedStopField"] = "kept";
            JsonObject existingColor = existingStop["value"]?.AsObject()
                ?? throw new InvalidDataException("The existing particle effect has an invalid color.");
            existingColor["smokePreservedColorField"] = "kept";
            existingEffect["content"] = existingContent.ToJsonString();
            _particleEffectWindow.FocusParticleEffect(existingId);
            var existingColorField = (ColorPickerButton)_particleEffectWindow.FindChild("ParticleColorValue0", true, false);
            Color changedExistingColor = new(0.2f, 0.4f, 0.6f, 0.8f);
            existingColorField.Color = changedExistingColor;
            existingColorField.EmitSignal(ColorPickerButton.SignalName.ColorChanged, changedExistingColor);
            nameField.Text = existingName + " editor smoke";
            nameField.EmitSignal(LineEdit.SignalName.TextChanged, nameField.Text);
            saveButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(3);
            JsonObject editedEffect = FindParticleEffectById(document.Root["sojoDatabase"]?["sojos"] as JsonArray, existingId)
                ?? throw new InvalidOperationException("Editing the existing particle effect removed it from the project.");
            JsonObject editedContent = ReadParticleContent(editedEffect);
            JsonArray editedStops = editedContent["colorOverLifetime"]?.AsArray()
                ?? throw new InvalidDataException("Editing removed the particle color stops.");
            JsonObject? editedStop = editedStops
                .OfType<JsonObject>()
                .FirstOrDefault(stop => LegacySaveDocument.ReadString(stop["id"]) == existingStopId);
            JsonObject? editedColor = editedStop?["value"] as JsonObject;
            if (LegacySaveDocument.ReadString(editedContent["smokePreservedField"]) != "kept"
                || Math.Abs(LegacySaveDocument.ReadFloat(editedContent["duration"], -1f) - 150f) > 0.001f
                || LegacySaveDocument.ReadInt(editedContent["shapeType"], -1) != 8
                || Math.Abs(LegacySaveDocument.ReadFloat(editedContent["startSize"], -1f) - 0.5f) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(editedContent["endSize"], -1f) - 1.5f) > 0.001f
                || editedStop is null
                || Math.Abs(LegacySaveDocument.ReadFloat(editedStop["position"], -1f) - 1.25f) > 0.001f
                || LegacySaveDocument.ReadString(editedStop["smokePreservedStopField"]) != "kept"
                || LegacySaveDocument.ReadString(editedColor?["smokePreservedColorField"]) != "kept"
                || Math.Abs(LegacySaveDocument.ReadFloat(editedColor?["r"], -1f) - changedExistingColor.R) > 0.001f)
                throw new InvalidOperationException("Editing an effect changed untouched values or discarded unowned top-level, color-stop, or color data.");

            newButton.EmitSignal(Button.SignalName.Pressed);
            nameField.Text = "Particle editor smoke";
            shape.Select(2);
            shape.EmitSignal(OptionButton.SignalName.ItemSelected, 2L);
            duration.Value = 2.5;
            speed.Value = 3.5;
            burstCount.Value = 14;
            shapeSize.Value = 1.75;
            startSize.Value = 0.5;
            endSize.Value = 1.5;
            gravity.Value = 0.25;
            addColorStop.EmitSignal(Button.SignalName.Pressed);
            var thirdColor = (ColorPickerButton)_particleEffectWindow.FindChild("ParticleColorValue2", true, false);
            var thirdPosition = (SpinBox)_particleEffectWindow.FindChild("ParticleColorPosition2", true, false);
            Color accent = new(0.9f, 0.3f, 0.1f, 0.8f);
            thirdColor.Color = accent;
            thirdColor.EmitSignal(ColorPickerButton.SignalName.ColorChanged, accent);
            thirdPosition.Value = 0.75;
            saveButton.EmitSignal(Button.SignalName.Pressed);
            await WaitForUiFrames(3);

            JsonObject savedEffect = FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke")
                ?? throw new InvalidOperationException("The particle editor did not add the new effect to the project.");
            string savedId = LegacySaveDocument.ReadString(savedEffect["id"]);
            if (savedId.Length == 0)
                throw new InvalidOperationException("The new particle effect has no stable ID.");
            JsonObject content = ReadParticleContent(savedEffect);
            JsonArray stops = content["colorOverLifetime"]?.AsArray()
                ?? throw new InvalidDataException("The particle effect has no color-over-lifetime stops.");
            JsonObject? accentStop = stops
                .OfType<JsonObject>()
                .FirstOrDefault(stop => Math.Abs(LegacySaveDocument.ReadFloat(stop["position"], -1f) - 0.75f) < 0.001f);
            JsonObject? accentColor = accentStop?["value"] as JsonObject;
            if (LegacySaveDocument.ReadInt(content["shapeType"], -1) != 2
                || Math.Abs(LegacySaveDocument.ReadFloat(content["duration"], -1f) - 2.5f) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(content["speed"], -1f) - 3.5f) > 0.001f
                || LegacySaveDocument.ReadInt(content["burstCount"], -1) != 14
                || Math.Abs(LegacySaveDocument.ReadFloat(content["shapeSize"], -1f) - 1.75f) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(content["startSize"], -1f) - 0.5f) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(content["endSize"], -1f) - 1.5f) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(content["gravityModifier"], -1f) - 0.25f) > 0.001f
                || stops.Count != 3
                || stops.Any(node => node is JsonObject stop && LegacySaveDocument.ReadString(stop["id"]).Length == 0)
                || accentColor is null
                || Math.Abs(LegacySaveDocument.ReadFloat(accentColor["r"], -1f) - accent.R) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(accentColor["g"], -1f) - accent.G) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(accentColor["b"], -1f) - accent.B) > 0.001f
                || Math.Abs(LegacySaveDocument.ReadFloat(accentColor["a"], -1f) - accent.A) > 0.001f)
                throw new InvalidOperationException($"The particle editor did not persist the selected fields and color stops: startSize={LegacySaveDocument.ReadFloat(content["startSize"], -1f)}, endSize={LegacySaveDocument.ReadFloat(content["endSize"], -1f)}, colors={stops.Count}, accent={accentColor?.ToJsonString() ?? "<missing>"}");

            CpuParticles3D preview = (CpuParticles3D)_particleEffectWindow.FindChild("ParticleEffectPreview", true, false);
            if (preview.Amount > 192 || preview.EmissionShape != CpuParticles3D.EmissionShapeEnum.Box)
                throw new InvalidOperationException("The editor preview does not use the selected particle shape or preview particle cap.");
            var previewCamera = (Camera3D)_particleEffectWindow.FindChild("PreviewCamera", true, false);
            shapeSize.Value = 20;
            if (Math.Abs(previewCamera.Position.Z - 120f) > 0.001f)
                throw new InvalidOperationException("The live preview did not frame large emission shapes.");
            shapeSize.Value = 1.75;
            var continuousPreviewToggle = (CheckBox)_particleEffectWindow.FindChild("ParticleContinuousPreview", true, false);
            continuousPreviewToggle.ButtonPressed = true;
            continuousPreviewToggle.EmitSignal("toggled", true);
            CpuParticles3D streamingPreview = (CpuParticles3D)_particleEffectWindow.FindChild("ParticleEffectPreview", true, false);
            if (streamingPreview.OneShot
                || streamingPreview.Amount != 35
                || Math.Abs(streamingPreview.Explosiveness) > 0.001f)
                throw new InvalidOperationException("Continuous preview did not preserve the legacy particles-per-second emission rate.");
            continuousPreviewToggle.ButtonPressed = false;
            continuousPreviewToggle.EmitSignal("toggled", false);
            CpuParticles3D runtimeParticles = LegacyParticleEffectFactory.Create(content, stream: false);
            if (runtimeParticles.Amount != 14
                || runtimeParticles.EmissionShape != CpuParticles3D.EmissionShapeEnum.Box
                || runtimeParticles.EmissionBoxExtents.DistanceTo(Vector3.One * 1.75f) > 0.001f
                || Math.Abs(runtimeParticles.Gravity.Y + 2.45f) > 0.001f
                || Math.Abs(runtimeParticles.InitialVelocityMax - 3.5f) > 0.001f
                || Math.Abs(runtimeParticles.AngularVelocityCurve!.Sample(0f) - 2f) > 0.001f
                || Math.Abs(runtimeParticles.ScaleAmountCurve!.Sample(1f) - 1.5f) > 0.001f)
                throw new InvalidOperationException("The Godot particle runtime did not apply the saved effect settings.");
            runtimeParticles.Free();
            var cappedStreamContent = new JsonObject { ["duration"] = 120f, ["burstCount"] = 4096 };
            CpuParticles3D cappedStream = LegacyParticleEffectFactory.Create(cappedStreamContent, stream: true, maximumParticles: 192);
            if (cappedStream.Amount != 192)
                throw new InvalidOperationException("Continuous preview exceeded its bounded particle budget.");
            cappedStream.Free();
            foreach ((int shapeType, CpuParticles3D.EmissionShapeEnum expectedShape) in new[]
            {
                (0, CpuParticles3D.EmissionShapeEnum.Ring),
                (1, CpuParticles3D.EmissionShapeEnum.Sphere),
                (2, CpuParticles3D.EmissionShapeEnum.Box),
                (3, CpuParticles3D.EmissionShapeEnum.Ring),
                (4, CpuParticles3D.EmissionShapeEnum.Box)
            })
            {
                JsonObject shapeContent = content.DeepClone().AsObject();
                shapeContent["shapeType"] = shapeType;
                CpuParticles3D shapePreview = LegacyParticleEffectFactory.Create(shapeContent, stream: false);
                if (shapePreview.EmissionShape != expectedShape
                    || (shapeType == 0 && shapePreview.EmissionRingConeAngle != 0f)
                    || (shapeType == 3 && shapePreview.EmissionRingConeAngle != 90f)
                    || (shapeType == 1 && Math.Abs(shapePreview.EmissionSphereRadius - 1.75f) > 0.001f))
                    throw new InvalidOperationException($"Legacy particle shape {shapeType} was not mapped to Godot.");
                shapePreview.Free();
            }

            int historyBeforeUndo = _historyPosition;
            UndoEdit();
            if (_historyPosition != historyBeforeUndo - 1
                || FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke") is not null)
                throw new InvalidOperationException("Undo did not remove the created particle effect.");
            RedoEdit();
            savedEffect = FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke")
                ?? throw new InvalidOperationException("Redo did not restore the created particle effect.");

            temporaryProjectPath = Path.Combine(ProjectSettings.GlobalizePath("user://"), $"particle-editor-smoke-{Guid.NewGuid():N}.voos");
            document.SaveCopyTo(temporaryProjectPath);
            LegacySaveDocument roundTrip = LegacySaveDocument.Load(temporaryProjectPath);
            JsonObject roundTripped = FindParticleEffect(roundTrip.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke")
                ?? throw new InvalidOperationException("The particle effect did not survive a .voos save and reload.");
            if (LegacySaveDocument.ReadString(roundTripped["id"]) != savedId
                || LegacySaveDocument.ReadInt(ReadParticleContent(roundTripped)["shapeType"], -1) != 2)
                throw new InvalidOperationException("The particle effect's identity or settings changed after a save round trip.");
            _particleEffectWindow.FocusParticleEffect(savedId);

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/particle-effect-editor-smoke.png");
            if (_particleEffectWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the particle editor screenshot.");

            var copyButton = (Button)_particleEffectWindow.FindChild("ParticleCopyButton", true, false);
            copyButton.EmitSignal(Button.SignalName.Pressed);
            JsonObject copied = FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke copy")
                ?? throw new InvalidOperationException("Copy did not add a particle effect.");
            if (LegacySaveDocument.ReadString(copied["id"]) == savedId
                || ReadParticleContent(copied).ToJsonString() != content.ToJsonString())
                throw new InvalidOperationException("Copy did not preserve particle content under a distinct ID.");

            var deleteButton = (Button)_particleEffectWindow.FindChild("ParticleDeleteButton", true, false);
            deleteButton.EmitSignal(Button.SignalName.Pressed);
            var confirmation = (ConfirmationDialog)_particleEffectWindow.FindChild("ParticleDeleteConfirmation", true, false);
            confirmation.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            if (FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke copy") is not null)
                throw new InvalidOperationException("Delete did not remove the copied particle effect.");
            UndoEdit();
            if (FindParticleEffect(document.Root["sojoDatabase"]?["sojos"] as JsonArray, "Particle editor smoke copy") is null)
                throw new InvalidOperationException("Undo did not restore the deleted particle effect.");

            GD.Print($"PARTICLE_EFFECT_EDITOR_SMOKE_OK created=true shape=box colors={stops.Count} undoRedo=true copyDelete=true runtime=true roundTrip=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"PARTICLE_EFFECT_EDITOR_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (_particleEffectWindow is not null && IsInstanceValid(_particleEffectWindow))
                _particleEffectWindow.Hide();
            if (hadDatabase)
                document.Root["sojoDatabase"] = originalDatabase?.DeepClone();
            else
                document.Root.Remove("sojoDatabase");
            if (temporaryProjectPath is not null && File.Exists(temporaryProjectPath))
                File.Delete(temporaryProjectPath);
            GetTree().Quit(exitCode);
        }
    }

    private static JsonObject? FindParticleEffect(JsonArray? sojos, string name)
    {
        if (sojos is null)
            return null;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo
                && LegacySaveDocument.ReadString(sojo["contentType"]) == "ParticleEffect"
                && LegacySaveDocument.ReadString(sojo["name"]) == name)
                return sojo;
        }
        return null;
    }
    private static JsonObject? FindParticleEffectById(JsonArray? sojos, string id)
    {
        if (sojos is null)
            return null;
        foreach (JsonNode? node in sojos)
        {
            if (node is JsonObject sojo
                && LegacySaveDocument.ReadString(sojo["contentType"]) == "ParticleEffect"
                && LegacySaveDocument.ReadString(sojo["id"]) == id)
                return sojo;
        }
        return null;
    }


    private static JsonObject ReadParticleContent(JsonObject sojo)
        => JsonNode.Parse(LegacySaveDocument.ReadString(sojo["content"]))?.AsObject()
            ?? throw new InvalidDataException("The particle effect has invalid content.");
}
