using System;
using System.IO;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunCameraEffectsSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null)
                throw new InvalidOperationException("Open a project before running the camera-effects smoke test.");

            LegacyVisualEffectsWindow window = _visualEffectsWindow;
            OpenVisualEffects();
            await WaitForUiFrames(2);
            if (!window.Visible)
                throw new InvalidOperationException("The legacy visual-effects window did not open.");
            if (LegacyVisualEffectsWindow.Effects.Length != 7)
                throw new InvalidOperationException("The legacy visual-effects window did not expose all seven filters.");

            Image baseline = GetViewport().GetTexture().GetImage();
            int visibleEffects = 0;
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/visual-effects-smoke.png");
            foreach ((string id, string label) in LegacyVisualEffectsWindow.Effects)
            {
                string controlName = $"VisualEffect_{id}";
                CheckBox toggle = window.FindChild(controlName, true, false) as CheckBox
                    ?? throw new InvalidOperationException($"The '{label}' filter toggle was not found.");
                if (toggle.ButtonPressed || _cameraEffects.IsEffectEnabled(id))
                    throw new InvalidOperationException($"The '{label}' filter was unexpectedly enabled at startup.");

                toggle.SetPressedNoSignal(true);
                toggle.EmitSignal("toggled", true);
                await WaitForUiFrames(2);
                if (!_cameraEffects.IsEffectEnabled(id))
                    throw new InvalidOperationException($"The '{label}' filter toggle did not enable its camera effect.");

                Image filtered = GetViewport().GetTexture().GetImage();
                int changedPixels = CountDifferentPixels(baseline, filtered);
                if (changedPixels == 0)
                    throw new InvalidOperationException($"The '{label}' filter did not change the rendered viewport.");
                visibleEffects++;
                if (id == "drawing" && filtered.SavePng(screenshot) != Error.Ok)
                    throw new InvalidOperationException("The visual-effects screenshot could not be saved.");

                toggle.SetPressedNoSignal(false);
                toggle.EmitSignal("toggled", false);
                await WaitForUiFrames(1);
                if (_cameraEffects.IsEffectEnabled(id))
                    throw new InvalidOperationException($"The '{label}' filter did not turn off.");
            }

            CheckBox bwToggle = (CheckBox)window.FindChild("VisualEffect_bw", true, false);
            CheckBox vhsToggle = (CheckBox)window.FindChild("VisualEffect_vhs", true, false);
            bwToggle.SetPressedNoSignal(true);
            bwToggle.EmitSignal("toggled", true);
            vhsToggle.SetPressedNoSignal(true);
            vhsToggle.EmitSignal("toggled", true);
            await WaitForUiFrames(2);
            if (!_cameraEffects.IsEffectEnabled("bw") || !_cameraEffects.IsEffectEnabled("vhs"))
                throw new InvalidOperationException("The camera-effects stack did not keep multiple filters enabled together.");
            bwToggle.SetPressedNoSignal(false);
            bwToggle.EmitSignal("toggled", false);
            vhsToggle.SetPressedNoSignal(false);
            vhsToggle.EmitSignal("toggled", false);
            window.Hide();
            GD.Print($"CAMERA_EFFECTS_SMOKE_OK filters={visibleEffects} simultaneous-stack=true screenshot={screenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CAMERA_EFFECTS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            foreach ((string id, _) in LegacyVisualEffectsWindow.Effects)
                _cameraEffects.SetEffectEnabled(id, false);
            _visualEffectsWindow.Hide();
        }

        GetTree().Quit(exitCode);
    }

    private async void RunCameraOffsetSmoke()
    {
        int exitCode = 0;
        bool wasPlaying = _playMode;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null)
                throw new InvalidOperationException("Open a project before running the camera-offset smoke test.");

            SetPlayMode(true);
            await WaitForUiFrames(3);
            if (!_playMode || GetPlayerActor() is null)
                throw new InvalidOperationException("Play mode did not provide a controllable actor for the camera-offset smoke test.");

            Vector3 legacyOffset = new(0.25f, 0f, 0f);
            Vector3 baseline = _camera.GlobalPosition;
            Vector3 expectedGlobalOffset = _camera.GlobalBasis * legacyOffset;
            SetTemporaryCameraOffset(legacyOffset);
            await WaitForUiFrames(1);
            float displacementError = (_camera.GlobalPosition - baseline - expectedGlobalOffset).Length();
            if (displacementError > 0.08f)
                throw new InvalidOperationException($"A camera-local offset was not applied for one frame (displacement error {displacementError:F4}).");

            await WaitForUiFrames(1);
            float restorationError = (_camera.GlobalPosition - baseline).Length();
            if (restorationError > 0.08f)
                throw new InvalidOperationException($"The camera offset did not clear after one frame (restoration error {restorationError:F4}).");

            GD.Print($"CAMERA_OFFSET_SMOKE_OK oneFrame=true cameraLocal=true displacementError={displacementError:F4} restorationError={restorationError:F4}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"CAMERA_OFFSET_SMOKE_FAILED: {exception}");
        }
        finally
        {
            _pendingCameraOffset = Vector3.Zero;
            if (!wasPlaying && _playMode)
                SetPlayMode(false);
        }

        GetTree().Quit(exitCode);
    }


    private static int CountDifferentPixels(Image baseline, Image filtered)
    {
        Vector2I size = baseline.GetSize();
        if (filtered.GetSize() != size)
            throw new InvalidOperationException("The camera-effects viewport dimensions changed unexpectedly.");

        int differences = 0;
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                Color before = baseline.GetPixel(x, y);
                Color after = filtered.GetPixel(x, y);
                if (Math.Abs(before.R - after.R) > 0.015f
                    || Math.Abs(before.G - after.G) > 0.015f
                    || Math.Abs(before.B - after.B) > 0.015f)
                    differences++;
            }
        }
        return differences;
    }
}
