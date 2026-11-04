using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using GBNG.Behaviors;
using GBNG.Assets;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunControlsSmoke()
    {
        int exitCode = 0;
        string settingsPath = ProjectSettings.GlobalizePath(EditorSettingsPath);
        byte[]? originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await WaitForUiFrames(2);
            LegacyControlsWindow window = _controlsWindow;
            if (!IsInstanceValid(window))
                throw new InvalidOperationException("The controls preferences window was not created.");
            OpenControls();
            await WaitForUiFrames(2);

            var invertLook = (CheckButton)window.FindChild("ControlsInvertVerticalLook", true, false);
            var lookSensitivity = (HSlider)window.FindChild("ControlsLookSensitivity", true, false);
            var wheelSensitivity = (HSlider)window.FindChild("ControlsWheelSensitivity", true, false);
            var reset = (Button)window.FindChild("ControlsResetDefaults", true, false);
            if (!window.Visible
                || !Mathf.IsEqualApprox((float)lookSensitivity.MinValue, 0.1f)
                || !Mathf.IsEqualApprox((float)lookSensitivity.MaxValue, 3f))
                throw new InvalidOperationException("The controls window did not expose the legacy mouse-look preferences.");

            invertLook.SetPressedNoSignal(true);
            invertLook.EmitSignal("toggled", true);
            lookSensitivity.Value = 2.0;
            wheelSensitivity.Value = 1.0;
            var savedPreferences = new ConfigFile();
            if (!_invertMouseLook
                || !Mathf.IsEqualApprox(_mouseLookSensitivity, 2f)
                || !Mathf.IsEqualApprox(_mouseWheelSensitivity, 1f)
                || savedPreferences.Load(EditorSettingsPath) != Error.Ok
                || !savedPreferences.GetValue("controls", "invert_vertical_look", false).AsBool()
                || !Mathf.IsEqualApprox((float)savedPreferences.GetValue("controls", "mouse_look_sensitivity", 0.0).AsDouble(), 2f)
                || !Mathf.IsEqualApprox((float)savedPreferences.GetValue("controls", "mouse_wheel_sensitivity", 0.0).AsDouble(), 1f))
                throw new InvalidOperationException("Changing the controls did not apply and persist all three preferences.");

            Vector2 normalLook = ComputeMouseLookDelta(new Vector2(10f, 5f), 1f, false);
            Vector2 invertedLook = ComputeMouseLookDelta(new Vector2(10f, 5f), 1f, true);
            Vector2 fasterLook = ComputeMouseLookDelta(new Vector2(10f, 5f), 2f, false);
            if (!Mathf.IsEqualApprox(normalLook.X, -0.04f)
                || !Mathf.IsEqualApprox(normalLook.Y, -0.02f)
                || !Mathf.IsEqualApprox(invertedLook.Y, 0.02f)
                || !Mathf.IsEqualApprox(fasterLook.X, -0.08f)
                || !Mathf.IsEqualApprox(fasterLook.Y, -0.04f))
                throw new InvalidOperationException("Invert look and mouse-look sensitivity did not change camera input as expected.");
            float maximumWheelMultiplier = LegacyControlsWindow.WheelZoomSensitivityMultiplier(1f);
            float fastWheelDistance = ApplyMouseWheelZoom(10f, MouseButton.WheelUp, 1f);
            if (maximumWheelMultiplier <= 1f
                || !Mathf.IsEqualApprox(fastWheelDistance, 10f * Mathf.Pow(0.88f, maximumWheelMultiplier)))
                throw new InvalidOperationException("Mouse-wheel sensitivity did not scale camera zoom.");

            reset.EmitSignal("pressed");
            await WaitForUiFrames(1);
            if (_invertMouseLook
                || !Mathf.IsEqualApprox(_mouseLookSensitivity, LegacyControlsWindow.DefaultMouseLookSensitivity)
                || !Mathf.IsEqualApprox(_mouseWheelSensitivity, LegacyControlsWindow.DefaultWheelSensitivitySliderValue)
                || !Mathf.IsEqualApprox(ApplyMouseWheelZoom(10f, MouseButton.WheelUp, 1f), 8.8f))
                throw new InvalidOperationException("Reset to defaults did not restore original camera-control behavior.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/controls-preferences-smoke.png");
            if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the controls preferences screenshot.");
            GD.Print($"CONTROLS_SMOKE_OK invert=true lookSensitivity=true wheelSensitivity=true persistence=true reset=true screenshot={screenshot}");
        }
        catch (System.Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CONTROLS_SMOKE_FAILED: {exception}");
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
                GD.PushError($"Could not restore control preferences after smoke: {exception}");
            }
            GetTree().Quit(exitCode);
        }
    }

    private async void RunWorldSettingsSmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        string? temporaryDirectory = null;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || originalProjectPath is null)
                throw new InvalidOperationException("A project must be open for the world-settings smoke run.");

            string sourcePath = ProjectSettings.GlobalizePath(originalProjectPath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The world-settings smoke project was not found.", sourcePath);
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"GBNG-WorldSettings-Smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            string projectPath = Path.Combine(temporaryDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable world-settings smoke project did not load.");

            JsonObject originalStage = _document.Stage?.DeepClone().AsObject() ?? new JsonObject();
            OpenWorldSettings();
            await WaitForUiFrames(2);
            LegacyWorldSettingsWindow window = _worldSettingsWindow;
            if (!window.Visible)
                throw new InvalidOperationException("The world settings window did not open.");

            OptionButton skyType = (OptionButton)window.FindChild("WorldSettingsSkyType", true, false);
            ColorPickerButton skyColor = (ColorPickerButton)window.FindChild("WorldSettingsSkyColor", true, false);
            OptionButton lightingMode = (OptionButton)window.FindChild("WorldSettingsLightingMode", true, false);
            string originalSkyType = LegacySaveDocument.ReadString(originalStage["skyType"], "Day");
            SelectWorldSetting(skyType, string.Equals(originalSkyType, "Space", StringComparison.OrdinalIgnoreCase) ? "Day" : "Space");
            SelectWorldSetting(skyType, "SolidColor");

            var expectedSkyColor = new Color(0.17f, 0.31f, 0.73f, 1f);
            skyColor.Color = expectedSkyColor;
            skyColor.EmitSignal("color_changed", expectedSkyColor);
            string originalLighting = LegacySaveDocument.ReadString(originalStage["sceneLightingMode"], "Day");
            string expectedLighting = string.Equals(originalLighting, "Night", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Night";
            SelectWorldSetting(lightingMode, expectedLighting);

            AssertStageVisuals(_document.Stage);
            if (LegacySaveDocument.ReadString(_document.Stage?["skyType"]) != "SolidColor"
                || LegacySaveDocument.ReadString(_document.Stage?["sceneLightingMode"]) != expectedLighting
                || !ColorsMatch(ReadStageVisualColor(_document.Stage?["skyColor"]), expectedSkyColor))
                throw new InvalidOperationException("World settings were not written to the expected .voos stage values.");

            for (int index = 0; index < 4; index++)
                UndoEdit();
            await WaitForUiFrames(2);
            if (!JsonNode.DeepEquals(_document.Stage?["skyType"], originalStage["skyType"])
                || !JsonNode.DeepEquals(_document.Stage?["skyColor"], originalStage["skyColor"])
                || !JsonNode.DeepEquals(_document.Stage?["sceneLightingMode"], originalStage["sceneLightingMode"]))
                throw new InvalidOperationException("World-settings undo did not restore the original .voos stage values.");
            AssertStageVisuals(_document.Stage);

            for (int index = 0; index < 4; index++)
                RedoEdit();
            await WaitForUiFrames(2);
            AssertStageVisuals(_document.Stage);
            if (LegacySaveDocument.ReadString(_document.Stage?["skyType"]) != "SolidColor"
                || LegacySaveDocument.ReadString(_document.Stage?["sceneLightingMode"]) != expectedLighting
                || !ColorsMatch(ReadStageVisualColor(_document.Stage?["skyColor"]), expectedSkyColor))
                throw new InvalidOperationException("World-settings redo did not restore the edited .voos values.");

            _document.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath
                || LegacySaveDocument.ReadString(_document.Stage?["skyType"]) != "SolidColor"
                || LegacySaveDocument.ReadString(_document.Stage?["sceneLightingMode"]) != expectedLighting
                || !ColorsMatch(ReadStageVisualColor(_document.Stage?["skyColor"]), expectedSkyColor))
                throw new InvalidOperationException("World settings did not persist after saving and reloading the .voos project.");
            AssertStageVisuals(_document.Stage);

            OpenWorldSettings();
            await WaitForUiFrames(2);
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/world-settings-smoke.png");
            if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the world-settings screenshot.");
            GD.Print($"WORLD_SETTINGS_SMOKE_OK sky=SolidColor lighting={expectedLighting} undo=redo=persistence=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"WORLD_SETTINGS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            _worldSettingsWindow?.Hide();
            if (originalProjectPath is not null)
            {
                LoadProject(originalProjectPath);
                await WaitForUiFrames(2);
                if (!string.Equals(_document?.SourcePath, originalProjectPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    exitCode = 1;
                    GD.PushError("WORLD_SETTINGS_SMOKE_FAILED: the original project could not be restored.");
                }
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private static void SelectWorldSetting(OptionButton picker, string value)
    {
        for (int index = 0; index < picker.ItemCount; index++)
        {
            if (picker.GetItemMetadata(index).AsString() != value)
                continue;
            picker.Select(index);
            picker.EmitSignal("item_selected", (long)index);
            return;
        }
        throw new InvalidOperationException($"World settings did not expose the '{value}' option.");
    }

    private void AssertStageVisuals(JsonObject? stage)
    {
        string skyType = LegacySaveDocument.ReadString(stage?["skyType"], "Day");
        Color skyColor = ReadStageVisualColor(stage?["skyColor"]);
        Color expectedBackground = skyType.ToLowerInvariant() switch
        {
            "day" => new Color("#65B6FB"),
            "space" => new Color("#3A1A24"),
            "overcast" => new Color("#D2DCF7"),
            _ => skyColor
        };
        string lightingMode = LegacySaveDocument.ReadString(stage?["sceneLightingMode"], "Day");
        float expectedAmbient = lightingMode.ToLowerInvariant() switch { "night" => 0.28f, "dark" => 0.08f, _ => 0.65f };
        float expectedSun = lightingMode.ToLowerInvariant() switch { "night" => 0.45f, "dark" => 0.12f, _ => 1.2f };
        Godot.Environment environment = GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment
            ?? throw new InvalidOperationException("The editor world environment is missing.");
        DirectionalLight3D sun = GetNodeOrNull<DirectionalLight3D>("Sun")
            ?? throw new InvalidOperationException("The editor sun light is missing.");
        if (!ColorsMatch(environment.BackgroundColor, expectedBackground)
            || Math.Abs(environment.AmbientLightEnergy - expectedAmbient) > 0.001f
            || Math.Abs(sun.LightEnergy - expectedSun) > 0.001f)
            throw new InvalidOperationException(
                $"The scene visuals did not apply stage settings: sky={environment.BackgroundColor}, ambient={environment.AmbientLightEnergy}, sun={sun.LightEnergy}.");
    }

    private static bool ColorsMatch(Color left, Color right)
        => Math.Abs(left.R - right.R) <= 0.001f
            && Math.Abs(left.G - right.G) <= 0.001f
            && Math.Abs(left.B - right.B) <= 0.001f
            && Math.Abs(left.A - right.A) <= 0.001f;

    private async void RunWorldMapSizeSmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        string? temporaryDirectory = null;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || originalProjectPath is null)
                throw new InvalidOperationException("A project must be open for the map-size smoke run.");

            string sourcePath = ProjectSettings.GlobalizePath(originalProjectPath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The map-size smoke project was not found.", sourcePath);
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"GBNG-MapSize-Smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            string projectPath = Path.Combine(temporaryDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath || _terrain is null)
                throw new InvalidOperationException("The writable terrain-bearing map-size smoke project did not load.");

            float originalWidth = LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeX"], 500f);
            float originalDepth = LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeZ"], 500f);
            int originalTerrainWidth = _terrain.Width;
            int originalTerrainDepth = _terrain.Depth;
            int originalCenterX = originalTerrainWidth / 2;
            int originalCenterZ = originalTerrainDepth / 2;
            const byte originalShape = 0;
            const ushort originalStyle = 27;
            const byte originalDirection = 0;
            if (!_terrain.SetCell(originalCenterX, 19, originalCenterZ, originalShape, originalStyle, originalDirection))
                throw new InvalidOperationException("Could not install the centered terrain fixture in the writable smoke copy.");
            _terrain.FlushUpdates();
            _document.SaveTerrainPayload(_terrain.Encode());

            OpenWorldSettings();
            await WaitForUiFrames(2);
            LegacyWorldSettingsWindow window = _worldSettingsWindow;
            Button changeSize = (Button)window.FindChild("WorldMapSizeButton", true, false);
            changeSize.EmitSignal("pressed");
            await WaitForUiFrames(2);
            var dialog = (Window)window.FindChild("WorldMapSizeDialog", true, false);
            OptionButton choices = (OptionButton)dialog.FindChild("WorldMapSizeChoice", true, false);
            if (!dialog.Visible || choices.ItemCount != 4)
                throw new InvalidOperationException("The map-size dialog did not show all four legacy presets.");

            SelectWorldMapSize(choices, "small");
            ((Button)dialog.FindChild("WorldMapSizeCancel", true, false)).EmitSignal("pressed");
            await WaitForUiFrames(1);
            if (dialog.Visible
                || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeX"], 500f), originalWidth)
                || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeZ"], 500f), originalDepth)
                || _terrain.Width != originalTerrainWidth || _terrain.Depth != originalTerrainDepth)
                throw new InvalidOperationException("Canceling a map-size choice changed the project or terrain.");

            changeSize.EmitSignal("pressed");
            await WaitForUiFrames(1);
            dialog = (Window)window.FindChild("WorldMapSizeDialog", true, false);
            choices = (OptionButton)dialog.FindChild("WorldMapSizeChoice", true, false);
            SelectWorldMapSize(choices, "small");
            ((Button)dialog.FindChild("WorldMapSizeConfirm", true, false)).EmitSignal("pressed");
            await WaitForUiFrames(3);
            AssertWorldMapSize(50f, 50f, 20, 20, 10, 10, originalShape, originalStyle, originalDirection);

            UndoEdit();
            await WaitForUiFrames(3);
            AssertWorldMapSize(originalWidth, originalDepth, originalTerrainWidth, originalTerrainDepth, originalCenterX, originalCenterZ,
                originalShape, originalStyle, originalDirection);
            RedoEdit();
            await WaitForUiFrames(3);
            AssertWorldMapSize(50f, 50f, 20, 20, 10, 10, originalShape, originalStyle, originalDirection);

            changeSize.EmitSignal("pressed");
            await WaitForUiFrames(1);
            dialog = (Window)window.FindChild("WorldMapSizeDialog", true, false);
            choices = (OptionButton)dialog.FindChild("WorldMapSizeChoice", true, false);
            SelectWorldMapSize(choices, "long");
            await WaitForUiFrames(2);
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/world-map-size-smoke.png");
            if (dialog.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the map-size confirmation screenshot.");
            ((Button)dialog.FindChild("WorldMapSizeConfirm", true, false)).EmitSignal("pressed");
            await WaitForUiFrames(3);
            AssertWorldMapSize(50f, 5000f, 20, 2000, 10, 1000, originalShape, originalStyle, originalDirection);

            _document.Root.Remove("gridState");
            _document.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            AssertFlatWorldMapSize(50f, 5000f);

            OpenWorldSettings();
            changeSize.EmitSignal("pressed");
            await WaitForUiFrames(1);
            dialog = (Window)window.FindChild("WorldMapSizeDialog", true, false);
            choices = (OptionButton)dialog.FindChild("WorldMapSizeChoice", true, false);
            SelectWorldMapSize(choices, "small");
            ((Button)dialog.FindChild("WorldMapSizeConfirm", true, false)).EmitSignal("pressed");
            await WaitForUiFrames(2);
            AssertFlatWorldMapSize(50f, 50f);
            UndoEdit();
            await WaitForUiFrames(2);
            AssertFlatWorldMapSize(50f, 5000f);
            RedoEdit();
            await WaitForUiFrames(2);
            AssertFlatWorldMapSize(50f, 50f);
            GD.Print($"WORLD_MAP_SIZE_SMOKE_OK cancel=true presets=4 small=20x20 long=20x2000 centered-terrain=preserved flat-ground=updated undo=redo=persistence=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"WORLD_MAP_SIZE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            if (_worldSettingsWindow is not null)
            {
                ((Window)_worldSettingsWindow.FindChild("WorldMapSizeDialog", true, false)).Hide();
                _worldSettingsWindow.Hide();
            }
            if (originalProjectPath is not null)
            {
                LoadProject(originalProjectPath);
                await WaitForUiFrames(2);
                if (!string.Equals(_document?.SourcePath, originalProjectPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    exitCode = 1;
                    GD.PushError("WORLD_MAP_SIZE_SMOKE_FAILED: the original project could not be restored.");
                }
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private static void SelectWorldMapSize(OptionButton picker, string sizeId)
    {
        for (int index = 0; index < picker.ItemCount; index++)
        {
            if (picker.GetItemMetadata(index).AsString() != sizeId)
                continue;
            picker.Select(index);
            picker.EmitSignal("item_selected", (long)index);
            return;
        }
        throw new InvalidOperationException($"The map-size dialog did not expose the '{sizeId}' preset.");
    }

    private void AssertWorldMapSize(
        float expectedWidth,
        float expectedDepth,
        int expectedTerrainWidth,
        int expectedTerrainDepth,
        int cellX,
        int cellZ,
        byte expectedShape,
        ushort expectedStyle,
        byte expectedDirection,
        bool expectGroundBounds = true)
    {
        if (_document is null
            || _terrain is null
            || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeX"], 500f), expectedWidth)
            || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeZ"], 500f), expectedDepth)
            || _terrain.Width != expectedTerrainWidth
            || _terrain.Depth != expectedTerrainDepth
            || !_terrain.TryGetCell(cellX, 19, cellZ, out byte shape, out ushort style, out byte direction)
            || shape != expectedShape || style != expectedStyle || direction != expectedDirection)
            throw new InvalidOperationException("The map size or centered terrain data did not match the requested resize.");

        if (expectGroundBounds
            && (_stageGroundMesh is null
                || _stageGroundMesh.Mesh is not PlaneMesh ground
                || !Mathf.IsEqualApprox(ground.Size.X, expectedWidth)
                || !Mathf.IsEqualApprox(ground.Size.Y, expectedDepth)
                || _stageGroundMesh.Visible
                || _stageGroundBody is null
                || _stageGroundBody.GetChild(0) is not CollisionShape3D collision
                || collision.Shape is not BoxShape3D box
                || !Mathf.IsEqualApprox(box.Size.X, expectedWidth)
                || !Mathf.IsEqualApprox(box.Size.Z, expectedDepth)))
            throw new InvalidOperationException("The visible terrain ground or collision bounds did not adopt the selected map size.");
    }
    private void AssertFlatWorldMapSize(float expectedWidth, float expectedDepth)
    {
        if (_document is null
            || _terrain is not null
            || _document.GridState is not null
            || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeX"], 500f), expectedWidth)
            || !Mathf.IsEqualApprox(LegacySaveDocument.ReadFloat(_document.Stage?["groundSizeZ"], 500f), expectedDepth)
            || _stageGroundMesh?.Mesh is not PlaneMesh ground
            || !Mathf.IsEqualApprox(ground.Size.X, expectedWidth)
            || !Mathf.IsEqualApprox(ground.Size.Y, expectedDepth)
            || !_stageGroundMesh.Visible
            || _stageGroundBody?.GetChild(0) is not CollisionShape3D collision
            || collision.Shape is not BoxShape3D box
            || !Mathf.IsEqualApprox(box.Size.X, expectedWidth)
            || !Mathf.IsEqualApprox(box.Size.Z, expectedDepth))
            throw new InvalidOperationException("The flat ground and collision bounds did not adopt the selected map size.");
    }


    private async System.Threading.Tasks.Task RunTerrainEditorSmoke()
    {
        LegacyTerrainNode originalTerrain = _terrain
            ?? throw new InvalidOperationException("The sample project did not load its legacy terrain.");
        var targetCell = new Vector3I(4, 19, 4);
        var createdCell = new Vector3I(4, 20, 4);
        var fixtureData = LegacyTerrainData.CreateEmpty(8, 32, 8);
        if (!fixtureData.SetCell(targetCell.X, targetCell.Y, targetCell.Z, 0, 27, 0))
            throw new InvalidOperationException("Could not prepare the terrain editor smoke fixture.");
        LegacyTerrainNode terrain = LegacyTerrainNode.Create(fixtureData);
        _world.RemoveChild(originalTerrain);
        _terrain = terrain;
        _world.AddChild(terrain);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await WaitForUiFrames(2);

        const byte originalShape = 0;
        const ushort originalStyle = 27;
        const byte originalDirection = 0;
        TerrainCellSnapshot originalTargetValue = new(originalShape, originalStyle, originalDirection);
        TerrainCellSnapshot originalCreatedValue = new(-1, 0, 0);
        Transform3D oldCameraTransform = _camera.GlobalTransform;
        Vector3 target = terrain.GetCellCenterGlobal(targetCell.X, targetCell.Y, targetCell.Z);
        _camera.GlobalPosition = target + Vector3.Up * 12f;
        _camera.LookAt(target, Vector3.Forward);
        OpenTerrainEditor();
        await WaitForUiFrames(3);

        LegacyTerrainEditorWindow window = _terrainEditorWindow
            ?? throw new InvalidOperationException("The terrain editor window did not open.");
        if (!window.Visible)
            throw new InvalidOperationException("The terrain editor window is not visible.");
        window.Position = new Vector2I(20, 20);
        await WaitForUiFrames(2);
        window.SetBrushActive(true);
        Vector2 center = _camera.UnprojectPosition(terrain.GetCellCenterGlobal(targetCell.X, targetCell.Y, targetCell.Z));
        await WaitForUiFrames(2);
        if (!TryGetTerrainSelectionCellAtScreen(center, out Vector3I brushTarget) || brushTarget != targetCell)
            throw new InvalidOperationException($"Terrain fixture ray mismatch expected={targetCell} selected={brushTarget}.");
        OptionButton mode = (OptionButton)window.FindChild("TerrainMode", true, false);
        OptionButton shape = (OptionButton)window.FindChild("TerrainShape", true, false);
        OptionButton direction = (OptionButton)window.FindChild("TerrainDirection", true, false);
        OptionButton style = (OptionButton)window.FindChild("TerrainStyle", true, false);
        mode.Select((int)LegacyTerrainBrushMode.Dig);
        ApplyTerrainSmokeStroke(center);
        if (terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out _, out _, out _))
            throw new InvalidOperationException("The dig brush did not remove the targeted .voos terrain cell.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out byte restoredShape, out ushort restoredStyle, out byte restoredDirection)
            || restoredShape != originalShape || restoredStyle != originalStyle || restoredDirection != originalDirection)
            throw new InvalidOperationException("Terrain undo did not restore shape, style, and direction.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out _, out _, out _))
            throw new InvalidOperationException("Terrain redo did not reapply the dig stroke.");
        UndoEdit();
        await WaitForUiFrames(2);

        mode.Select((int)LegacyTerrainBrushMode.Paint);
        style.Select(24);
        ApplyTerrainSmokeStroke(center);
        if (!terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out byte paintedShape, out ushort paintedStyle, out byte paintedDirection)
            || paintedShape != originalShape || paintedStyle != 24 || paintedDirection != originalDirection)
            throw new InvalidOperationException("The paint brush did not preserve the cell shape and direction while changing style.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out _, out ushort undoneStyle, out _) || undoneStyle != originalStyle)
            throw new InvalidOperationException("Paint undo did not restore the original terrain style.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z, out _, out ushort redoneStyle, out _) || redoneStyle != 24)
            throw new InvalidOperationException("Paint redo did not restore the painted terrain style.");
        UndoEdit();
        await WaitForUiFrames(2);

        mode.Select((int)LegacyTerrainBrushMode.Create);
        shape.Select(3);
        direction.Select(2);
        style.Select(25);
        ApplyTerrainSmokeStroke(center);
        if (!terrain.TryGetCell(createdCell.X, createdCell.Y, createdCell.Z, out byte createdShape, out ushort createdStyle, out byte createdDirection)
            || createdShape != 3 || createdStyle != 25 || createdDirection != 2)
            throw new InvalidOperationException("The create brush did not add the configured adjacent .voos cell.");
        UndoEdit();
        await WaitForUiFrames(2);
        if (terrain.TryGetCell(createdCell.X, createdCell.Y, createdCell.Z, out _, out _, out _))
            throw new InvalidOperationException("Terrain creation undo did not remove its cell.");
        RedoEdit();
        await WaitForUiFrames(2);
        if (!terrain.TryGetCell(createdCell.X, createdCell.Y, createdCell.Z, out _, out _, out _))
            throw new InvalidOperationException("Terrain creation redo did not restore its cell.");
        UndoEdit();
        await WaitForUiFrames(2);
        await RunTerrainCopySmoke(terrain, window, mode, targetCell);


        bool hadGridState = _document!.Root.TryGetPropertyValue("gridState", out JsonNode? originalGridState);
        originalGridState = originalGridState?.DeepClone();
        _document.SaveTerrainPayload(terrain.Encode());
        LegacyTerrainData? roundTrip = LegacyTerrainData.Decode(_document.GridState);
        if (roundTrip is null)
            throw new InvalidOperationException("Terrain edits did not round-trip through the original .voos payload.");
        TerrainCellSnapshot savedTarget = roundTrip.TryGetCell(targetCell.X, targetCell.Y, targetCell.Z,
            out byte savedShape, out ushort savedStyle, out byte savedDirection)
            ? new TerrainCellSnapshot(savedShape, savedStyle, savedDirection)
            : new TerrainCellSnapshot(-1, 0, 0);
        TerrainCellSnapshot savedCreated = roundTrip.TryGetCell(createdCell.X, createdCell.Y, createdCell.Z,
            out byte savedCreatedShape, out ushort savedCreatedStyle, out byte savedCreatedDirection)
            ? new TerrainCellSnapshot(savedCreatedShape, savedCreatedStyle, savedCreatedDirection)
            : new TerrainCellSnapshot(-1, 0, 0);
        if (savedTarget != originalTargetValue || savedCreated != originalCreatedValue)
            throw new InvalidOperationException("Terrain edits did not restore and round-trip the original .voos cell values.");
        if (hadGridState)
            _document.Root["gridState"] = originalGridState;
        else
            _document.Root.Remove("gridState");
        _world.RemoveChild(terrain);
        terrain.QueueFree();
        _terrain = originalTerrain;
        _world.AddChild(originalTerrain);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);


        string screenshot = ProjectSettings.GlobalizePath("res://.godot/terrain-editor-smoke.png");
        if (window.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
            throw new InvalidOperationException("Could not save the terrain editor screenshot.");
        window.SetBrushActive(false);
        window.Hide();
        _camera.GlobalTransform = oldCameraTransform;
        UpdateOrbitCamera();
        ClearHistory();
        GD.Print($"TERRAIN_EDITOR_SMOKE_OK cell=({targetCell.X - terrain.Width / 2},{targetCell.Y + LegacyTerrainData.CellsYStart},{targetCell.Z - terrain.Depth / 2}) create=dig=paint undo=redo=.voos-roundtrip screenshot={screenshot}");
    }

    private void ApplyTerrainSmokeStroke(Vector2 screenPosition)
    {
        BeginTerrainStroke();
        ApplyTerrainBrush(screenPosition);
        EndTerrainStroke();
    }
    private async System.Threading.Tasks.Task RunTerrainCopySmoke(
        LegacyTerrainNode terrain,
        LegacyTerrainEditorWindow window,
        OptionButton mode,
        Vector3I sourceA)
    {
        Vector2I originalWindowPosition = window.Position;
        var sourceB = new Vector3I(sourceA.X + 1, sourceA.Y, sourceA.Z);
        var overwriteA = new Vector3I(sourceA.X + 2, sourceA.Y, sourceA.Z);
        var overwriteB = new Vector3I(sourceA.X + 3, sourceA.Y, sourceA.Z);
        var aboveA = new Vector3I(sourceA.X, sourceA.Y + 1, sourceA.Z);
        var aboveB = new Vector3I(sourceB.X, sourceB.Y + 1, sourceB.Z);
        var originalCells = new Dictionary<Vector3I, TerrainCellSnapshot>();
        for (int y = sourceA.Y; y < terrain.Data.Height; y++)
        {
            var sourceColumnA = new Vector3I(sourceA.X, y, sourceA.Z);
            var sourceColumnB = new Vector3I(sourceB.X, y, sourceB.Z);
            originalCells.Add(sourceColumnA, ReadTerrainCell(sourceColumnA));
            originalCells.Add(sourceColumnB, ReadTerrainCell(sourceColumnB));
        }
        originalCells.Add(overwriteA, ReadTerrainCell(overwriteA));
        originalCells.Add(overwriteB, ReadTerrainCell(overwriteB));

        void SetCell(Vector3I cell, TerrainCellSnapshot value)
        {
            if (!terrain.SetCell(cell.X, cell.Y, cell.Z, value.Shape, value.Style, value.Direction))
                throw new InvalidOperationException($"Could not prepare terrain-copy fixture cell {cell}.");
        }

        void AssertCell(Vector3I cell, TerrainCellSnapshot expected, string message)
        {
            if (ReadTerrainCell(cell) != expected)
                throw new InvalidOperationException(message);
        }

        try
        {
            if (originalCells[sourceA] != new TerrainCellSnapshot(0, 27, originalCells[sourceA].Direction))
                throw new InvalidOperationException("The terrain-copy source fixture cell changed.");

            var sourceBValue = new TerrainCellSnapshot(2, 25, 3);
            SetCell(sourceB, sourceBValue);
            for (int y = sourceA.Y + 1; y < terrain.Data.Height; y++)
            {
                SetCell(new Vector3I(sourceA.X, y, sourceA.Z), new TerrainCellSnapshot(-1, 0, 0));
                SetCell(new Vector3I(sourceB.X, y, sourceB.Z), new TerrainCellSnapshot(-1, 0, 0));
            }

            SetCell(overwriteA, new TerrainCellSnapshot(1, 24, 1));
            SetCell(overwriteB, new TerrainCellSnapshot(3, 28, 2));
            terrain.FlushUpdates();
            window.Position = new Vector2I(20, 20);
            await WaitForUiFrames(3);

            mode.Select((int)LegacyTerrainBrushMode.Select);
            Vector2 start = _camera.UnprojectPosition(terrain.GetCellCenterGlobal(sourceA.X, sourceA.Y, sourceA.Z));
            Vector2 end = _camera.UnprojectPosition(terrain.GetCellCenterGlobal(sourceB.X, sourceB.Y, sourceB.Z));
            if (!TryGetTerrainSelectionCellAtScreen(start, out Vector3I startCell) || startCell != sourceA
                || !TryGetTerrainSelectionCellAtScreen(end, out Vector3I endCell) || endCell != sourceB)
                throw new InvalidOperationException("Terrain selection screen rays did not resolve to the intended source cells.");

            BeginTerrainSelectionAtScreen(start);
            UpdateTerrainSelectionAtScreen(end);
            CompleteTerrainSelectionAtScreen(end);
            if (_terrainSelectionDragging || _terrainSelectionCellCount != 2
                || _terrainSelectionData is null
                || _terrainSelectionData.Width != 2
                || _terrainSelectionData.Height != 1
                || _terrainSelectionData.Depth != 1)
                throw new InvalidOperationException("Dragging across two occupied cells did not create the expected terrain selection.");

            Button copyButton = (Button)window.FindChild("TerrainCopySelectionButton", true, false);
            if (copyButton.Disabled)
                throw new InvalidOperationException("The terrain copy button remained disabled after selecting cells.");
            copyButton.EmitSignal("pressed");
            if (!_terrainCopyActive || _terrainCopyPreview is null
                || _terrainCopyPreview.GetNodeOrNull<MeshInstance3D>("TerrainMesh")?.Mesh is not ArrayMesh copyMesh
                || copyMesh.GetSurfaceCount() == 0)
                throw new InvalidOperationException("Copy selection did not create a visible terrain preview.");

            if (!TryGetTerrainPlacementCellAtScreen(start, out Vector3I emptyTarget)
                || emptyTarget != aboveA)
                throw new InvalidOperationException("The terrain copy fixture did not resolve the empty cell above its source.");
            UpdateTerrainCopyPreviewAtScreen(start);
            if (_terrainCopyTargetOrigin != emptyTarget)
                throw new InvalidOperationException("Moving the cursor did not position the terrain-copy preview at the snapped cell.");
            await WaitForUiFrames(2);
            string previewScreenshot = ProjectSettings.GlobalizePath("res://.godot/terrain-copy-preview-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(previewScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the terrain-copy preview screenshot.");

            PasteTerrainCopyAtScreen(start);
            AssertCell(aboveA, originalCells[sourceA], "Terrain paste did not preserve the source shape, style, and direction.");
            AssertCell(aboveB, sourceBValue, "Terrain paste did not copy the adjacent source cell.");
            AssertCell(sourceA, originalCells[sourceA], "Terrain paste changed its source cell.");
            AssertCell(sourceB, sourceBValue, "Terrain paste changed the adjacent source cell.");
            UndoEdit();
            await WaitForUiFrames(2);
            AssertCell(aboveA, originalCells[aboveA], "Terrain paste undo did not restore the first destination.");
            AssertCell(aboveB, originalCells[aboveB], "Terrain paste undo did not restore the second destination.");
            RedoEdit();
            await WaitForUiFrames(2);
            AssertCell(aboveA, originalCells[sourceA], "Terrain paste redo did not restore the first copied cell.");
            AssertCell(aboveB, sourceBValue, "Terrain paste redo did not restore the second copied cell.");
            UndoEdit();
            await WaitForUiFrames(2);

            Vector3I overwriteOrigin = overwriteA;
            SetTerrainCopyTargetOrigin(overwriteOrigin);
            if (!PasteTerrainCopyAtOrigin(overwriteOrigin))
                throw new InvalidOperationException("Pasting over existing terrain was rejected.");
            AssertCell(overwriteA, originalCells[sourceA], "Overwriting terrain did not copy the source cell data.");
            AssertCell(overwriteB, sourceBValue, "Overwriting terrain did not copy the adjacent source cell data.");
            UndoEdit();
            await WaitForUiFrames(2);
            AssertCell(overwriteA, new TerrainCellSnapshot(1, 24, 1), "Undo did not restore the first overwritten terrain cell.");
            AssertCell(overwriteB, new TerrainCellSnapshot(3, 28, 2), "Undo did not restore the second overwritten terrain cell.");
            RedoEdit();
            await WaitForUiFrames(2);
            AssertCell(overwriteA, originalCells[sourceA], "Redo did not restore the first overwritten terrain cell.");
            AssertCell(overwriteB, sourceBValue, "Redo did not restore the second overwritten terrain cell.");
            UndoEdit();
            await WaitForUiFrames(2);

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_terrainCopyActive || _terrainCopyPreview is not null || _terrainSelectionCellCount != 2)
                throw new InvalidOperationException("Esc did not cancel the copy preview while retaining the source selection.");
            Button clearButton = (Button)window.FindChild("TerrainClearSelectionButton", true, false);
            clearButton.EmitSignal("pressed");
            if (_terrainSelectionCellCount != 0 || _terrainSelectionPreview is not null || !copyButton.Disabled)
                throw new InvalidOperationException("Clear selection did not remove the selection preview and disable copy.");
            GD.Print($"TERRAIN_COPY_SMOKE_OK selection=dragged-two preview=visible paste=snapped-and-overwrite undo=redo cancel=escape clear=button screenshot={previewScreenshot}");
        }
        finally
        {
            ClearTerrainSelection();
            foreach ((Vector3I cell, TerrainCellSnapshot value) in originalCells)
                SetCell(cell, value);
            terrain.FlushUpdates();
            mode.Select((int)LegacyTerrainBrushMode.Create);
            window.Position = originalWindowPosition;
            await WaitForUiFrames(2);
        }
    }


    private async void RunAssetStoreSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null)
                throw new InvalidOperationException("The sample project did not load for the asset-store smoke run.");

            const string expectedUri = "builtin:BuiltinAssets/BubblegumDreams/Arch";
            int originalActorCount = _document.Actors.Count;
            OpenAssetStore();
            await WaitForUiFrames(3);
            if (!_assetStoreWindow.Visible)
                throw new InvalidOperationException("The offline asset store did not open.");

            LineEdit search = (LineEdit)_assetStoreWindow.FindChild("AssetStoreSearch", true, false);
            OptionButton category = (OptionButton)_assetStoreWindow.FindChild("AssetStoreCategory", true, false);
            ItemList assets = (ItemList)_assetStoreWindow.FindChild("AssetStoreItems", true, false);
            Button add = (Button)_assetStoreWindow.FindChild("AssetStoreAddActor", true, false);
            Label license = (Label)_assetStoreWindow.FindChild("AssetStoreLicenseDetails", true, false);
            var preview = (LegacyAssetPreviewControl)_assetStoreWindow.FindChild("LegacyAssetPreview", true, false);

            if (category.GetItemText(category.Selected) != "All categories")
                throw new InvalidOperationException("The asset store did not default to all categories.");

            search.Text = "BubblegumDreams/Arch";
            _assetStoreWindow.RefreshCatalog();
            int[] selected = assets.GetSelectedItems();
            if (assets.ItemCount != 1
                || selected.Length != 1
                || assets.GetItemMetadata(selected[0]).AsString() != expectedUri)
                throw new InvalidOperationException("Asset-store search did not isolate the requested bundled model.");
            if (!preview.Has3DPreview || !license.Text.Contains("Apache License 2.0", StringComparison.Ordinal))
                throw new InvalidOperationException("The bundled model preview or license evidence was not shown.");

            search.Text = "asset-that-does-not-exist";
            _assetStoreWindow.RefreshCatalog();
            if (assets.ItemCount != 0 || !add.Disabled)
                throw new InvalidOperationException("An empty asset search left a stale selection available to add.");

            search.Text = "BubblegumDreams/Arch";
            _assetStoreWindow.RefreshCatalog();
            add.EmitSignal("pressed");
            await WaitForUiFrames(3);
            if (_document.Actors.Count != originalActorCount + 1
                || _document.Actors[^1].RenderableUri != expectedUri)
                throw new InvalidOperationException("Adding the selected store asset did not create the expected actor.");

            await WaitForUiFrames(2);
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/asset-store-smoke.png");
            if (_assetStoreWindow.GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the asset-store screenshot.");
            _assetStoreWindow.Hide();
            GD.Print($"ASSET_STORE_SMOKE_OK uri={expectedUri} search=filtered preview=3d license=shown add=actor screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ASSET_STORE_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }
    private async void RunTerrainEditorSmokeStandalone()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(1);
            if (_document is null)
                throw new InvalidOperationException("The sample project did not load for the terrain smoke run.");
            await RunTerrainEditorSmoke();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"TERRAIN_EDITOR_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }


    private async void RunCardLibrarySmoke()
    {
        int exitCode = 0;
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_document is null || _behaviorRuntime is null)
                throw new InvalidOperationException("The sample project did not load for the smoke run.");
            await RunTerrainEditorSmoke();

            WorldActorNode? actor = FindActorWithCardDeck();
            if (actor is null)
                throw new InvalidOperationException("The sample has no actor with a card deck.");

            var canvas = new LegacyPanelCanvasWindow(
                _document,
                actor.State,
                () => { },
                uri => _behaviorRuntime.GetBehaviorPropertyDefinitions(uri),
                () => { });
            AddChild(canvas);
            canvas.PopupCentered(new Vector2I(1100, 760));
            await WaitForUiFrames(3);

            Button toggle = (Button)canvas.FindChild("ToggleCardLibrary", true, false);
            toggle.EmitSignal("pressed");
            ItemList items = (ItemList)canvas.FindChild("CardLibraryItems", true, false);
            LineEdit search = (LineEdit)canvas.FindChild("CardLibrarySearch", true, false);
            OptionButton categoryPicker = (OptionButton)canvas.FindChild("CardLibraryCategory", true, false);
            if (items.ItemCount == 0)
                throw new InvalidOperationException("The searchable card library rendered no choices.");

            string firstChoice = items.GetItemMetadata(0).AsString();
            string filter = items.GetItemText(0)[..Math.Min(3, items.GetItemText(0).Length)].ToLowerInvariant();
            search.Text = filter;
            InvokeSmoke(canvas, "RefreshCardLibrary");
            if (Enumerable.Range(0, items.ItemCount).All(index => items.GetItemMetadata(index).AsString() != firstChoice))
                throw new InvalidOperationException("Case-insensitive title search removed its matching card.");

            bool categoryReturnedCards = false;
            for (int index = 1; index < categoryPicker.ItemCount; index++)
            {
                categoryPicker.Select(index);
                InvokeSmoke(canvas, "RefreshCardLibrary");
                if (items.ItemCount > 0)
                {
                    categoryReturnedCards = true;
                    break;
                }
            }
            if (!categoryReturnedCards)
                throw new InvalidOperationException("No card category returned results.");

            categoryPicker.Select(0);
            search.Text = "";
            InvokeSmoke(canvas, "RefreshCardLibrary");
            await WaitForUiFrames(2);

            var hiddenMetadata = (Dictionary<string, JsonObject?>)typeof(LegacyPanelCanvasWindow)
                .GetField("_builtinMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(canvas)!;
            MethodInfo isHiddenMethod = typeof(LegacyPanelCanvasWindow)
                .GetMethod("IsHiddenMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
            var choices = Enumerable.Range(0, items.ItemCount)
                .Select(index => items.GetItemMetadata(index).AsString())
                .ToHashSet(StringComparer.Ordinal);
            int hiddenDefinitions = 0;
            foreach ((string uri, JsonObject? metadata) in hiddenMetadata)
            {
                JsonObject? cardData = metadata?["cardSystemCardData"] as JsonObject;
                if (cardData is null || !(bool)isHiddenMethod.Invoke(null, [cardData])!)
                    continue;
                hiddenDefinitions++;
                if (choices.Contains($"behavior:{uri}"))
                    throw new InvalidOperationException($"Hidden card '{uri}' appeared as a new library choice.");
            }

            var dropTargets = Descendants(canvas)
                .OfType<Button>()
                .Where(button => button.Name.ToString().StartsWith("CardLibraryDrop_", StringComparison.Ordinal))
                .ToArray();
            if (dropTargets.Length == 0)
                throw new InvalidOperationException("The sample canvas rendered no deck drop targets.");

            Button? acceptedTarget = null;
            Variant acceptedData = default;
            for (int index = 0; index < items.ItemCount && acceptedTarget is null; index++)
            {
                items.Select(index);
                items.EnsureCurrentIsVisible();
                await WaitForUiFrames(1);
                Rect2 itemRect = items.GetItemRect(index);
                Variant data = (Variant)InvokeSmoke(items, "_GetDragData", itemRect.GetCenter())!;
                if (data.VariantType != Variant.Type.String)
                    continue;
                foreach (Button target in dropTargets)
                {
                    if (!(bool)InvokeSmoke(target, "_CanDropData", Vector2.Zero, data)!)
                        continue;
                    acceptedTarget = target;
                    acceptedData = data;
                    break;
                }
            }
            if (acceptedTarget is null)
                throw new InvalidOperationException("No visible library card was accepted by a compatible deck.");

            JsonObject brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
            JsonArray uses = (JsonArray)brain["behaviorUses"]!;
            HashSet<string> useIdsBefore = uses.OfType<JsonObject>()
                .Select(use => LegacySaveDocument.ReadString(use["id"]))
                .ToHashSet(StringComparer.Ordinal);
            string targetName = acceptedTarget.Name;
            InvokeSmoke(acceptedTarget, "_DropData", Vector2.Zero, acceptedData);
            brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
            uses = (JsonArray)brain["behaviorUses"]!;
            (string panelUseId, string propertyName) = ParseDeckTargetName(targetName);
            JsonObject panelUse = FindUseSmoke(uses, panelUseId)
                ?? throw new InvalidOperationException("The deck target panel disappeared after the drop.");
            JsonArray assignedIds = (JsonArray)InvokeSmoke(canvas, "ReadAssignedValue", panelUse, propertyName)!;
            string assignedUseId = assignedIds.Select(node => LegacySaveDocument.ReadString(node))
                .FirstOrDefault(id => id.Length > 0 &&
                    (acceptedData.AsString().StartsWith("use:", StringComparison.Ordinal)
                        ? id == acceptedData.AsString()["use:".Length..]
                        : !useIdsBefore.Contains(id)
                            && LegacySaveDocument.ReadString(FindUseSmoke(uses, id)?["behaviorUri"]) == acceptedData.AsString()["behavior:".Length..]))
                ?? throw new InvalidOperationException("Dropping the library card did not persist its use ID in the .voos deck assignment.");

            int moveCount = 0;
            if (acceptedData.AsString().StartsWith("behavior:", StringComparison.Ordinal))
            {
                string useChoice = $"use:{assignedUseId}";
                foreach (Button target in Descendants(canvas).OfType<Button>()
                    .Where(button => button.Name.ToString().StartsWith("CardLibraryDrop_", StringComparison.Ordinal)))
                {
                    if (!(bool)InvokeSmoke(target, "_CanDropData", Vector2.Zero, Variant.From(useChoice))!)
                        continue;
                    string newTargetName = target.Name;
                    InvokeSmoke(target, "_DropData", Vector2.Zero, Variant.From(useChoice));
                    brain = (JsonObject)InvokeSmoke(canvas, "EnsureWritableBrain")!;
                    uses = (JsonArray)brain["behaviorUses"]!;
                    (string newPanelId, string newPropertyName) = ParseDeckTargetName(newTargetName);
                    JsonObject newPanel = FindUseSmoke(uses, newPanelId)
                        ?? throw new InvalidOperationException("The use-move destination panel disappeared.");
                    JsonArray movedIds = (JsonArray)InvokeSmoke(canvas, "ReadAssignedValue", newPanel, newPropertyName)!;
                    if (!movedIds.Any(node => LegacySaveDocument.ReadString(node) == assignedUseId)
                        || FindUseSmoke(uses, assignedUseId) is null)
                        throw new InvalidOperationException("Moving an assigned use changed or lost its .voos use ID.");
                    moveCount++;
                    break;
                }
            }

            await RunCardManagerSmoke(canvas);
            await WaitForUiFrames(3);
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/card-library-smoke.png");
            Error screenshotError = GetViewport().GetTexture().GetImage().SavePng(screenshotPath);
            if (screenshotError != Error.Ok)
                throw new InvalidOperationException($"Could not save the card library screenshot: {screenshotError}.");
            await RunCustomAssetImportSmoke();
            GD.Print($"CARD_LIBRARY_SMOKE_OK cards={choices.Count} hiddenDefinitions={hiddenDefinitions} drop={acceptedData.AsString()} movedUse={moveCount} screenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CARD_LIBRARY_SMOKE_FAILED: {exception}");
        }
        GetTree().Quit(exitCode);
    }
    private async void RunAssetImportSmoke()
    {
        int exitCode = 0;
        try
        {
            await RunCustomAssetImportSmoke();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ASSET_IMPORT_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }
    private async void RunCodeEditorSmoke()
    {
        int exitCode = 0;
        LegacyCardLibraryManagerWindow? manager = null;
        LegacyBehaviorAuthoringWindow? behaviorEditor = null;
        try
        {
            LegacySaveDocument document = _document ?? throw new InvalidOperationException("The code-editor smoke project did not load.");
            behaviorEditor = new LegacyBehaviorAuthoringWindow(
                document,
                document.Actors[0],
                () => { },
                _ => null,
                ValidateBehaviorSourceForEditor);
            AddChild(behaviorEditor);
            behaviorEditor.PopupCentered(new Vector2I(1000, 700));
            await WaitForUiFrames(2);
            InvokeSmoke(behaviorEditor, "CreateCustomBehavior");
            JsonObject selectedUse = (JsonObject)InvokeSmoke(behaviorEditor, "GetSelectedUse")!;
            string behaviorUri = LegacySaveDocument.ReadString(selectedUse["behaviorUri"]);
            if (!behaviorUri.StartsWith("embedded:", StringComparison.Ordinal))
                throw new InvalidOperationException("The behavior editor smoke did not create an embedded behavior.");
            string behaviorValidationId = behaviorUri["embedded:".Length..];
            string originalSource = LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorValidationId)?["javascript"]);
            var behaviorCode = (LegacyJavaScriptCodeEdit)behaviorEditor.FindChild("BehaviorJavaScript", true, false);
            int defaultFontSize = behaviorCode.GetThemeFontSize("font_size");
            var behaviorZoomIn = (Button)behaviorEditor.FindChild("BehaviorCodeZoomIn", true, false);
            var behaviorZoomOut = (Button)behaviorEditor.FindChild("BehaviorCodeZoomOut", true, false);
            var behaviorZoomReset = (Button)behaviorEditor.FindChild("BehaviorCodeZoomReset", true, false);
            behaviorZoomIn.EmitSignal("pressed");
            int enlargedFontSize = behaviorCode.GetThemeFontSize("font_size");
            if (enlargedFontSize <= defaultFontSize || enlargedFontSize > 36)
                throw new InvalidOperationException("The behavior editor zoom-in control did not increase the bounded code font size.");
            await WaitForUiFrames(1);
            string zoomScreenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-zoom-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(zoomScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the JavaScript zoom screenshot.");
            behaviorZoomOut.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") != defaultFontSize)
                throw new InvalidOperationException("One zoom-in followed by one zoom-out did not restore the previous code font size.");
            behaviorZoomOut.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") >= defaultFontSize)
                throw new InvalidOperationException("The behavior editor zoom-out control did not reduce the code font size.");
            behaviorZoomReset.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") != defaultFontSize)
                throw new InvalidOperationException("Reset zoom did not restore the theme's default code font size.");
            for (int index = 0; index < 32; index++)
                behaviorZoomOut.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") != 8)
                throw new InvalidOperationException("The behavior editor zoom-out limit was not enforced at 8 points.");
            for (int index = 0; index < 32; index++)
                behaviorZoomIn.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") != 36)
                throw new InvalidOperationException("The behavior editor zoom-in limit was not enforced at 36 points.");
            behaviorZoomReset.EmitSignal("pressed");
            if (behaviorCode.GetThemeFontSize("font_size") != defaultFontSize)
                throw new InvalidOperationException("Reset zoom did not restore the baseline after reaching a font-size limit.");
            string firstUseId = LegacySaveDocument.ReadString(selectedUse["id"]);
            InvokeSmoke(behaviorEditor, "CreateCustomBehavior");
            JsonObject secondUse = (JsonObject)InvokeSmoke(behaviorEditor, "GetSelectedUse")!;
            string secondUseId = LegacySaveDocument.ReadString(secondUse["id"]);
            string secondBehaviorUri = LegacySaveDocument.ReadString(secondUse["behaviorUri"]);
            if (!secondBehaviorUri.StartsWith("embedded:", StringComparison.Ordinal))
                throw new InvalidOperationException("The second smoke behavior was not embedded.");
            string secondBehaviorId = secondBehaviorUri["embedded:".Length..];
            var assignedUses = (ItemList)behaviorEditor.FindChild("AssignedBehaviorUses", true, false);
            int firstUseIndex = -1;
            int secondUseIndex = -1;
            for (int index = 0; index < assignedUses.ItemCount; index++)
            {
                string useId = assignedUses.GetItemMetadata(index).AsString();
                if (useId == firstUseId)
                    firstUseIndex = index;
                if (useId == secondUseId)
                    secondUseIndex = index;
            }
            if (firstUseIndex < 0 || secondUseIndex < 0 || firstUseIndex == secondUseIndex)
                throw new InvalidOperationException("The behavior editor did not list both custom behaviors for the scroll restoration smoke.");
            var scrollLines = new string[64];
            for (int index = 0; index < scrollLines.Length; index++)
                scrollLines[index] = $"// Scroll persistence line {index + 1}";
            scrollLines[^1] = "export function onCollision(msg) {}";
            string scrollTestSource = string.Join("\n", scrollLines);
            behaviorCode.Text = scrollTestSource;
            InvokeSmoke(behaviorEditor, "ApplyScript");
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(secondBehaviorId)?["javascript"]) != scrollTestSource)
                throw new InvalidOperationException("The long behavior source was not applied for the scroll restoration smoke.");
            await WaitForUiFrames(1);
            behaviorCode.ScrollVertical = 20d;
            await WaitForUiFrames(1);
            double savedScroll = behaviorCode.ScrollVertical;
            if (savedScroll < 10d)
                throw new InvalidOperationException($"The scroll smoke source did not scroll far enough to verify restoration: {savedScroll}.");
            assignedUses.Select(firstUseIndex);
            InvokeSmoke(behaviorEditor, "SelectAssignedUse", (long)firstUseIndex);
            if (behaviorCode.Text == scrollTestSource)
                throw new InvalidOperationException("Selecting another behavior did not replace the editor source.");
            assignedUses.Select(secondUseIndex);
            InvokeSmoke(behaviorEditor, "SelectAssignedUse", (long)secondUseIndex);
            await WaitForUiFrames(1);
            if (behaviorCode.Text != scrollTestSource || Math.Abs(behaviorCode.ScrollVertical - savedScroll) > 0.001d)
                throw new InvalidOperationException($"Switching behavior uses did not restore the second behavior's source and scroll offset: {behaviorCode.ScrollVertical}.");
            string scrollScreenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-scroll-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(scrollScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the behavior scroll restoration screenshot.");
            assignedUses.Select(firstUseIndex);
            InvokeSmoke(behaviorEditor, "SelectAssignedUse", (long)firstUseIndex);
            if (behaviorCode.Text != originalSource)
                throw new InvalidOperationException("Returning to the first behavior did not restore its source.");
            behaviorCode.Text = "export function onCollision(msg) {\n  const broken = ;\n}";
            InvokeSmoke(behaviorEditor, "ApplyScript");
            var diagnostics = (Label)behaviorEditor.FindChild("BehaviorScriptDiagnostics", true, false);
            if (!diagnostics.Visible || !diagnostics.Text.Contains("Line 2:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid JavaScript did not produce a line-numbered editor diagnostic: '{diagnostics.Text}'.");
            int syntaxErrorGutter = FindDiagnosticGutter(behaviorCode);
            if (syntaxErrorGutter < 0
                || behaviorCode.GetLineGutterText(1, syntaxErrorGutter) != "!"
                || behaviorCode.GetLineBackgroundColor(1).A <= 0f)
                throw new InvalidOperationException("The JavaScript syntax diagnostic did not mark its source line in the CodeEdit gutter and background.");
            Vector2 syntaxDiagnosticPoint = GetEditorPositionOnLine(behaviorCode, 1);
            Vector2 normalLinePoint = GetEditorPositionOnLine(behaviorCode, 0);
            string syntaxTooltip = behaviorCode.GetTooltip(syntaxDiagnosticPoint);
            if (syntaxTooltip != diagnostics.Text
                || behaviorCode.GetTooltip(normalLinePoint).Length != 0)
                throw new InvalidOperationException("The syntax diagnostic tooltip did not match the marked source line.");
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorValidationId)?["javascript"]) != originalSource)
                throw new InvalidOperationException("Invalid JavaScript replaced the last valid embedded behavior source.");
            await WaitForUiFrames(1);
            string diagnosticScreenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-diagnostics-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(diagnosticScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the line-numbered JavaScript diagnostic screenshot.");

            const string validBehaviorSource = "export function onCollision(msg) { broadcast('EditorDiagnosticsSmoke'); }";
            int textLengthBeforeEdit = behaviorCode.Text.Length;
            behaviorCode.GrabFocus();
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = Key.X,
                Unicode = 'x',
                Pressed = true
            });
            await WaitForUiFrames(1);
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = Key.X,
                Unicode = 'x',
                Pressed = false
            });
            int clearedDiagnosticGutter = FindDiagnosticGutter(behaviorCode);
            Vector2 clearedDiagnosticPoint = GetEditorPositionOnLine(behaviorCode, 1);
            if (behaviorCode.Text.Length != textLengthBeforeEdit + 1
                || diagnostics.Visible
                || diagnostics.Text.Length != 0
                || clearedDiagnosticGutter < 0
                || behaviorCode.GetLineGutterText(1, clearedDiagnosticGutter) != ""
                || behaviorCode.GetGutterWidth(clearedDiagnosticGutter) != 0
                || behaviorCode.GetLineBackgroundColor(1).A > 0f
                || behaviorCode.GetTooltip(clearedDiagnosticPoint).Length != 0)
                throw new InvalidOperationException("Typing in the JavaScript editor did not update the source and clear its stale diagnostic.");

            behaviorCode.Text = validBehaviorSource;
            InvokeSmoke(behaviorEditor, "ApplyScript");
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorValidationId)?["javascript"]) != validBehaviorSource)
                throw new InvalidOperationException("Valid JavaScript was not applied after fixing the syntax error.");
            const string throwingBehaviorSource = "export function onTick() {\n  throw new Error('RuntimeEditorSmoke');\n}";
            behaviorCode.Text = throwingBehaviorSource;
            InvokeSmoke(behaviorEditor, "ApplyScript");
            var runtimeSmoke = (LegacyBehaviorRuntime)InvokeSmoke(this, "CreateBehaviorRuntime", document)!;
            try
            {
                runtimeSmoke.Tick(1d / 60d);
            }
            finally
            {
                runtimeSmoke.Dispose();
            }
            if (!diagnostics.Visible
                || !diagnostics.Text.Contains("Line 2:", StringComparison.Ordinal)
                || !diagnostics.Text.Contains("line 2:9", StringComparison.Ordinal)
                || !diagnostics.Text.Contains("RuntimeEditorSmoke", StringComparison.Ordinal))
                throw new InvalidOperationException($"Runtime JavaScript exceptions did not reach the open behavior editor with their source line: '{diagnostics.Text}'.");
            int runtimeErrorGutter = FindDiagnosticGutter(behaviorCode);
            if (runtimeErrorGutter < 0
                || behaviorCode.GetLineGutterText(1, runtimeErrorGutter) != "!"
                || behaviorCode.GetLineBackgroundColor(1).A <= 0f)
                throw new InvalidOperationException("The JavaScript runtime exception did not mark its source line in the CodeEdit gutter and background.");
            Vector2 runtimeDiagnosticPoint = GetEditorPositionOnLine(behaviorCode, 1);
            if (!behaviorCode.GetTooltip(runtimeDiagnosticPoint)
                .Contains("RuntimeEditorSmoke", StringComparison.Ordinal))
                throw new InvalidOperationException("The runtime diagnostic tooltip did not expose the exception message on its source line.");
            await WaitForUiFrames(1);
            if (GetViewport().GetTexture().GetImage().SavePng(diagnosticScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the runtime JavaScript diagnostic screenshot.");
            behaviorEditor.QueueFree();
            behaviorEditor = null;
            await WaitForUiFrames(1);

            manager = new LegacyCardLibraryManagerWindow(
                document,
                () => { });
            AddChild(manager);
            manager.PopupCentered(new Vector2I(1100, 680));
            await WaitForUiFrames(2);
            ((Button)manager.FindChild("CreateEmbeddedCard", true, false)).EmitSignal("pressed");
            await WaitForUiFrames(1);

            ItemList cards = (ItemList)manager.FindChild("ManagedCards", true, false);
            int[] selected = cards.GetSelectedItems();
            if (selected.Length != 1)
                throw new InvalidOperationException("The code-editor smoke card was not selected.");
            string uri = cards.GetItemMetadata(selected[0]).AsString();
            if (!uri.StartsWith("embedded:", StringComparison.Ordinal))
                throw new InvalidOperationException("The code-editor smoke card was not embedded in the project.");
            string behaviorId = uri["embedded:".Length..];
            var editor = (LegacyJavaScriptCodeEdit)manager.FindChild("ManagedCardJavaScript", true, false);
            int managedDefaultFontSize = editor.GetThemeFontSize("font_size");
            ((Button)manager.FindChild("ManagedCardCodeZoomIn", true, false)).EmitSignal("pressed");
            if (editor.GetThemeFontSize("font_size") <= managedDefaultFontSize)
                throw new InvalidOperationException("The card-library editor zoom-in control did not increase the code font size.");
            ((Button)manager.FindChild("ManagedCardCodeZoomReset", true, false)).EmitSignal("pressed");
            if (editor.GetThemeFontSize("font_size") != managedDefaultFontSize)
                throw new InvalidOperationException("The card-library editor reset control did not restore the default code font size.");
            Label cardDiagnostics = (Label)manager.FindChild("ManagedCardScriptDiagnostics", true, false);
            string originalCardSource = LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorId)?["javascript"]);
            editor.Text = "export function onCollision(msg) {\n  const broken = ;\n}";
            InvokeSmoke(manager, "ApplyCode");
            int cardDiagnosticGutter = FindDiagnosticGutter(editor);
            Vector2 cardErrorPoint = GetEditorPositionOnLine(editor, 1);
            if (!cardDiagnostics.Visible
                || !cardDiagnostics.Text.Contains("Line 2:", StringComparison.Ordinal)
                || cardDiagnosticGutter < 0
                || editor.GetLineGutterText(1, cardDiagnosticGutter) != "!"
                || !editor.GetTooltip(cardErrorPoint).Contains("SyntaxError", StringComparison.Ordinal)
                || LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorId)?["javascript"]) != originalCardSource)
                throw new InvalidOperationException("Invalid managed-card JavaScript was not rejected with a line diagnostic while preserving the last valid source.");
            await WaitForUiFrames(1);
            string managedCardDiagnosticsScreenshot = ProjectSettings.GlobalizePath("res://.godot/managed-card-diagnostics-smoke.png");
            if (manager.GetViewport().GetTexture().GetImage().SavePng(managedCardDiagnosticsScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the managed-card diagnostics screenshot.");
            int cardTextLengthBeforeEdit = editor.Text.Length;
            editor.SetCaretLine(1);
            editor.SetCaretColumn(0);
            editor.GrabFocus();
            editor.InsertTextAtCaret("x");
            await WaitForUiFrames(1);
            int clearedCardDiagnosticGutter = FindDiagnosticGutter(editor);
            Vector2 clearedCardDiagnosticPoint = GetEditorPositionOnLine(editor, 1);
            if (editor.Text.Length != cardTextLengthBeforeEdit + 1
                || cardDiagnostics.Visible
                || cardDiagnostics.Text.Length != 0
                || clearedCardDiagnosticGutter < 0
                || editor.GetLineGutterText(1, clearedCardDiagnosticGutter) != ""
                || editor.GetGutterWidth(clearedCardDiagnosticGutter) != 0
                || editor.GetLineBackgroundColor(1).A > 0f
                || editor.GetTooltip(clearedCardDiagnosticPoint).Length != 0)
                throw new InvalidOperationException("Editing the managed-card source did not clear its stale syntax diagnostic.");
            editor.Text = "mys";
            editor.SetCaretLine(0);
            editor.SetCaretColumn(editor.Text.Length);
            editor.GrabFocus();
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = Key.Space,
                Pressed = true,
                CtrlPressed = true
            });
            await WaitForUiFrames(1);
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = Key.Space,
                Pressed = false,
                CtrlPressed = true
            });
            Godot.Collections.Array<Godot.Collections.Dictionary> options = editor.GetCodeCompletionOptions();
            int myselfIndex = -1;
            for (int index = 0; index < options.Count; index++)
            {
                if (options[index]["display_text"].AsString() == "myself")
                {
                    myselfIndex = index;
                    break;
                }
            }
            if (myselfIndex < 0)
                throw new InvalidOperationException("The native editor did not suggest the documented Game Builder API function 'myself'.");
            editor.SetCodeCompletionSelectedIndex(myselfIndex);
            editor.ConfirmCodeCompletion();
            await WaitForUiFrames(1);
            if (!editor.Text.Contains("myself()", StringComparison.Ordinal))
                throw new InvalidOperationException("Accepting a Game Builder API completion did not insert its function call.");

            const string appliedSource = "export function onCollision(msg) { broadcast('NativeEditorSmoke'); }";
            editor.Text = appliedSource;
            InvokeSmoke(manager, "ApplyCode");
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(behaviorId)?["javascript"]) != appliedSource)
                throw new InvalidOperationException("CodeEdit source was not applied to the embedded behavior.");

            string uiButtonBoundsScreenshot = await RunBehaviorUiButtonBoundsSmoke();
            string uiCircleQualityScreenshot = await RunBehaviorUiCircleQualitySmoke();
            string uiFractionalTextSizeScreenshot = await RunBehaviorUiFractionalTextSizeSmoke();
            string uiImageFilterScreenshot = await RunBehaviorUiImageFilterSmoke();
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/code-editor-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the native code-editor screenshot.");
            GD.Print($"CODE_EDITOR_SMOKE_OK completion=myself zoom=true zoomBounds=8-36 inlineDiagnostics=true scrollRestoration=true syntaxDiagnostics=true runtimeDiagnostics=true managedCardValidation=true managedCardDiagnostics=true applied=true uiButtonHalfOpenBounds=true uiButtonBoundsScreenshot={uiButtonBoundsScreenshot} uiImageFilter=true uiImageSliceFilter=true uiCircleAdaptiveDetail=true uiCircleQualityScreenshot={uiCircleQualityScreenshot} uiFractionalTextSize=true uiFractionalTextSizeScreenshot={uiFractionalTextSizeScreenshot} uiImageFilterScreenshot={uiImageFilterScreenshot} zoomScreenshot={zoomScreenshot} scrollScreenshot={scrollScreenshot} diagnosticsScreenshot={diagnosticScreenshot} managedCardDiagnosticsScreenshot={managedCardDiagnosticsScreenshot} screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CODE_EDITOR_SMOKE_FAILED: {exception}");
        }
        finally
        {
            manager?.QueueFree();
            behaviorEditor?.QueueFree();
            await WaitForUiFrames(1);
            GetTree().Quit(exitCode);
        }
    }


    private async System.Threading.Tasks.Task<string> RunBehaviorUiImageFilterSmoke()
    {
        const string textureUri = "user://behavior-ui-filter-smoke.tres";
        string texturePath = ProjectSettings.GlobalizePath(textureUri);
        var image = Image.CreateEmpty(2, 1, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(1f, 0f, 0f, 1f));
        image.SetPixel(1, 0, new Color(0f, 0f, 1f, 1f));
        if (ResourceSaver.Save(ImageTexture.CreateFromImage(image), textureUri) != Error.Ok)
            throw new InvalidOperationException("Could not save the generated behavior UI filter-smoke texture.");

        SubViewport? viewport = null;
        try
        {
            if (ResourceLoader.Load<Texture2D>(textureUri) is null)
                throw new InvalidOperationException("The generated behavior UI filter-smoke texture could not be loaded.");

            viewport = new SubViewport
            {
                Name = "BehaviorUiImageFilterSmokeViewport",
                Size = new Vector2I(512, 256),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            var behaviorUi = new LegacyBehaviorUi();
            viewport.AddChild(behaviorUi);
            behaviorUi.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            JsonObject CreateCommand(int commandId, float x, float y, bool noFilter)
            {
                var rect = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = 600f, ["h"] = 100f };
                var command = new JsonObject
                {
                    ["cmd"] = commandId,
                    ["imageId"] = textureUri,
                    ["rect"] = rect,
                    ["noFilter"] = noFilter
                };
                if (commandId == 8)
                    command["srcRect"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["w"] = 2f, ["h"] = 1f };
                return command;
            }

            behaviorUi.SetCommands(new JsonArray
            {
                CreateCommand(4, 50f, 20f, false),
                CreateCommand(4, 850f, 20f, true),
                CreateCommand(8, 50f, 200f, false),
                CreateCommand(8, 850f, 200f, true)
            });
            await WaitForUiFrames(2);

            Image rendered = viewport.GetTexture().GetImage();
            Color linearImage = rendered.GetPixel(112, 22);
            Color nearestImage = rendered.GetPixel(368, 22);
            Color linearSlice = rendered.GetPixel(112, 80);
            Color nearestSlice = rendered.GetPixel(368, 80);
            static bool IsInterpolated(Color color) => color.R > 0.1f && color.B > 0.1f;
            static bool IsPointSample(Color color) =>
                (color.R > 0.9f && color.B < 0.1f) || (color.B > 0.9f && color.R < 0.1f);
            if (!IsInterpolated(linearImage) || !IsPointSample(nearestImage)
                || !IsInterpolated(linearSlice) || !IsPointSample(nearestSlice))
                throw new InvalidOperationException(
                    $"Behavior UI image filtering differed from the legacy contract: image linear={linearImage}, nearest={nearestImage}; slice linear={linearSlice}, nearest={nearestSlice}.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-ui-filter-smoke.png");
            if (rendered.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the behavior UI image-filter screenshot.");
            return screenshot;
        }
        finally
        {
            viewport?.QueueFree();
            await WaitForUiFrames(1);
            if (File.Exists(texturePath))
                File.Delete(texturePath);
        }
    }

    private async System.Threading.Tasks.Task<string> RunBehaviorUiFractionalTextSizeSmoke()
    {
        const int viewportWidth = 6400;
        var viewport = new SubViewport
        {
            Name = "BehaviorUiFractionalTextSizeSmokeViewport",
            Size = new Vector2I(viewportWidth, 900),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };

        try
        {
            AddChild(viewport);
            var behaviorUi = new LegacyBehaviorUi();
            viewport.AddChild(behaviorUi);
            behaviorUi.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            JsonObject CreateTextCommand(float y, float size) => new()
            {
                ["cmd"] = 1,
                ["text"] = "MMMMMMMMMMMM",
                ["rect"] = new JsonObject { ["x"] = 20f, ["y"] = y, ["w"] = 1f, ["h"] = 1f },
                ["textSize"] = size,
                ["textColor"] = 0xffffff,
                ["opacity"] = 1f
            };
            behaviorUi.SetCommands(new JsonArray
            {
                CreateTextCommand(10f, 40.01f),
                CreateTextCommand(80f, 40.49f)
            });
            await WaitForUiFrames(2);

            Image rendered = viewport.GetTexture().GetImage();
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-ui-text-size-smoke.png");
            if (rendered.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the behavior UI text-size screenshot.");
            int smallerTextWidth = GetOpaqueTextWidth(rendered, 0, 250);
            int largerTextWidth = GetOpaqueTextWidth(rendered, 250, 600);
            if (largerTextWidth <= smallerTextWidth)
                throw new InvalidOperationException($"Fractional behavior UI text sizes collapsed to the same rendered size: 40.01 width={smallerTextWidth}, 40.49 width={largerTextWidth}. Screenshot: {screenshot}");
            float expectedLargeTextWidth = (11 * 44f + 46f) * 40.49f / 100f * (viewportWidth / 1600f);
            if (Math.Abs(largerTextWidth - expectedLargeTextWidth) > 28f)
                throw new InvalidOperationException($"Behavior UI text does not match the legacy ProggyClean glyph advance: expected≈{expectedLargeTextWidth}, rendered={largerTextWidth}. Screenshot: {screenshot}");
            return screenshot;
        }
        finally
        {
            viewport.QueueFree();
            await WaitForUiFrames(1);
        }
    }

    private static int GetOpaqueTextWidth(Image image, int startY, int endY)
    {
        int minX = image.GetWidth();
        int maxX = -1;
        for (int y = startY; y < Math.Min(endY, image.GetHeight()); y++)
        {
            for (int x = 0; x < image.GetWidth(); x++)
            {
                if (image.GetPixel(x, y).A <= 0.5f)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }
        return maxX >= minX ? maxX - minX + 1 : 0;
    }

    private async System.Threading.Tasks.Task<string> RunBehaviorUiCircleQualitySmoke()
    {
        const int borderSampleX = 749;
        const int borderSampleY = 473;
        const float borderCenterX = 400f;
        const float borderCenterY = 450f;
        float borderOffsetX = borderSampleX + 0.5f - borderCenterX;
        float borderOffsetY = borderSampleY + 0.5f - borderCenterY;
        float borderRadius = MathF.Sqrt(borderOffsetX * borderOffsetX + borderOffsetY * borderOffsetY);
        const int dashedSampleX = 1547;
        const int dashedSampleY = 499;
        const float dashedCenterX = 1200f;
        const float dashedCenterY = 450f;
        float dashedOffsetX = dashedSampleX + 0.5f - dashedCenterX;
        float dashedOffsetY = dashedSampleY + 0.5f - dashedCenterY;
        float dashedRadius = MathF.Sqrt(dashedOffsetX * dashedOffsetX + dashedOffsetY * dashedOffsetY);
        var viewport = new SubViewport
        {
            Name = "BehaviorUiCircleQualitySmokeViewport",
            Size = new Vector2I(1600, 900),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };

        try
        {
            AddChild(viewport);
            var behaviorUi = new LegacyBehaviorUi();
            viewport.AddChild(behaviorUi);
            behaviorUi.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            JsonObject CreateCircleCommand(float centerX, float centerY, float radius, string style, int color) => new()
            {
                ["cmd"] = 5,
                ["points"] = new JsonArray { new JsonObject { ["x"] = centerX, ["y"] = centerY } },
                ["radius"] = radius,
                ["style"] = style,
                ["backgroundColor"] = color,
                ["opacity"] = 1f
            };
            behaviorUi.SetCommands(new JsonArray
            {
                CreateCircleCommand(borderCenterX, borderCenterY, borderRadius, "BORDER", 0xff0000),
                CreateCircleCommand(dashedCenterX, dashedCenterY, dashedRadius, "DASHED", 0x0000ff)
            });
            await WaitForUiFrames(2);

            Image rendered = viewport.GetTexture().GetImage();
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-ui-circle-quality-smoke.png");
            if (rendered.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the behavior UI circle-quality screenshot.");
            Color borderSample = rendered.GetPixel(borderSampleX, borderSampleY);
            Color dashedSample = rendered.GetPixel(dashedSampleX, dashedSampleY);
            if (borderSample.A < 0.5f || dashedSample.A < 0.5f)
                throw new InvalidOperationException($"Adaptive UI circle detail missed its target samples: border={borderSample}, dashed={dashedSample}. Screenshot: {screenshot}");
            return screenshot;
        }
        finally
        {
            viewport.QueueFree();
            await WaitForUiFrames(1);
        }
    }

    private async System.Threading.Tasks.Task<string> RunBehaviorUiButtonBoundsSmoke()
    {
        var viewport = new SubViewport
        {
            Name = "BehaviorUiButtonBoundsSmokeViewport",
            Size = new Vector2I(1600, 900),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        try
        {
            AddChild(viewport);
            var behaviorUi = new LegacyBehaviorUi();
            viewport.AddChild(behaviorUi);
            behaviorUi.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            int clickCount = 0;
            string clickedActor = "";
            string clickedMessage = "";
            string clickedArgument = "";
            behaviorUi.SetMessageHandler((actorName, messageName, argumentJson) =>
            {
                clickCount++;
                clickedActor = actorName;
                clickedMessage = messageName;
                clickedArgument = argumentJson;
            });
            behaviorUi.SetCommands(new JsonArray
            {
                new JsonObject
                {
                    ["cmd"] = 3,
                    ["actorName"] = "ButtonBoundsActor",
                    ["clickMessageName"] = "BoundsClicked",
                    ["clickMessageArgJson"] = "{\"test\":true}",
                    ["rect"] = new JsonObject { ["x"] = 100f, ["y"] = 100f, ["w"] = 200f, ["h"] = 100f },
                    ["text"] = "Bounds",
                    ["backgroundColor"] = 0x336699,
                    ["textColor"] = 0xffffff,
                    ["textSize"] = 32f,
                    ["opacity"] = 1f
                }
            });
            await WaitForUiFrames(2);

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/behavior-ui-button-bounds-smoke.png");
            if (viewport.GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the behavior UI button-bounds screenshot.");
            void Click(float x, float y) => behaviorUi._GuiInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = new Vector2(x, y)
            });

            Click(100f, 100f);
            if (clickCount != 1)
                throw new InvalidOperationException("The behavior button's inclusive top-left boundary did not dispatch its click.");
            Click(300f, 150f);
            if (clickCount != 1)
                throw new InvalidOperationException("The behavior button incorrectly accepted a click on its exclusive right boundary.");
            Click(150f, 200f);
            if (clickCount != 1)
                throw new InvalidOperationException("The behavior button incorrectly accepted a click on its exclusive bottom boundary.");
            Click(299.99f, 199.99f);
            if (clickCount != 2
                || clickedActor != "ButtonBoundsActor"
                || clickedMessage != "BoundsClicked"
                || clickedArgument != "{\"test\":true}")
                throw new InvalidOperationException("An interior behavior-button click did not dispatch its configured actor, message, and argument.");
            return screenshot;
        }
        finally
        {
            viewport.QueueFree();
            await WaitForUiFrames(1);
        }
    }

    private async System.Threading.Tasks.Task RunCustomAssetImportSmoke()
    {
        string originalProjectPath = OS.GetCmdlineUserArgs()
            .First(argument => argument.EndsWith(".voos", StringComparison.OrdinalIgnoreCase));
        string temporaryDirectory = Path.Combine(OS.GetUserDataDir(), $"asset-import-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string projectPath = Path.Combine(temporaryDirectory, "asset-smoke.voos");
        try
        {
            _document!.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable asset-import smoke project did not load.");

            OpenAssetStore();
            await WaitForUiFrames(2);
            Button storeImport = (Button)_assetStoreWindow.FindChild("AssetStoreImportModel", true, false);
            bool useNativeDialog = _importModelDialog.UseNativeDialog;
            _importModelDialog.UseNativeDialog = false;
            storeImport.EmitSignal("pressed");
            await WaitForUiFrames(2);
            if (!_importModelDialog.Visible)
                throw new InvalidOperationException("The store's local-import action did not open the model chooser.");
            _importModelDialog.Hide();
            _importModelDialog.UseNativeDialog = useNativeDialog;

            string sourceModelPath = Path.Combine(temporaryDirectory, "smoke-model.glb");
            var sourceRoot = new Node3D { Name = "ImportedSmokeModel" };
            sourceRoot.AddChild(new MeshInstance3D
            {
                Name = "SmokeBox",
                Mesh = new BoxMesh { Size = new Vector3(1f, 2f, 3f) }
            });
            var exportDocument = new GltfDocument();
            var exportState = new GltfState();
            Error appendResult = exportDocument.AppendFromScene(sourceRoot, exportState);
            sourceRoot.Free();
            if (appendResult != Error.Ok)
                throw new InvalidOperationException($"Could not generate the GLB smoke fixture: {appendResult}.");
            Error writeResult = exportDocument.WriteToFilesystem(exportState, sourceModelPath);
            if (writeResult != Error.Ok)
                throw new InvalidOperationException($"Could not write the GLB smoke fixture: {writeResult}.");

            ImportModelFromPath(sourceModelPath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported model was not selected in the asset browser.");
            string customUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            string gltfUri = "";
            string fbxUri = "";
            if (!customUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(customUri, out RenderableEntry importedEntry)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException("The imported GLB was not catalogued and rendered in the 3D preview.");
            if (!File.Exists(importedEntry.ModelPaths[0]))
                throw new InvalidOperationException("The imported GLB was not copied into the project sidecar.");

            OpenAssetStore();
            await WaitForUiFrames(2);
            LineEdit storeSearch = (LineEdit)_assetStoreWindow.FindChild("AssetStoreSearch", true, false);
            ItemList storeItems = (ItemList)_assetStoreWindow.FindChild("AssetStoreItems", true, false);
            storeSearch.Text = "smoke-model";
            _assetStoreWindow.RefreshCatalog();
            int[] storeSelection = storeItems.GetSelectedItems();
            Label storeLicense = (Label)_assetStoreWindow.FindChild("AssetStoreLicenseDetails", true, false);
            var storePreview = (LegacyAssetPreviewControl)_assetStoreWindow.FindChild("LegacyAssetPreview", true, false);
            if (storeItems.ItemCount != 1
                || storeSelection.Length != 1
                || storeItems.GetItemMetadata(storeSelection[0]).AsString() != customUri
                || !storePreview.Has3DPreview
                || !storeLicense.Text.Contains("does not verify its license", StringComparison.Ordinal))
                throw new InvalidOperationException("The imported model or its unverified-license warning did not appear in the store.");
            _assetStoreWindow.Hide();
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri))
                throw new InvalidOperationException("Adding the imported asset did not create a rendered actor.");
            string customImageUri = await RunCustomImageImportSmoke(temporaryDirectory);
            string sourceGltfPath = Path.Combine(temporaryDirectory, "smoke-model.gltf");
            var gltfRoot = new Node3D { Name = "ImportedGltfSmokeModel" };
            gltfRoot.AddChild(new MeshInstance3D
            {
                Name = "SmokeSphere",
                Mesh = new SphereMesh { Radius = 0.75f, Height = 1.5f }
            });
            var gltfExportDocument = new GltfDocument();
            var gltfExportState = new GltfState();
            Error gltfAppendResult = gltfExportDocument.AppendFromScene(gltfRoot, gltfExportState);
            gltfRoot.Free();
            if (gltfAppendResult != Error.Ok)
                throw new InvalidOperationException($"Could not generate the glTF smoke fixture: {gltfAppendResult}.");
            Error gltfWriteResult = gltfExportDocument.WriteToFilesystem(gltfExportState, sourceGltfPath);
            if (gltfWriteResult != Error.Ok)
                throw new InvalidOperationException($"Could not write the glTF smoke fixture: {gltfWriteResult}.");

            ImportModelFromPath(sourceGltfPath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported glTF was not selected in the asset browser.");
            gltfUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            if (gltfUri == customUri
                || !gltfUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(gltfUri, out RenderableEntry gltfEntry)
                || !Path.GetExtension(gltfEntry.ModelPaths[0]).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException("The imported glTF was not bundled as a self-contained GLB and previewed.");
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(gltfUri))
                throw new InvalidOperationException("Adding the imported glTF did not create a rendered actor.");

            string fbxSourcePath = Path.GetFullPath(Path.Combine(
                ProjectSettings.GlobalizePath("res://"),
                "Assets",
                "GameAssets",
                "Models",
                "NPCs",
                "Fox",
                "FoxNPC_RunWalkLEFT.fbx"));
            if (!File.Exists(fbxSourcePath))
                throw new InvalidOperationException($"The animated FBX smoke fixture is missing: {fbxSourcePath}.");
            ImportModelFromPath(fbxSourcePath);
            if (_assetPicker.Selected < 0)
                throw new InvalidOperationException("The imported FBX was not selected in the asset browser.");
            fbxUri = _assetPicker.GetItemMetadata(_assetPicker.Selected).AsString();
            if (fbxUri == customUri
                || fbxUri == gltfUri
                || !fbxUri.StartsWith("custom:", StringComparison.Ordinal)
                || !_catalog.TryGet(fbxUri, out RenderableEntry fbxEntry)
                || !Path.GetExtension(fbxEntry.ModelPaths[0]).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                || !_assetPreview.Has3DPreview)
                throw new InvalidOperationException($"The imported FBX was not bundled as a GLB and rendered in the 3D preview: {_errorDialog.DialogText}");

            Node3D? fbxInstance = LegacyRenderableCatalog.LoadModelInstance(fbxEntry.ModelPaths[0]);
            if (fbxInstance is null)
                throw new InvalidOperationException("The converted FBX could not be instantiated.");
            bool hasImportedAnimation = fbxInstance
                .FindChildren("*", nameof(AnimationPlayer), true, false)
                .OfType<AnimationPlayer>()
                .Any(player => player.GetAnimationList().Length > 0);
            fbxInstance.Free();
            if (!hasImportedAnimation)
                throw new InvalidOperationException("The converted FBX lost its animation clips.");
            AddActor();
            await WaitForUiFrames(2);
            if (!HasImportedActor(fbxUri))
                throw new InvalidOperationException("Adding the imported FBX did not create a rendered actor.");


            SaveTo(projectPath);
            if (!_status.Text.StartsWith("Saved ", StringComparison.Ordinal))
                throw new InvalidOperationException("Saving the project with imported models failed.");
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri) || !HasImportedActor(gltfUri) || !HasImportedActor(fbxUri) || !HasImportedImage(customImageUri))
                throw new InvalidOperationException("Imported model or image references did not survive a .voos reload.");

            string copyPath = Path.Combine(temporaryDirectory, "asset-smoke-copy.voos");
            SaveTo(copyPath);
            if (!_status.Text.StartsWith("Saved ", StringComparison.Ordinal)
                || !File.Exists(Path.Combine(temporaryDirectory, "asset-smoke-copy.assets", "manifest.json")))
                throw new InvalidOperationException("Save As did not copy the imported model sidecar.");
            LoadProject(copyPath);
            await WaitForUiFrames(2);
            if (!HasImportedActor(customUri) || !HasImportedActor(gltfUri) || !HasImportedActor(fbxUri) || !HasImportedImage(customImageUri))
                throw new InvalidOperationException("Imported model or image references did not survive Save As and reload.");

            string screenshot = ProjectSettings.GlobalizePath("res://.godot/imported-asset-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the imported-asset browser screenshot.");
            GD.Print($"CUSTOM_ASSET_SMOKE_OK glb={customUri} gltf={gltfUri} animatedFbx={fbxUri} image={customImageUri} uiImage=uiImageSlice=assetStore=propImage=sidecar=save-as screenshot={screenshot}");
        }
        finally
        {
            LoadProject(originalProjectPath);
            await WaitForUiFrames(2);
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private bool HasImportedActor(string customUri)
    {
        WorldActorNode? actor = _actors.FirstOrDefault(candidate => candidate.RenderableUri == customUri);
        return actor is not null && actor.FindChildren("*", "MeshInstance3D", true, false).Count > 0;
    }
    private async System.Threading.Tasks.Task<string> RunCustomImageImportSmoke(string temporaryDirectory)
    {
        string sourceImagePath = Path.Combine(temporaryDirectory, "smoke-ui-image.png");
        Image fixture = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
                fixture.SetPixel(x, y, new Color(0f, 1f, 0f, 1f));
        }
        if (fixture.SavePng(sourceImagePath) != Error.Ok)
            throw new InvalidOperationException("Could not generate the local image-import smoke fixture.");

        OpenAssetStore();
        await WaitForUiFrames(2);
        Button imageImportButton = (Button)_assetStoreWindow.FindChild("AssetStoreImportImage", true, false);
        bool previousNativeDialog = _importImageDialog.UseNativeDialog;
        _importImageDialog.UseNativeDialog = false;
        try
        {
            imageImportButton.EmitSignal("pressed");
            await WaitForUiFrames(2);
            if (!_importImageDialog.Visible)
                throw new InvalidOperationException("The store's local-image action did not open the image chooser.");
        }
        finally
        {
            _importImageDialog.Hide();
            _importImageDialog.UseNativeDialog = previousNativeDialog;
        }

        HashSet<string> existingUris = _catalog.Entries.Select(entry => entry.Uri).ToHashSet(StringComparer.Ordinal);
        ImportImageFromPath(sourceImagePath);
        RenderableEntry importedImage = _catalog.Entries
            .Single(entry => !existingUris.Contains(entry.Uri) && entry.ImagePaths.Length == 1 && entry.ModelPaths.Length == 0);
        string imageUri = importedImage.Uri;
        string imagePath = importedImage.ImagePaths[0];
        string expectedAssetDirectory = Path.GetFullPath(Path.Combine(temporaryDirectory, "asset-smoke.assets"));
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!File.Exists(imagePath) || !string.Equals(Path.GetDirectoryName(imagePath), expectedAssetDirectory, pathComparison))
            throw new InvalidOperationException("The imported image was not stored as a local project asset.");
        string manifestPath = Path.Combine(temporaryDirectory, "asset-smoke.assets", "manifest.json");
        JsonObject? manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        bool manifestContainsImage = manifest?["assets"] is JsonArray manifestAssets
            && manifestAssets.OfType<JsonObject>().Any(asset =>
                LegacySaveDocument.ReadString(asset["uri"]) == imageUri
                && LegacySaveDocument.ReadString(asset["file"]) == Path.GetFileName(imagePath));
        if (!manifestContainsImage)
            throw new InvalidOperationException("The imported image was not recorded in the project sidecar manifest.");

        OpenAssetStore();
        await WaitForUiFrames(2);
        LineEdit imageStoreSearch = (LineEdit)_assetStoreWindow.FindChild("AssetStoreSearch", true, false);
        ItemList imageStoreItems = (ItemList)_assetStoreWindow.FindChild("AssetStoreItems", true, false);
        Label imageStoreLicense = (Label)_assetStoreWindow.FindChild("AssetStoreLicenseDetails", true, false);
        TextureRect imageStorePreview = (TextureRect)_assetStoreWindow.FindChild("AssetImagePreview", true, false);
        Button addAsActor = (Button)_assetStoreWindow.FindChild("AssetStoreAddActor", true, false);
        imageStoreSearch.Text = "smoke-ui-image";
        _assetStoreWindow.RefreshCatalog();
        await WaitForUiFrames(2);
        int[] imageSelection = imageStoreItems.GetSelectedItems();
        if (imageStoreItems.ItemCount != 1
            || imageSelection.Length != 1
            || imageStoreItems.GetItemMetadata(imageSelection[0]).AsString() != imageUri
            || !imageStorePreview.Visible
            || imageStorePreview.Texture is null
            || !imageStoreLicense.Text.Contains("does not verify its license", StringComparison.Ordinal)
            || !addAsActor.Disabled)
            throw new InvalidOperationException("The imported image preview, license warning, or actor-ineligible state did not appear in the offline asset store.");

        int actorCount = _document!.Actors.Count;
        if (AddActor(imageUri) || _document.Actors.Count != actorCount)
            throw new InvalidOperationException("An image-only asset was incorrectly added as a world actor.");
        _assetStoreWindow.Hide();

        LegacyBehaviorAuthoringWindow imageEditor = new(
            _document,
            _document.Actors[0],
            () => { },
            _ => null,
            (_, _) => null,
            _catalog);
        AddChild(imageEditor);
        SubViewport? viewport = null;
        try
        {
            var imagePicker = (OptionButton)InvokeSmoke(
                imageEditor,
                "CreatePropertyEditor",
                new JsonObject(),
                "Image",
                JsonValue.Create(imageUri)!)!;
            int imageChoice = -1;
            bool hasBundledImage = false;
            for (int index = 0; index < imagePicker.ItemCount; index++)
            {
                string value = imagePicker.GetItemMetadata(index).AsString();
                if (value == imageUri)
                    imageChoice = index;
                if (value.StartsWith("res://", StringComparison.Ordinal))
                    hasBundledImage = true;
            }
            if (imageChoice < 0 || !hasBundledImage)
                throw new InvalidOperationException("The Image behavior-property picker did not offer imported and built-in local images.");
            imagePicker.Select(imageChoice);
            JsonNode? assignedImage = InvokeSmoke(imageEditor, "ReadEditorValue", "image", "Image", imagePicker) as JsonNode;
            if (LegacySaveDocument.ReadString(assignedImage) != imageUri)
                throw new InvalidOperationException("Selecting an imported image did not persist its project-local URI as the Image property value.");

            viewport = new SubViewport
            {
                Name = "CustomImageBehaviorUiSmokeViewport",
                Size = new Vector2I(512, 256),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            var behaviorUi = new LegacyBehaviorUi(_catalog);
            viewport.AddChild(behaviorUi);
            behaviorUi.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            JsonObject CreateImageCommand(int commandId, float x)
            {
                var command = new JsonObject
                {
                    ["cmd"] = commandId,
                    ["imageId"] = imageUri,
                    ["rect"] = new JsonObject { ["x"] = x, ["y"] = 20f, ["w"] = 50f, ["h"] = 50f }
                };
                if (commandId == 8)
                    command["srcRect"] = new JsonObject { ["x"] = 0f, ["y"] = 0f, ["w"] = 2f, ["h"] = 2f };
                return command;
            }
            behaviorUi.SetCommands(new JsonArray
            {
                CreateImageCommand(4, 50f),
                CreateImageCommand(8, 200f)
            });
            await WaitForUiFrames(2);
            Image rendered = viewport.GetTexture().GetImage();
            Color imagePixel = rendered.GetPixel(22, 12);
            Color slicePixel = rendered.GetPixel(70, 12);
            static bool IsFixtureGreen(Color color) =>
                color.G > 0.9f && color.R < 0.1f && color.B < 0.1f && color.A > 0.9f;
            if (!IsFixtureGreen(imagePixel) || !IsFixtureGreen(slicePixel))
                throw new InvalidOperationException($"Project-local image rendering failed: uiImage={imagePixel}, uiImageSlice={slicePixel}.");
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/custom-image-behavior-ui-smoke.png");
            if (rendered.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the imported-image behavior UI screenshot.");
            GD.Print($"CUSTOM_IMAGE_SMOKE_OK uri={imageUri} storePreview=true licenseWarning=true propImage=true uiImage=true uiImageSlice=true screenshot={screenshot}");
        }
        finally
        {
            viewport?.QueueFree();
            imageEditor.QueueFree();
            await WaitForUiFrames(1);
        }
        return imageUri;
    }

    private bool HasImportedImage(string uri)
    {
        return _catalog.TryGet(uri, out RenderableEntry entry)
            && entry.ModelPaths.Length == 0
            && entry.ImagePaths.Length == 1
            && File.Exists(entry.ImagePaths[0]);
    }

    private async System.Threading.Tasks.Task RunCardManagerSmoke(LegacyPanelCanvasWindow canvas)
    {
        Button manageButton = (Button)canvas.FindChild("ManageEmbeddedCards", true, false);
        manageButton.EmitSignal("pressed");
        await WaitForUiFrames(2);
        LegacyCardLibraryManagerWindow manager = canvas.GetChildren()
            .OfType<LegacyCardLibraryManagerWindow>()
            .Single();
        var conflictDialog = (ConfirmationDialog)typeof(LegacyCardLibraryManagerWindow)
            .GetField("_conflictDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        ItemList cards = (ItemList)manager.FindChild("ManagedCards", true, false);
        string managerScreenshot = ProjectSettings.GlobalizePath("res://.godot/card-manager-smoke.png");
        if (manager.GetViewport().GetTexture().GetImage().SavePng(managerScreenshot) != Error.Ok)
            throw new InvalidOperationException("Could not save the card manager screenshot.");

        JsonObject database = _document!.BehaviorDatabase
            ?? throw new InvalidOperationException("The card demo has no behavior database.");
        JsonArray ids = (JsonArray)database["behaviorIds"]!;
        int initialBehaviorCount = ids.Count;
        ((Button)manager.FindChild("CreateEmbeddedCard", true, false)).EmitSignal("pressed");
        if (ids.Count != initialBehaviorCount + 1)
            throw new InvalidOperationException("Creating a card did not add an embedded behavior.");
        string createdUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
        if (!createdUri.StartsWith("embedded:", StringComparison.Ordinal))
            throw new InvalidOperationException("The new card was not selected as an embedded behavior.");
        string createdId = createdUri["embedded:".Length..];

        ((LineEdit)manager.FindChild("ManagedCardTitle", true, false)).Text = "Smoke Edited Card";
        ((LineEdit)manager.FindChild("ManagedCardCategories", true, false)).Text = "Custom, Smoke";
        ((LineEdit)manager.FindChild("ManagedCardImagePath", true, false)).Text = "BuiltinAssets/CardImages/placeholder";
        ((TextEdit)manager.FindChild("ManagedCardDescription", true, false)).Text = "Edited through the manager.";
        InvokeSmoke(manager, "ApplyMetadata");
        ((TextEdit)manager.FindChild("ManagedCardJavaScript", true, false)).Text =
            "export function onCollision(msg) { broadcast('SmokeEdited'); }";
        InvokeSmoke(manager, "ApplyCode");
        JsonObject createdBehavior = FindEmbeddedBehaviorSmoke(createdId)
            ?? throw new InvalidOperationException("The edited embedded card disappeared.");
        JsonObject createdMetadata = JsonNode.Parse(LegacySaveDocument.ReadString(createdBehavior["metadataJson"]))!.AsObject();
        JsonObject cardData = (JsonObject)createdMetadata["cardSystemCardData"]!;
        if (LegacySaveDocument.ReadString(cardData["title"]) != "Smoke Edited Card"
            || LegacySaveDocument.ReadString(createdBehavior["javascript"]) != "export function onCollision(msg) { broadcast('SmokeEdited'); }")
            throw new InvalidOperationException("Card metadata or JavaScript edits were not persisted in the .voos behavior record.");

        string builtinUri = Enumerable.Range(0, cards.ItemCount)
            .Select(index => cards.GetItemMetadata(index).AsString())
            .FirstOrDefault(uri => uri.StartsWith("builtin:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No built-in card was available to copy.");
        SelectManagedCard(manager, builtinUri);
        int beforeCopy = ids.Count;
        InvokeSmoke(manager, "CopySelectedCard");
        if (ids.Count != beforeCopy + 1)
            throw new InvalidOperationException("Copying a built-in card did not create an embedded card.");
        string copiedUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
        JsonObject copiedBehavior = FindEmbeddedBehaviorSmoke(copiedUri["embedded:".Length..])
            ?? throw new InvalidOperationException("The copied built-in card could not be found.");
        JsonObject copiedMetadata = JsonNode.Parse(LegacySaveDocument.ReadString(copiedBehavior["metadataJson"]))!.AsObject();
        if (!LegacySaveDocument.ReadString((copiedMetadata["cardSystemCardData"] as JsonObject)?["title"]).StartsWith("Copy of ", StringComparison.Ordinal))
            throw new InvalidOperationException("The built-in card copy did not retain editable card metadata.");

        string exportRoot = ProjectSettings.GlobalizePath("res://.godot/card-pack-smoke");
        if (Directory.Exists(exportRoot))
            Directory.Delete(exportRoot, recursive: true);
        Directory.CreateDirectory(exportRoot);
        try
        {
            SelectManagedCard(manager, createdUri);
            InvokeSmoke(manager, "ExportDirectorySelected", exportRoot);
            string packDirectory = Directory.GetDirectories(exportRoot).Single();
            string sourceFile = Directory.GetFiles(packDirectory, "*.js", SearchOption.AllDirectories).Single();
            string packScript = "export function onCollision(msg) { broadcast('PackOverwrite'); }";
            File.WriteAllText(sourceFile, packScript);
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            InvokeSmoke(manager, "CompleteImport", true);
            conflictDialog.Hide();
            if (LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(createdId)?["javascript"]) != packScript)
                throw new InvalidOperationException("The overwrite conflict choice did not replace the existing card data.");

            int beforeDuplicate = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            InvokeSmoke(manager, "CompleteImport", false);
            conflictDialog.Hide();
            if (ids.Count != beforeDuplicate + 1)
                throw new InvalidOperationException("The duplicate conflict choice did not allocate a fresh embedded ID.");
            string duplicateUri = cards.GetItemMetadata(cards.GetSelectedItems()[0]).AsString();
            if (!duplicateUri.StartsWith("embedded:", StringComparison.Ordinal)
                || duplicateUri == createdUri
                || LegacySaveDocument.ReadString(FindEmbeddedBehaviorSmoke(duplicateUri["embedded:".Length..])?["javascript"]) != packScript)
                throw new InvalidOperationException("The duplicated pack card lost its content or retained the conflicting ID.");

            int beforeCancel = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", packDirectory);
            conflictDialog.EmitSignal("custom_action", new StringName("cancel"));
            conflictDialog.Hide();
            if (ids.Count != beforeCancel)
                throw new InvalidOperationException("Cancelling a card-pack conflict changed the project.");

            string freshPack = Path.Combine(exportRoot, "fresh-pack");
            Directory.CreateDirectory(freshPack);
            string modernId = Guid.NewGuid().ToString("N");
            string modernCard = Path.Combine(freshPack, "Modern card");
            Directory.CreateDirectory(modernCard);
            File.WriteAllText(Path.Combine(modernCard, $"{modernId}.js"), "export function onCollision(msg) { broadcast('ModernPack'); }");
            File.WriteAllText(Path.Combine(modernCard, $"{modernId}.json"), CreateSmokeCardMetadata("Modern imported card"));
            string legacyId = Guid.NewGuid().ToString("N");
            string legacyCard = Path.Combine(freshPack, legacyId);
            Directory.CreateDirectory(legacyCard);
            File.WriteAllText(Path.Combine(legacyCard, "source.js"), "export function onCollision(msg) { broadcast('LegacyPack'); }");
            File.WriteAllText(Path.Combine(legacyCard, "metadata.json"), CreateSmokeCardMetadata("Legacy imported card"));
            int beforeFreshImport = ids.Count;
            InvokeSmoke(manager, "ImportDirectorySelected", freshPack);
            if (ids.Count != beforeFreshImport + 2
                || FindEmbeddedBehaviorSmoke(modernId) is null
                || FindEmbeddedBehaviorSmoke(legacyId) is null)
                throw new InvalidOperationException("Modern and legacy card-pack folders did not import their cards.");

            JsonArray brains = (JsonArray)database["brains"]!;
            JsonObject targetBrain = brains.OfType<JsonObject>()
                .FirstOrDefault(brain => brain["behaviorUses"] is JsonArray)
                ?? throw new InvalidOperationException("The sample has no behavior brain for the delete-reference check.");
            JsonArray brainUses = (JsonArray)targetBrain["behaviorUses"]!;
            brainUses.Add(new JsonObject { ["id"] = "smoke-card-use", ["behaviorUri"] = createdUri });
            SelectManagedCard(manager, createdUri);
            InvokeSmoke(manager, "ConfirmDeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(createdId) is null)
                throw new InvalidOperationException("A card referenced by a behavior brain was deleted.");
            brainUses.RemoveAt(brainUses.Count - 1);

            SelectManagedCard(manager, duplicateUri);
            InvokeSmoke(manager, "DeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(duplicateUri["embedded:".Length..]) is not null)
                throw new InvalidOperationException("An unused embedded card could not be deleted.");
            SelectManagedCard(manager, copiedUri);
            InvokeSmoke(manager, "DeleteSelectedCard");
            if (FindEmbeddedBehaviorSmoke(copiedUri["embedded:".Length..]) is not null)
                throw new InvalidOperationException("An unused built-in copy could not be deleted.");
        }
        finally
        {
            if (Directory.Exists(exportRoot))
                Directory.Delete(exportRoot, recursive: true);
        }
        manager.QueueFree();
        await WaitForUiFrames(1);
        GD.Print($"CARD_MANAGER_SMOKE_OK embeddedCreated={createdId} copy={copiedUri} importOverwrite=duplicate=cancel=modern+legacy deleteReference=protected screenshot={managerScreenshot}");
    }

    private static void SelectManagedCard(LegacyCardLibraryManagerWindow manager, string uri)
    {
        ItemList cards = (ItemList)manager.FindChild("ManagedCards", true, false);
        for (int index = 0; index < cards.ItemCount; index++)
        {
            if (cards.GetItemMetadata(index).AsString() != uri)
                continue;
            cards.Select(index);
            InvokeSmoke(manager, "OnCardSelected", (long)index);
            return;
        }
        throw new InvalidOperationException($"Managed card '{uri}' was not listed.");
    }

    private JsonObject? FindEmbeddedBehaviorSmoke(string id)
    {
        if (_document?.BehaviorDatabase is not JsonObject database
            || database["behaviorIds"] is not JsonArray ids
            || database["behaviors"] is not JsonArray behaviors)
            return null;
        int index = FindStringIndexSmoke(ids, id);
        return index >= 0 && index < behaviors.Count ? behaviors[index] as JsonObject : null;
    }

    private static int FindStringIndexSmoke(JsonArray values, string expected)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (LegacySaveDocument.ReadString(values[index]) == expected)
                return index;
        }
        return -1;
    }

    private static string CreateSmokeCardMetadata(string title)
        => new JsonObject
        {
            ["cardSystemCardData"] = new JsonObject
            {
                ["isCard"] = true,
                ["title"] = title,
                ["categories"] = new JsonArray { "Custom" },
                ["description"] = "Pack import verification.",
                ["imageResourcePath"] = ""
            }
        }.ToJsonString();

    private WorldActorNode? FindActorWithCardDeck()
    {
        if (_document?.BehaviorDatabase is not JsonObject database
            || database["brainIds"] is not JsonArray brainIds
            || database["brains"] is not JsonArray brains)
            return _actors.FirstOrDefault();

        foreach (WorldActorNode actor in _actors)
        {
            string brainId = actor.State.BrainName;
            int brainIndex = -1;
            for (int index = 0; index < brainIds.Count; index++)
            {
                if (LegacySaveDocument.ReadString(brainIds[index]) == brainId)
                {
                    brainIndex = index;
                    break;
                }
            }
            if (brainIndex < 0 || brainIndex >= brains.Count || brains[brainIndex] is not JsonObject brain
                || brain["behaviorUses"] is not JsonArray uses)
                continue;
            if (uses.OfType<JsonObject>().Any(use =>
            {
                JsonArray? definitions = _behaviorRuntime?.GetBehaviorPropertyDefinitions(LegacySaveDocument.ReadString(use["behaviorUri"]));
                return definitions?.OfType<JsonObject>().Any(definition => LegacySaveDocument.ReadString(definition["type"]) == "CardDeck") == true;
            }))
                return actor;
        }
        return _actors.FirstOrDefault();
    }

    private async System.Threading.Tasks.Task WaitForUiFrames(int frames)
    {
        for (int index = 0; index < frames; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static object? InvokeSmoke(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, arguments);
    }

    private static int FindDiagnosticGutter(LegacyJavaScriptCodeEdit editor)
    {
        for (int gutter = 0; gutter < editor.GetGutterCount(); gutter++)
        {
            if (editor.GetGutterName(gutter) == LegacyJavaScriptCodeEdit.DiagnosticGutterName)
                return gutter;
        }
        return -1;
    }
    private static Vector2 GetEditorPositionOnLine(LegacyJavaScriptCodeEdit editor, int zeroBasedLine)
    {
        int x = editor.GetTotalGutterWidth() + 1;
        for (int y = 0; y < editor.Size.Y; y++)
        {
            Vector2I lineAndColumn = editor.GetLineColumnAtPos(new Vector2I(x, y), false, true);
            if (lineAndColumn.Y == zeroBasedLine)
                return new Vector2(x, y);
        }
        throw new InvalidOperationException($"CodeEdit line {zeroBasedLine + 1} is not visible for the tooltip smoke assertion.");
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static (string PanelUseId, string PropertyName) ParseDeckTargetName(string name)
    {
        const string prefix = "CardLibraryDrop_";
        string value = name[prefix.Length..];
        int separator = value.IndexOf('_');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidOperationException($"Unexpected deck target name '{name}'.");
        return (value[..separator], value[(separator + 1)..]);
    }

    private static JsonObject? FindUseSmoke(JsonArray uses, string id)
        => uses.OfType<JsonObject>().FirstOrDefault(use => LegacySaveDocument.ReadString(use["id"]) == id);
}
