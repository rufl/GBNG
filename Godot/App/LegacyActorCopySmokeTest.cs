using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GBNG.Domain;
using GBNG.World;
using Godot;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunActorCopySmoke()
    {
        int exitCode = 0;
        string? originalProjectPath = _document?.SourcePath;
        bool originalParentOnReset = _parentOnReset;
        bool originalAutoSetSpawnOnMove = _autoSetSpawnOnMove;
        bool originalAutoSetSpawnOnRotate = _autoSetSpawnOnRotate;
        string? temporaryDirectory = null;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || originalProjectPath is null)
                throw new InvalidOperationException("Open a project before running the actor-copy smoke test.");

            string sourcePath = ProjectSettings.GlobalizePath(originalProjectPath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The actor-copy smoke project was not found.", sourcePath);
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"GBNG-ActorCopy-Smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            string projectPath = Path.Combine(temporaryDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath)
                throw new InvalidOperationException("The writable actor-copy smoke project did not load.");
            if (_assetStoreWindow.Visible || _worldSettingsWindow.Visible
                || ((Window)_worldSettingsWindow.FindChild("WorldMapSizeDialog", true, false)).Visible)
                throw new InvalidOperationException("Editor utility windows must remain hidden until opened by the user.");
            int actorCountBeforeListToggle = _actors.Count;
            int selectedCountBeforeListToggle = _selectedActors.Count;
            WorldActorNode? focusBeforeListToggle = _selectedActor;
            Button actorListToggleButton = FindChild("ActorListToggleButton", true, false) as Button
                ?? throw new InvalidOperationException("The actor-list toggle button was not found.");
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.L, CtrlPressed = true, Pressed = true });
            await WaitForUiFrames(1);
            if (_actorList.Visible || _offstageActorList.Visible
                || _actors.Count != actorCountBeforeListToggle
                || _selectedActors.Count != selectedCountBeforeListToggle
                || _selectedActor != focusBeforeListToggle)
                throw new InvalidOperationException("Ctrl+L did not hide the actor hierarchy without changing the world selection.");
            string actorListScreenshot = ProjectSettings.GlobalizePath("res://.godot/actor-list-toggle-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(actorListScreenshot) != Error.Ok)
                throw new InvalidOperationException("The hidden actor-list surface screenshot could not be saved.");
            actorListToggleButton.EmitSignal("pressed");
            await WaitForUiFrames(1);
            if (!_actorList.Visible || !_offstageActorList.Visible
                || _actors.Count != actorCountBeforeListToggle
                || _selectedActors.Count != selectedCountBeforeListToggle
                || _selectedActor != focusBeforeListToggle)
                throw new InvalidOperationException("The actor-list Show button did not restore both lists and selection.");

            WorldActorNode source = _actors
                .Where(actor => !actor.LegacyName.StartsWith("builtin:", StringComparison.Ordinal)
                    && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]).Length == 0
                    && actor.GetNodeOrNull<Node3D>("Renderable") is not null)
                .OrderByDescending(actor => actor.GetWorldRenderBounds().Size.LengthSquared())
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The smoke project has no renderable original actor to copy.");
            string sourceName = source.LegacyName;
            JsonObject childData = (JsonObject)source.State.Data.DeepClone();
            string childName = $"copy-smoke-child-{Guid.NewGuid():N}";
            childData["name"] = childName;
            childData["displayName"] = "Copy smoke child";
            childData["transformParent"] = source.LegacyName;
            childData["cloneParent"] = "";
            int childIndex = _document.Actors.Count;
            LegacyActorData childState = _document.InsertActor(childData, childIndex);
            WorldActorNode child = CreateActorNode(childState, childIndex);
            child.GlobalPosition = source.GlobalPosition + new Vector3(0f, 1.5f, 0f);
            child.SaveTransform();
            JsonObject independentData = (JsonObject)source.State.Data.DeepClone();
            string independentName = $"multi-select-smoke-{Guid.NewGuid():N}";
            independentData["name"] = independentName;
            independentData["displayName"] = "Multi-select smoke root";
            independentData["transformParent"] = "";
            independentData["cloneParent"] = "";
            int independentIndex = _document.Actors.Count;
            LegacyActorData independentState = _document.InsertActor(independentData, independentIndex);
            WorldActorNode independent = CreateActorNode(independentState, independentIndex);
            independent.GlobalPosition = source.GlobalPosition + Vector3.Right * 5f;
            independent.SaveTransform();
            child.GlobalPosition = source.GlobalPosition + Vector3.Up * 5f;
            child.SaveTransform();
            RefreshActorList();
            ClearHistory();
            SetActorSelection(new[] { source }, source);
            await WaitForUiFrames(2);
            RunAutoSetSpawnSmoke(source, child);
            _cameraTarget = (source.GlobalPosition + independent.GlobalPosition) * 0.5f;
            _cameraDistance = 18f;
            UpdateOrbitCamera();
            await WaitForUiFrames(2);
            Button cameraFollowButton = FindChild("CameraFollowButton", true, false) as Button
                ?? throw new InvalidOperationException("The camera-follow button was not found.");
            Vector3 cameraTargetBeforeFollow = _cameraTarget;
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.F, Pressed = true });
            await WaitForUiFrames(2);
            if (!_cameraFollowingActor
                || cameraFollowButton.Text != "Following"
                || !_cameraTarget.IsEqualApprox(source.GetWorldRenderBounds().GetCenter()))
                throw new InvalidOperationException("F did not focus and follow the selected actor.");
            string cameraFollowScreenshot = ProjectSettings.GlobalizePath("res://.godot/camera-follow-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(cameraFollowScreenshot) != Error.Ok)
                throw new InvalidOperationException("The camera-follow surface screenshot could not be saved.");
            Transform3D sourceTransformBeforeFollow = source.GlobalTransform;
            Transform3D childTransformBeforeFollow = child.GlobalTransform;
            source.GlobalPosition += Vector3.Right * 2f;
            _Process(1d);
            if (_cameraTarget.DistanceTo(source.GetWorldRenderBounds().GetCenter()) > 0.02f)
                throw new InvalidOperationException("The editor camera did not track the selected actor's movement.");
            cameraFollowButton.EmitSignal("pressed");
            Vector3 cameraTargetAfterStop = _cameraTarget;
            if (_cameraFollowingActor || cameraFollowButton.Text != "Follow")
                throw new InvalidOperationException("The Follow button did not stop camera tracking.");
            source.GlobalPosition += Vector3.Right * 2f;
            _Process(1d);
            if (!_cameraTarget.IsEqualApprox(cameraTargetAfterStop))
                throw new InvalidOperationException("The camera continued following after the toggle was disabled.");
            source.GlobalTransform = sourceTransformBeforeFollow;
            child.GlobalTransform = childTransformBeforeFollow;
            _cameraTarget = cameraTargetBeforeFollow;
            UpdateOrbitCamera();

            var sourceStoredMotion = new ActorMotionSnapshot(
                source.State.GetWorldTransform(),
                source.State.Velocity,
                source.State.AngularVelocity);
            source.GlobalPosition += Vector3.Right * 3f;
            source.LinearVelocity = new Vector3(3f, 2f, 1f);
            source.AngularVelocity = new Vector3(0.5f, 0.25f, 0.75f);
            var sourceBeforeReset = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.R, CtrlPressed = true, Pressed = true });
            if (!source.GlobalTransform.IsEqualApprox(sourceStoredMotion.Transform)
                || !source.LinearVelocity.IsEqualApprox(sourceStoredMotion.Velocity)
                || !source.AngularVelocity.IsEqualApprox(sourceStoredMotion.AngularVelocity))
                throw new InvalidOperationException("Ctrl+R did not restore the selected actor's stored transform and motion.");
            UndoEdit();
            if (!source.GlobalTransform.IsEqualApprox(sourceBeforeReset.Transform)
                || !source.LinearVelocity.IsEqualApprox(sourceBeforeReset.Velocity)
                || !source.AngularVelocity.IsEqualApprox(sourceBeforeReset.AngularVelocity))
                throw new InvalidOperationException("Undo did not restore actor motion from before Ctrl+R.");
            RedoEdit();
            if (!source.GlobalTransform.IsEqualApprox(sourceStoredMotion.Transform)
                || !source.LinearVelocity.IsEqualApprox(sourceStoredMotion.Velocity)
                || !source.AngularVelocity.IsEqualApprox(sourceStoredMotion.AngularVelocity))
                throw new InvalidOperationException("Redo did not restore the Ctrl+R reset state.");
            ClearHistory();
            ClickActorForSmoke(source);
            AssertActorSelectionForSmoke(source);
            ClickActorForSmoke(independent, addToSelection: true);
            AssertActorSelectionForSmoke(source, independent);
            if (_selectedActor != independent
                || _actorList.GetSelectedItems().Length + _offstageActorList.GetSelectedItems().Length != 2)
                throw new InvalidOperationException("Shift-click did not add and focus the second actor in the world and actor list.");
            ClickActorForSmoke(independent, toggleSelection: true);
            AssertActorSelectionForSmoke(source);
            ClickActorForSmoke(independent, toggleSelection: true);
            AssertActorSelectionForSmoke(source, independent);
            Vector2 marqueeCenter = _camera.UnprojectPosition(source.GetWorldRenderBounds().GetCenter());
            Vector2 marqueeStart = marqueeCenter - new Vector2(40f, 40f);
            Vector2 marqueeEnd = marqueeCenter + new Vector2(40f, 40f);
            BeginSelectionMarquee(marqueeStart, addToSelection: false, toggleSelection: false);
            UpdateSelectionMarquee(marqueeEnd);
            await WaitForUiFrames(2);
            Image marqueeCapture = GetViewport().GetTexture().GetImage();
            int marqueeAccentPixels = 0;
            for (int y = Mathf.Max(0, Mathf.FloorToInt(marqueeStart.Y));
                y <= Mathf.Min(marqueeCapture.GetHeight() - 1, Mathf.CeilToInt(marqueeEnd.Y));
                y++)
            {
                for (int x = Mathf.Max(0, Mathf.FloorToInt(marqueeStart.X));
                    x <= Mathf.Min(marqueeCapture.GetWidth() - 1, Mathf.CeilToInt(marqueeEnd.X));
                    x++)
                {
                    Color pixel = marqueeCapture.GetPixel(x, y);
                    if (pixel.B > pixel.R + 0.3f && pixel.G > pixel.R + 0.2f)
                        marqueeAccentPixels++;
                }
            }
            string marqueeScreenshot = ProjectSettings.GlobalizePath("res://.godot/actor-selection-marquee-smoke.png");
            if (marqueeAccentPixels < 20 || marqueeCapture.SavePng(marqueeScreenshot) != Error.Ok)
                throw new InvalidOperationException("The drag marquee did not produce a visible selection overlay.");
            CompleteSelectionMarquee(marqueeEnd);
            if (!_selectedActors.Contains(source))
                throw new InvalidOperationException("The rendered marquee did not select the actor inside its bounds.");

            SelectActorInMarqueeForSmoke(source);
            AssertActorSelectionForSmoke(source);


            SelectActorInMarqueeForSmoke(independent, addToSelection: true);
            AssertActorSelectionForSmoke(source, independent);
            SelectActorInMarqueeForSmoke(source, toggleSelection: true);
            AssertActorSelectionForSmoke(independent);

            ClickActorForSmoke(source, addToSelection: true);
            AssertActorSelectionForSmoke(independent, source);
            var sourceBeforeRotate = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            var independentBeforeRotate = new ActorMotionSnapshot(independent.GlobalTransform, independent.LinearVelocity, independent.AngularVelocity);
            Vector3 rotationPivot = (source.GlobalPosition + independent.GlobalPosition) * 0.5f;
            Quaternion rotationDelta = CoordinateSpace.ToGodot(new Quaternion(Vector3.Up, Mathf.DegToRad(45f)));
            Button rotateButton = FindChild("QuickRotateActorsButton", true, false) as Button
                ?? throw new InvalidOperationException("The quick-rotate toolbar button was not found.");
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            UndoEdit();
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            rotateButton.EmitSignal("pressed");
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            UndoEdit();
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            RedoEdit();
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: true);
            UndoEdit();
            await WaitForUiFrames(2);
            AssertQuickRotationForSmoke(source, sourceBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            AssertQuickRotationForSmoke(independent, independentBeforeRotate, rotationPivot, rotationDelta, rotated: false);
            CheckBox parentOnResetToggle = FindChild("ParentOnResetToggle", true, false) as CheckBox
                ?? throw new InvalidOperationException("The Parent on reset preference toggle was not found.");
            if (parentOnResetToggle.ButtonPressed != _parentOnReset)
                throw new InvalidOperationException("The Parent on reset toggle did not reflect the loaded preference.");
            parentOnResetToggle.ButtonPressed = false;
            if (_parentOnReset)
                throw new InvalidOperationException("The Parent on reset toggle did not disable the preference.");
            parentOnResetToggle.ButtonPressed = true;
            if (!_parentOnReset)
                throw new InvalidOperationException("The Parent on reset toggle did not enable the preference.");
            var persistedParentSetting = new ConfigFile();
            if (persistedParentSetting.Load(EditorSettingsPath) != Error.Ok
                || !persistedParentSetting.GetValue("editor", "parent_on_reset", false).AsBool())
                throw new InvalidOperationException("Enabling Parent on reset did not persist the preference.");
            SetActorSelection(new[] { source }, source);
            string originalTransformParent = source.State.TransformParent;
            string originalSpawnTransformParent = LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]);
            Node originalSceneParent = source.GetParent();
            Vector2 parentTargetPosition = _camera.UnprojectPosition(independent.GetWorldRenderBounds().GetCenter());
            Input.WarpMouse(parentTargetPosition);
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) != independent)
                throw new InvalidOperationException("The parent shortcut could not resolve the actor under the cursor.");
            var sourceBeforeParent = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            _UnhandledInput(new InputEventKey { Keycode = Key.H, CtrlPressed = true, Pressed = true });
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != independent
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeParent.Transform)
                || !source.LinearVelocity.IsEqualApprox(sourceBeforeParent.Velocity)
                || !source.AngularVelocity.IsEqualApprox(sourceBeforeParent.AngularVelocity))
                throw new InvalidOperationException($"Ctrl+H did not parent the selected actor while preserving its world motion state: selection={_selectedActors.Count}/{_selectedActor?.LegacyName}, transformParent='{source.State.TransformParent}', spawnParent='{LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"])}', sceneParent='{source.GetParent()?.Name}', transformPreserved={source.GlobalTransform.IsEqualApprox(sourceBeforeParent.Transform)}, velocityPreserved={source.LinearVelocity.IsEqualApprox(sourceBeforeParent.Velocity)}, angularVelocityPreserved={source.AngularVelocity.IsEqualApprox(sourceBeforeParent.AngularVelocity)}.");
            UndoEdit();
            if (source.State.TransformParent != originalTransformParent
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalSpawnTransformParent
                || source.GetParent() != originalSceneParent
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeParent.Transform))
                throw new InvalidOperationException("Undo did not restore the actor's original parent and world transform.");
            RedoEdit();
            if (source.State.TransformParent != independentName || source.GetParent() != independent)
                throw new InvalidOperationException("Redo did not restore the actor parent.");

            SetActorSelection(new[] { source }, source);
            string sourceParentBeforeCycle = source.State.TransformParent;
            string sourceSpawnParentBeforeCycle = LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]);
            Node sourceSceneParentBeforeCycle = source.GetParent();
            Vector2 cycleTargetPosition = _camera.UnprojectPosition(child.GetWorldRenderBounds().GetCenter());
            Input.WarpMouse(cycleTargetPosition);
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) != child)
                throw new InvalidOperationException("The cycle-rejection smoke test could not resolve the descendant actor.");
            _UnhandledInput(new InputEventKey { Keycode = Key.H, CtrlPressed = true, Pressed = true });
            if (source.State.TransformParent != sourceParentBeforeCycle
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != sourceSpawnParentBeforeCycle
                || source.GetParent() != sourceSceneParentBeforeCycle)
                throw new InvalidOperationException("Parenting an actor to its descendant created an invalid hierarchy.");
            Button setSpawnParentButton = FindChild("SetSpawnParentButton", true, false) as Button
                ?? throw new InvalidOperationException("The reset-parent picker button was not found.");
            Label spawnParentLabel = FindChild("SpawnParentLabel", true, false) as Label
                ?? throw new InvalidOperationException("The reset-parent label was not found.");
            if (spawnParentLabel.Text != independent.State.DisplayName)
                throw new InvalidOperationException("The inspector did not show the selected actor's reset parent.");
            string parentBeforeRejectedResetPick = source.State.TransformParent;
            string spawnParentBeforeRejectedResetPick = LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]);
            Node sceneParentBeforeRejectedResetPick = source.GetParent();
            setSpawnParentButton.EmitSignal("pressed");
            if (!_settingSpawnParent)
                throw new InvalidOperationException("The reset-parent button did not enter scene-picking mode.");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = cycleTargetPosition
            });
            if (!_settingSpawnParent
                || source.State.TransformParent != parentBeforeRejectedResetPick
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != spawnParentBeforeRejectedResetPick
                || source.GetParent() != sceneParentBeforeRejectedResetPick)
                throw new InvalidOperationException("The reset-parent picker accepted a descendant as its target.");
            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_settingSpawnParent || setSpawnParentButton.Text != "Choose in scene…")
                throw new InvalidOperationException("Escape did not cancel reset-parent scene-picking mode.");

            SetActorSelection(new[] { source }, source);
            Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
            Vector2 emptyPosition = default;
            bool foundEmptyPosition = false;
            for (int row = 0; row < 8 && !foundEmptyPosition; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    Vector2 candidate = new(
                        viewportSize.X * (column + 0.5f) / 8f,
                        viewportSize.Y * (row + 0.5f) / 8f);
                    if (candidate.X < 280f || candidate.X > viewportSize.X - 300f || candidate.Y < 55f)
                        continue;
                    if (GetActorAtScreenPosition(candidate) is not null || IsPointerOverUi(candidate))
                        continue;
                    emptyPosition = candidate;
                    foundEmptyPosition = true;
                    break;
                }
            }
            if (!foundEmptyPosition)
                throw new InvalidOperationException("The parent smoke test could not find empty world space.");
            if (GetActorAtScreenPosition(emptyPosition) is not null || IsPointerOverUi(emptyPosition))
                throw new InvalidOperationException("The parent smoke test's empty-world point is not available for unparenting.");
            parentOnResetToggle.ButtonPressed = false;
            if (_parentOnReset)
                throw new InvalidOperationException("The Parent on reset toggle did not disable the preference before unparenting.");
            var persistedDisabledSetting = new ConfigFile();
            if (persistedDisabledSetting.Load(EditorSettingsPath) != Error.Ok
                || persistedDisabledSetting.GetValue("editor", "parent_on_reset", true).AsBool())
                throw new InvalidOperationException("Disabling Parent on reset did not persist the preference.");
            var sourceBeforeUnparent = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            SetActorTransformParent(source, parent: null);
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeUnparent.Transform))
                throw new InvalidOperationException("Unparenting with Parent on reset disabled did not preserve the reset parent and world transform.");
            UndoEdit();
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != independent
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeUnparent.Transform))
                throw new InvalidOperationException("Undo did not restore the actor's current and reset parents after unparenting.");
            RedoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("Redo did not restore the unparented actor while preserving its reset parent.");

            var beforeResetParentPick = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            setSpawnParentButton.EmitSignal("pressed");
            if (!_settingSpawnParent)
                throw new InvalidOperationException("The reset-parent button did not enter scene-picking mode to clear the parent.");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = emptyPosition
            });
            if (_settingSpawnParent
                || source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(beforeResetParentPick.Transform)
                || spawnParentLabel.Text != "<none>")
                throw new InvalidOperationException("Picking empty world space did not clear only the actor's reset parent.");
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world
                || spawnParentLabel.Text != independent.State.DisplayName)
                throw new InvalidOperationException("Undo did not restore only the reset parent.");
            RedoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world)
                throw new InvalidOperationException("Redo did not clear only the reset parent.");

            Input.WarpMouse(parentTargetPosition);
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) != independent
                || IsPointerOverUi(parentTargetPosition))
                throw new InvalidOperationException("The reset-parent picker could not target the independent actor.");
            setSpawnParentButton.EmitSignal("pressed");
            if (!_settingSpawnParent)
                throw new InvalidOperationException("The reset-parent button did not restart scene-picking mode.");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = parentTargetPosition
            });
            if (_settingSpawnParent
                || source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(beforeResetParentPick.Transform))
                throw new InvalidOperationException("Picking an actor did not set only the reset parent.");
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world)
                throw new InvalidOperationException("Undo did not clear only the chosen reset parent.");
            RedoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("Redo did not restore only the chosen reset parent.");
            UndoEdit();
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("Undo did not restore the state before reset-parent picking.");
            Button setTransformParentButton = FindChild("SetTransformParentButton", true, false) as Button
                ?? throw new InvalidOperationException("The current-parent picker button was not found.");
            Label transformParentLabel = FindChild("TransformParentLabel", true, false) as Label
                ?? throw new InvalidOperationException("The current-parent label was not found.");
            if (transformParentLabel.Text != "<none>")
                throw new InvalidOperationException("The inspector did not show the selected actor's current parent.");

            setTransformParentButton.EmitSignal("pressed");
            if (!_settingTransformParent)
                throw new InvalidOperationException("The current-parent button did not enter scene-picking mode.");
            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_settingTransformParent || setTransformParentButton.Text != "Choose in scene…")
                throw new InvalidOperationException("Escape did not cancel current-parent scene-picking mode.");

            var beforeRejectedCurrentParentPick = new ActorMotionSnapshot(
                source.GlobalTransform,
                source.LinearVelocity,
                source.AngularVelocity);
            string transformParentBeforeRejectedPick = source.State.TransformParent;
            string resetParentBeforeRejectedPick = LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]);
            Node sceneParentBeforeRejectedPick = source.GetParent();
            setTransformParentButton.EmitSignal("pressed");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = cycleTargetPosition
            });
            if (!_settingTransformParent
                || source.State.TransformParent != transformParentBeforeRejectedPick
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != resetParentBeforeRejectedPick
                || source.GetParent() != sceneParentBeforeRejectedPick
                || !source.GlobalTransform.IsEqualApprox(beforeRejectedCurrentParentPick.Transform))
                throw new InvalidOperationException("The current-parent picker accepted a descendant or changed the actor while rejecting it.");
            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_settingTransformParent)
                throw new InvalidOperationException("Escape did not exit current-parent picking after cycle rejection.");

            setSpawnParentButton.EmitSignal("pressed");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = emptyPosition
            });
            if (_settingSpawnParent
                || source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world)
                throw new InvalidOperationException("The current-parent smoke setup could not clear the reset parent.");

            var beforeCurrentParentPick = new ActorMotionSnapshot(
                source.GlobalTransform,
                source.LinearVelocity,
                source.AngularVelocity);
            setTransformParentButton.EmitSignal("pressed");
            if (!_settingTransformParent)
                throw new InvalidOperationException("The current-parent button did not start actor picking.");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = parentTargetPosition
            });
            if (_settingTransformParent
                || source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != independent
                || !source.GlobalTransform.IsEqualApprox(beforeCurrentParentPick.Transform)
                || !source.LinearVelocity.IsEqualApprox(beforeCurrentParentPick.Velocity)
                || !source.AngularVelocity.IsEqualApprox(beforeCurrentParentPick.AngularVelocity)
                || transformParentLabel.Text != independent.State.DisplayName)
                throw new InvalidOperationException("The current-parent picker did not reparent while preserving world motion and the reset parent with Parent on reset disabled.");
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(beforeCurrentParentPick.Transform))
                throw new InvalidOperationException("Undo did not restore the prior current-parent hierarchy.");
            RedoEdit();
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != independent)
                throw new InvalidOperationException("Redo did not restore the current parent without changing the reset parent.");

            setTransformParentButton.EmitSignal("pressed");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = emptyPosition
            });
            if (_settingTransformParent
                || source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(beforeCurrentParentPick.Transform))
                throw new InvalidOperationException("Picking empty world space did not clear only the current parent with Parent on reset disabled.");
            UndoEdit();
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != independent)
                throw new InvalidOperationException("Undo did not restore the current parent cleared by the picker.");
            RedoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world)
                throw new InvalidOperationException("Redo did not clear only the current parent.");
            UndoEdit();
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world)
                throw new InvalidOperationException("Undo did not restore the state before current-parent picking.");
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("Undo did not restore the hierarchy before the current-parent smoke.");

            SetParentOnResetPreference(true);
            var beforeCurrentParentPickWithReset = new ActorMotionSnapshot(
                source.GlobalTransform,
                source.LinearVelocity,
                source.AngularVelocity);
            setTransformParentButton.EmitSignal("pressed");
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = parentTargetPosition
            });
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != independent
                || !source.GlobalTransform.IsEqualApprox(beforeCurrentParentPickWithReset.Transform)
                || !source.LinearVelocity.IsEqualApprox(beforeCurrentParentPickWithReset.Velocity)
                || !source.AngularVelocity.IsEqualApprox(beforeCurrentParentPickWithReset.AngularVelocity))
                throw new InvalidOperationException("The current-parent picker did not update the reset parent when Parent on reset was enabled.");
            UndoEdit();
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("Undo did not restore the current parent while preserving its prior reset parent.");
            RedoEdit();
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != independent)
                throw new InvalidOperationException("Redo did not restore both parents when Parent on reset was enabled.");
            UndoEdit();
            SetParentOnResetPreference(originalParentOnReset);
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != _world)
                throw new InvalidOperationException("The current-parent picker smoke did not restore the pre-existing hierarchy.");



            UndoEdit();
            UndoEdit();
            if (source.State.TransformParent != originalTransformParent
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalSpawnTransformParent
                || source.GetParent() != originalSceneParent)
                throw new InvalidOperationException("Undo did not return the actor to its original hierarchy.");
            SetActorSelection(new[] { source }, source);
            SpinBox spawnXField = FindChild("SpawnPositionXField", true, false) as SpinBox
                ?? throw new InvalidOperationException("The restart spawn-position X field was not found.");
            Button setSpawnToCurrentButton = FindChild("SetSpawnToCurrentButton", true, false) as Button
                ?? throw new InvalidOperationException("The Use current position button was not found.");
            if (!spawnXField.Editable || setSpawnToCurrentButton.Disabled)
                throw new InvalidOperationException("The restart spawn controls were not enabled for a single actor.");

            Vector3 originalSpawnPosition = source.State.SpawnPosition;
            Quaternion originalSpawnRotation = source.State.SpawnRotation;
            string originalResetParent = LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]);
            string originalCurrentParent = source.State.TransformParent;
            Transform3D originalCurrentTransform = source.GlobalTransform;
            Node originalCurrentSceneParent = source.GetParent();
            float manualSpawnX = source.GlobalPosition.X + 20f;
            if (manualSpawnX > spawnXField.MaxValue || Mathf.IsEqualApprox(manualSpawnX, originalSpawnPosition.X))
                manualSpawnX = source.GlobalPosition.X - 20f;
            if (manualSpawnX < spawnXField.MinValue
                || manualSpawnX > spawnXField.MaxValue
                || Mathf.IsEqualApprox(manualSpawnX, originalSpawnPosition.X))
                throw new InvalidOperationException("The smoke actor has no distinct restart position within the inspector's coordinate range.");

            CommitNumberFieldInput(spawnXField, manualSpawnX);
            float effectiveSpawnX = (float)spawnXField.Value;
            if (Mathf.IsEqualApprox(effectiveSpawnX, originalSpawnPosition.X))
                throw new InvalidOperationException("The restart spawn-position field did not accept a distinct value.");
            Vector3 editedSpawnPosition = originalSpawnPosition;
            editedSpawnPosition.X = effectiveSpawnX;
            if (!source.State.SpawnPosition.IsEqualApprox(editedSpawnPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalResetParent
                || !source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation)
                || source.State.TransformParent != originalCurrentParent
                || source.GetParent() != originalCurrentSceneParent
                || !source.GlobalTransform.IsEqualApprox(originalCurrentTransform))
                throw new InvalidOperationException($"Editing restart spawn position changed more than the spawn position: field={spawnXField.Value}, manual={manualSpawnX}, current={source.GlobalPosition}, spawn={source.State.SpawnPosition}/{editedSpawnPosition}, resetParent='{LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"])}'/'{originalResetParent}', spawnRotationMatches={source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation)}, transformParent='{source.State.TransformParent}'/'{originalCurrentParent}', sceneParentMatches={source.GetParent() == originalCurrentSceneParent}, currentTransformMatches={source.GlobalTransform.IsEqualApprox(originalCurrentTransform)}.");
            UndoEdit();
            if (!source.State.SpawnPosition.IsEqualApprox(originalSpawnPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalResetParent)
                throw new InvalidOperationException("Undo did not restore the previous restart spawn position.");
            RedoEdit();
            if (!source.State.SpawnPosition.IsEqualApprox(editedSpawnPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalResetParent)
                throw new InvalidOperationException("Redo did not restore the edited restart spawn position.");

            string alternateResetParent = originalCurrentParent == independentName ? string.Empty : independentName;
            SetActorSpawnParent(sourceName, alternateResetParent);
            setSpawnToCurrentButton.EmitSignal("pressed");
            if (!source.State.SpawnPosition.IsEqualApprox(source.GlobalPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalCurrentParent
                || !source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation)
                || source.State.TransformParent != originalCurrentParent
                || source.GetParent() != originalCurrentSceneParent
                || !source.GlobalTransform.IsEqualApprox(originalCurrentTransform))
                throw new InvalidOperationException("Use current position did not update only the restart position and reset parent.");
            UndoEdit();
            if (!source.State.SpawnPosition.IsEqualApprox(editedSpawnPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != alternateResetParent)
                throw new InvalidOperationException("Undo did not restore both values changed by Use current position.");
            RedoEdit();
            if (!source.State.SpawnPosition.IsEqualApprox(source.GlobalPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalCurrentParent)
                throw new InvalidOperationException("Redo did not restore the current restart position and parent.");
            UndoEdit();
            UndoEdit();
            SetActorSpawnParent(sourceName, originalResetParent);
            if (!source.State.SpawnPosition.IsEqualApprox(originalSpawnPosition)
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalResetParent)
                throw new InvalidOperationException("Undo did not return the restart spawn state to its original values.");

            Vector3 beforeUpperBoundary = source.State.SpawnPosition;
            if (!Mathf.IsEqualApprox(beforeUpperBoundary.X, (float)spawnXField.MaxValue))
            {
                CommitNumberFieldInput(spawnXField, spawnXField.MaxValue + spawnXField.Step);
                if (!Mathf.IsEqualApprox((float)spawnXField.Value, (float)spawnXField.MaxValue)
                    || !Mathf.IsEqualApprox(source.State.SpawnPosition.X, (float)spawnXField.MaxValue))
                    throw new InvalidOperationException("The restart spawn-position field did not clamp to its upper boundary.");
                UndoEdit();
                if (!source.State.SpawnPosition.IsEqualApprox(beforeUpperBoundary))
                    throw new InvalidOperationException("Undo did not restore the spawn position after upper-bound editing.");
            }
            Vector3 beforeLowerBoundary = source.State.SpawnPosition;
            if (!Mathf.IsEqualApprox(beforeLowerBoundary.X, (float)spawnXField.MinValue))
            {
                CommitNumberFieldInput(spawnXField, spawnXField.MinValue - spawnXField.Step);
                if (!Mathf.IsEqualApprox((float)spawnXField.Value, (float)spawnXField.MinValue)
                    || !Mathf.IsEqualApprox(source.State.SpawnPosition.X, (float)spawnXField.MinValue))
                    throw new InvalidOperationException("The restart spawn-position field did not clamp to its lower boundary.");
                UndoEdit();
                if (!source.State.SpawnPosition.IsEqualApprox(beforeLowerBoundary))
                    throw new InvalidOperationException("Undo did not restore the spawn position after lower-bound editing.");
            }
            if (!source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation)
                || source.State.TransformParent != originalCurrentParent
                || source.GetParent() != originalCurrentSceneParent
                || !source.GlobalTransform.IsEqualApprox(originalCurrentTransform))
                throw new InvalidOperationException("Restart spawn edits changed the actor's current transform, hierarchy, or spawn rotation.");

            CheckBox showModelOffsetsToggle = FindChild("ShowRenderableOffsetsToggle", true, false) as CheckBox
                ?? throw new InvalidOperationException("The advanced model-offset toggle was not found.");
            VBoxContainer modelOffsetsPanel = FindChild("RenderableOffsetsPanel", true, false) as VBoxContainer
                ?? throw new InvalidOperationException("The advanced model-offset panel was not found.");
            showModelOffsetsToggle.ButtonPressed = true;
            if (!modelOffsetsPanel.Visible)
                throw new InvalidOperationException("The advanced model-offset toggle did not reveal the model controls.");
            SpinBox renderableOffsetXField = FindChild("RenderableOffsetXField", true, false) as SpinBox
                ?? throw new InvalidOperationException("The renderable offset X field was not found.");
            SpinBox renderableRotationYField = FindChild("RenderableRotationOffsetYField", true, false) as SpinBox
                ?? throw new InvalidOperationException("The renderable rotation Y field was not found.");
            if (!renderableOffsetXField.Editable || !renderableRotationYField.Editable)
                throw new InvalidOperationException("The model offset controls were not enabled for a single selected actor.");
            ScrollContainer inspectorScroll = FindChild("InspectorScrollContainer", true, false) as ScrollContainer
                ?? throw new InvalidOperationException("The inspector scroll container was not found.");
            await WaitForUiFrames(1);
            inspectorScroll.EnsureControlVisible(renderableRotationYField);
            await WaitForUiFrames(1);
            inspectorScroll.ScrollHorizontal = 0;
            await WaitForUiFrames(1);
            string modelOffsetScreenshot = ProjectSettings.GlobalizePath("res://.godot/renderable-offset-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(modelOffsetScreenshot) != Error.Ok)
                throw new InvalidOperationException("The advanced model-offset inspector screenshot could not be saved.");

            Node3D sourceRenderable = source.GetNodeOrNull<Node3D>("Renderable")
                ?? throw new InvalidOperationException("The smoke actor has no renderable root.");
            Vector3 originalRenderableOffset = source.State.RenderableOffset;
            Quaternion originalRenderableRotation = source.State.RenderableRotation;
            Transform3D actorTransformBeforeModelOffsets = source.GlobalTransform;
            Vector3 editedRenderableOffset = originalRenderableOffset;
            editedRenderableOffset.X += 1.25f;
            CommitNumberFieldInput(renderableOffsetXField, editedRenderableOffset.X);
            editedRenderableOffset.X = (float)renderableOffsetXField.Value;
            if (!source.State.RenderableOffset.IsEqualApprox(editedRenderableOffset)
                || !sourceRenderable.Position.IsEqualApprox(editedRenderableOffset)
                || !source.State.RenderableRotation.IsEqualApprox(originalRenderableRotation)
                || !source.GlobalTransform.IsEqualApprox(actorTransformBeforeModelOffsets)
                || !source.State.SpawnPosition.IsEqualApprox(originalSpawnPosition)
                || !source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation))
                throw new InvalidOperationException("Editing model position offset did not update only the rendered model.");
            UndoEdit();
            if (!source.State.RenderableOffset.IsEqualApprox(originalRenderableOffset)
                || !sourceRenderable.Position.IsEqualApprox(originalRenderableOffset))
                throw new InvalidOperationException("Undo did not restore the rendered model's original position offset.");
            RedoEdit();
            if (!source.State.RenderableOffset.IsEqualApprox(editedRenderableOffset)
                || !sourceRenderable.Position.IsEqualApprox(editedRenderableOffset))
                throw new InvalidOperationException("Redo did not restore the edited rendered model position offset.");
            UndoEdit();

            Vector3 editedRotationEuler = originalRenderableRotation.GetEuler();
            editedRotationEuler.Y += Mathf.DegToRad(25f);
            CommitNumberFieldInput(renderableRotationYField, editedRotationEuler.Y * (180f / Mathf.Pi));
            editedRotationEuler.Y = Mathf.DegToRad((float)renderableRotationYField.Value);
            Quaternion editedRenderableRotation = Quaternion.FromEuler(editedRotationEuler);
            if (!source.State.RenderableRotation.IsEqualApprox(editedRenderableRotation)
                || !sourceRenderable.Quaternion.IsEqualApprox(editedRenderableRotation)
                || !source.State.RenderableOffset.IsEqualApprox(originalRenderableOffset)
                || !source.GlobalTransform.IsEqualApprox(actorTransformBeforeModelOffsets)
                || !source.State.SpawnPosition.IsEqualApprox(originalSpawnPosition)
                || !source.State.SpawnRotation.IsEqualApprox(originalSpawnRotation))
                throw new InvalidOperationException("Editing model rotation offset did not update only the rendered model.");
            UndoEdit();
            if (!source.State.RenderableRotation.IsEqualApprox(originalRenderableRotation)
                || !sourceRenderable.Quaternion.IsEqualApprox(originalRenderableRotation))
                throw new InvalidOperationException("Undo did not restore the rendered model's original rotation offset.");
            RedoEdit();
            if (!source.State.RenderableRotation.IsEqualApprox(editedRenderableRotation)
                || !sourceRenderable.Quaternion.IsEqualApprox(editedRenderableRotation))
                throw new InvalidOperationException("Redo did not restore the edited rendered model rotation offset.");
            UndoEdit();
            showModelOffsetsToggle.ButtonPressed = false;
            if (modelOffsetsPanel.Visible
                || !source.State.RenderableOffset.IsEqualApprox(originalRenderableOffset)
                || !source.State.RenderableRotation.IsEqualApprox(originalRenderableRotation))
                throw new InvalidOperationException("The model-offset smoke did not restore its original values and collapsed state.");


            SetActorSelection(new[] { source, independent }, source);
            await WaitForUiFrames(1);
            if (!_spawnPositionFields[0].Editable
                || !setSpawnToCurrentButton.Disabled
                || !showModelOffsetsToggle.Disabled
                || renderableOffsetXField.Editable
                || renderableRotationYField.Editable)
                throw new InvalidOperationException("Multi-selection did not disable single-actor restart and model-offset controls.");


            BeginCopyPlacement();
            if (!_copyPlacementActive || _copySources.Count != 3
                || !_copySources.Contains(source) || !_copySources.Contains(independent) || !_copySources.Contains(child))
                throw new InvalidOperationException("Copy placement did not include both selected roots and the source descendant.");
            CancelCopyPlacement(showStatus: false);

            int beforeMultiDelete = _document.Actors.Count;
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.Delete, Pressed = true });
            await WaitForUiFrames(2);
            if (_document.Actors.Count != beforeMultiDelete - 2
                || _actors.Any(actor => actor.LegacyName == sourceName || actor.LegacyName == independentName)
                || child.State.TransformParent.Length != 0)
                throw new InvalidOperationException("Delete did not remove both selected roots while preserving and detaching their unselected child.");
            UndoEdit();
            await WaitForUiFrames(2);
            WorldActorNode restoredSource = _actors.FirstOrDefault(actor => actor.LegacyName == sourceName)
                ?? throw new InvalidOperationException("Undo did not restore the selected source actor.");
            WorldActorNode restoredIndependent = _actors.FirstOrDefault(actor => actor.LegacyName == independentName)
                ?? throw new InvalidOperationException("Undo did not restore the selected independent actor.");
            if (_document.Actors.Count != beforeMultiDelete
                || child.State.TransformParent != sourceName
                || child.GetParent() != restoredSource)
                throw new InvalidOperationException("Undo did not restore the selected roots and their child hierarchy.");
            source = restoredSource;
            independent = restoredIndependent;
            AssertActorSelectionForSmoke(independent, source);
            RedoEdit();
            await WaitForUiFrames(2);
            if (_document.Actors.Count != beforeMultiDelete - 2
                || _actors.Any(actor => actor.LegacyName == sourceName || actor.LegacyName == independentName))
                throw new InvalidOperationException("Redo did not remove the complete multi-actor deletion.");
            UndoEdit();
            await WaitForUiFrames(2);
            source = _actors.FirstOrDefault(actor => actor.LegacyName == sourceName)
                ?? throw new InvalidOperationException("Undo after redo did not restore the source actor.");
            independent = _actors.FirstOrDefault(actor => actor.LegacyName == independentName)
                ?? throw new InvalidOperationException("Undo after redo did not restore the independent actor.");
            if (_document.Actors.Count != beforeMultiDelete)
                throw new InvalidOperationException("Undo after redo did not restore the deleted actors.");
            SetActorSelection(new[] { source, independent }, source);
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.Backspace, Pressed = true });
            await WaitForUiFrames(2);
            if (_document.Actors.Count != beforeMultiDelete - 2
                || _actors.Any(actor => actor.LegacyName == sourceName || actor.LegacyName == independentName)
                || child.State.TransformParent.Length != 0)
                throw new InvalidOperationException("Backspace did not remove both selected roots while preserving and detaching their unselected child.");
            UndoEdit();
            await WaitForUiFrames(2);
            source = _actors.FirstOrDefault(actor => actor.LegacyName == sourceName)
                ?? throw new InvalidOperationException("Undo did not restore the Backspace-deleted source actor.");
            independent = _actors.FirstOrDefault(actor => actor.LegacyName == independentName)
                ?? throw new InvalidOperationException("Undo did not restore the Backspace-deleted independent actor.");
            if (_document.Actors.Count != beforeMultiDelete
                || child.State.TransformParent != sourceName
                || child.GetParent() != source)
                throw new InvalidOperationException("Undo did not restore the Backspace-deleted actor hierarchy.");
            ClickActorForSmoke(source);
            AssertActorSelectionForSmoke(source);
            ClearHistory();

            int originalCount = _document.Actors.Count;
            var originalActorNames = _actors.Select(actor => actor.LegacyName).ToHashSet(StringComparer.Ordinal);
            int existingCopies = _actors.Count(actor => actor.LegacyName != sourceName
                && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName);
            Button copyButton = FindChild("CopyActorsButton", true, false) as Button
                ?? throw new InvalidOperationException("The actor-copy toolbar button was not found.");
            _UnhandledInput(new InputEventKey { Keycode = Key.C, CtrlPressed = true, Pressed = true });
            await WaitForUiFrames(1);
            if (!_copyPlacementActive)
                throw new InvalidOperationException("Ctrl+C did not activate the actor-copy placement tool.");
            CancelCopyPlacement(showStatus: false);
            await WaitForUiFrames(1);
            copyButton.EmitSignal("pressed");
            await WaitForUiFrames(2);
            if (!_copyPlacementActive || _copyPreviewRoot is null || _copyPreviewRoot.GetChildCount() != 2
                || _document.Actors.Count != originalCount)
                throw new InvalidOperationException("Copy mode did not show the actor and descendant previews without modifying the project.");
            GeometryInstance3D? previewGeometry = _copyPreviewRoot
                .FindChildren("*", "GeometryInstance3D", true, false)
                .OfType<GeometryInstance3D>()
                .FirstOrDefault();
            if (previewGeometry is null || previewGeometry.MaterialOverride is null)
                throw new InvalidOperationException("The actor-copy preview has no rendered geometry or translucent material override.");
            UpdateCopyPreview(new Vector2(680f, 360f), snapToGrid: false);
            Vector2 cursorProjection = _camera.UnprojectPosition(_copyPreviewRoot.GlobalPosition);
            if (cursorProjection.DistanceTo(new Vector2(680f, 360f)) > 1f)
                throw new InvalidOperationException("Copy placement did not follow the cursor ray.");
            UpdateCopyPreview(new Vector2(680f, 360f), snapToGrid: true);
            Vector3 snappedPosition = _copyPreviewRoot.GlobalPosition;
            if (!Mathf.IsEqualApprox(snappedPosition.X / CopySnapHorizontal, Mathf.Round(snappedPosition.X / CopySnapHorizontal))
                || !Mathf.IsEqualApprox(snappedPosition.Y / CopySnapVertical, Mathf.Round(snappedPosition.Y / CopySnapVertical))
                || !Mathf.IsEqualApprox(snappedPosition.Z / CopySnapHorizontal, Mathf.Round(snappedPosition.Z / CopySnapHorizontal)))
                throw new InvalidOperationException("Ctrl/Cmd grid snapping did not align the preview to the legacy placement grid.");
            _copyPreviewRoot.GlobalPosition = source.GlobalPosition + Vector3.Right * 3f;
            await WaitForUiFrames(3);
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            Vector2 previewScreen = _camera.UnprojectPosition(previewGeometry.GlobalPosition);
            Image previewCapture = GetViewport().GetTexture().GetImage();
            int cyanPreviewPixels = 0;
            int minX = Mathf.Max(0, Mathf.RoundToInt(previewScreen.X) - 32);
            int maxX = Mathf.Min(previewCapture.GetWidth() - 1, Mathf.RoundToInt(previewScreen.X) + 32);
            int minY = Mathf.Max(0, Mathf.RoundToInt(previewScreen.Y) - 32);
            int maxY = Mathf.Min(previewCapture.GetHeight() - 1, Mathf.RoundToInt(previewScreen.Y) + 32);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Color pixel = previewCapture.GetPixel(x, y);
                    if (pixel.R < 0.4f && pixel.G > 0.5f && pixel.B > 0.55f)
                        cyanPreviewPixels++;
                }
            }
            if (cyanPreviewPixels == 0)
                throw new InvalidOperationException("The copy preview was not visible in the rendered editor view.");
            string screenshot = ProjectSettings.GlobalizePath("res://.godot/actor-copy-preview-smoke.png");
            if (previewCapture.SavePng(screenshot) != Error.Ok)
                throw new InvalidOperationException("The actor-copy preview screenshot could not be saved.");

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            await WaitForUiFrames(1);
            if (_copyPlacementActive || _document.Actors.Count != originalCount)
                throw new InvalidOperationException("Canceling copy placement changed the project.");

            copyButton.EmitSignal("pressed");
            await WaitForUiFrames(1);
            Vector3 placement = source.GlobalPosition + new Vector3(3f, 0f, 0f);
            PasteCopyAt(placement);
            await WaitForUiFrames(2);
            List<WorldActorNode> firstCopies = FindCopiesSince(originalActorNames);
            if (firstCopies.Count != 2 || _document.Actors.Count != originalCount + 2)
                throw new InvalidOperationException("Copy placement did not duplicate the selected actor and its child.");

            WorldActorNode firstRootCopy = firstCopies.Single(actor =>
                LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == source.LegacyName);
            WorldActorNode firstChildCopy = firstCopies.Single(actor => actor.State.TransformParent == firstRootCopy.LegacyName);
            if (firstRootCopy.State.DisplayName != GetCopyDisplayNameForSmoke(sourceName, existingCopies + 1))
                throw new InvalidOperationException("The new actor copy did not receive the next legacy clone display name.");
            AssertCopyTransform(source, firstRootCopy, placement - source.GlobalPosition);
            AssertCopyTransform(child, firstChildCopy, placement - source.GlobalPosition);
            if (firstRootCopy.State.RenderableUri != source.State.RenderableUri
                || firstRootCopy.State.BrainName != source.State.BrainName
                || !JsonNode.DeepEquals(firstRootCopy.State.Data["memoryJson"], source.State.Data["memoryJson"]))
                throw new InvalidOperationException("The copied actor did not preserve its legacy behavior and renderable data.");

            PasteCopyAt(placement + new Vector3(2f, 0f, 0f));
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Copy mode did not remain active for repeated placements.");
            WorldActorNode secondRootCopy = _actors.Single(actor =>
                !originalActorNames.Contains(actor.LegacyName)
                && !firstCopies.Contains(actor)
                && LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName);
            string secondRootCopyName = secondRootCopy.LegacyName;
            if (secondRootCopy.State.DisplayName != GetCopyDisplayNameForSmoke(sourceName, existingCopies + 2))
                throw new InvalidOperationException("Repeated copies did not receive the next legacy clone display name.");

            UndoEdit();
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 2 || _actors.Any(actor => actor.LegacyName == secondRootCopyName))
                throw new InvalidOperationException("Undo did not remove the latest actor-copy group.");
            RedoEdit();
            await WaitForUiFrames(2);
            if (_document.Actors.Count != originalCount + 4
                || !_actors.Any(actor => actor.LegacyName == secondRootCopyName))
                throw new InvalidOperationException("Redo did not restore the latest actor-copy group.");

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            WorldActorNode persistenceSource = _actors.Single(actor => actor.LegacyName == sourceName);
            WorldActorNode persistenceParent = _actors.Single(actor => actor.LegacyName == independentName);
            SetActorSelection(new[] { persistenceSource }, persistenceSource);
            ApplyActorHierarchySnapshot(
                persistenceSource.LegacyName,
                new ActorHierarchySnapshot(
                    independentName,
                    independentName,
                    persistenceSource.State.IsOffstage,
                    persistenceSource.GlobalTransform,
                    persistenceSource.LinearVelocity,
                    persistenceSource.AngularVelocity));
            if (persistenceSource.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(persistenceSource.State.Data["spawnTransformParent"]) != independentName
                || persistenceSource.GetParent() != persistenceParent)
                throw new InvalidOperationException("The parent persistence smoke fixture could not establish the expected hierarchy.");
            Vector3 persistenceSpawnPosition = persistenceSource.State.SpawnPosition;
            float persistenceSpawnY = persistenceSpawnPosition.Y + 2f;
            if (persistenceSpawnY > _spawnPositionFields[1].MaxValue)
                persistenceSpawnY = persistenceSpawnPosition.Y - 2f;
            if (persistenceSpawnY < _spawnPositionFields[1].MinValue || persistenceSpawnY > _spawnPositionFields[1].MaxValue)
                throw new InvalidOperationException("The persistence smoke actor has no restart position within the inspector's coordinate range.");
            persistenceSpawnPosition.Y = persistenceSpawnY;
            CommitNumberFieldInput(_spawnPositionFields[1], persistenceSpawnY);
            persistenceSpawnPosition.Y = (float)_spawnPositionFields[1].Value;
            if (!persistenceSource.State.SpawnPosition.IsEqualApprox(persistenceSpawnPosition))
                throw new InvalidOperationException($"Editing the restart spawn position did not update the persistence fixture: requestedY={persistenceSpawnY}, fieldY={_spawnPositionFields[1].Value}, selected='{_selectedActor?.LegacyName}', spawn={persistenceSource.State.SpawnPosition}, expected={persistenceSpawnPosition}.");
            string ctrlSaveMarker = $"Ctrl+S smoke {Guid.NewGuid():N}";
            persistenceSource.State.WriteString("description", ctrlSaveMarker);
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath || _document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Ctrl+S did not save the project before reload.");
            WorldActorNode ctrlSavedSource = _actors.Single(actor => actor.LegacyName == sourceName);
            if (ctrlSavedSource.State.Description != ctrlSaveMarker)
                throw new InvalidOperationException("Ctrl+S did not persist the current project state.");
            if (!ctrlSavedSource.State.SpawnPosition.IsEqualApprox(persistenceSpawnPosition))
                throw new InvalidOperationException("Ctrl+S did not persist the restart spawn position.");

            string cmdSaveMarker = $"Cmd+S smoke {Guid.NewGuid():N}";
            ctrlSavedSource.State.WriteString("description", cmdSaveMarker);
            _actorList.GrabFocus();
            _UnhandledInput(new InputEventKey { Keycode = Key.S, MetaPressed = true, Pressed = true });
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath || _document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Cmd+S did not save the project before reload.");
            if (!_actors.Any(actor => LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName))
                throw new InvalidOperationException("Saved copy lineage was not retained in the .voos project.");
            WorldActorNode persistedSource = _actors.Single(actor => actor.LegacyName == sourceName);
            WorldActorNode persistedParent = _actors.Single(actor => actor.LegacyName == independentName);
            if (persistedSource.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(persistedSource.State.Data["spawnTransformParent"]) != independentName
                || persistedSource.GetParent() != persistedParent)
                throw new InvalidOperationException("The actor parent and reset parent did not persist through save and reload.");
            if (!persistedSource.State.SpawnPosition.IsEqualApprox(persistenceSpawnPosition))
                throw new InvalidOperationException("The restart spawn position did not persist through save and reload.");
            if (persistedSource.State.Description != cmdSaveMarker)
                throw new InvalidOperationException("Cmd+S did not persist the current project state.");

            GD.Print("RESTART_SPAWN_SMOKE_OK inspector=true edit=true setCurrentPositionAndParent=true undoRedo=true currentTransformPreserved=true spawnRotationPreserved=true bounds=true multiSelection=true persistence=CtrlS+CmdS");

            GD.Print($"ACTOR_COPY_SMOKE_OK selection=shift-toggle-marquee=true marqueeVisual=true quickRotate=R+toolbar=true groupPivot=true undo-redo=true parenting=CtrlH=true transformPreserved=true cycleRejected=true unparent=emptySpace=true undo-redo=true parentPersistence=true currentParentPicker=assign+clear+cancel+cycleRejected=true undoRedo=true parentOnReset=enabled+disabled worldTransform+velocityPreserved=true renderableOffset=position+rotation=true undoRedo=true advancedToggle=true screenshot={modelOffsetScreenshot} actorListToggle=true actorListScreenshot={actorListScreenshot} cameraFollow=F+button=true actorTracking=true cameraFollowScreenshot={cameraFollowScreenshot} reset=CtrlR+undo-redo=true save=CtrlS+CmdS+persistence=true delete=Delete+Backspace+undoRedo=true multiCopyRoots=true preview=true ctrlC=true snap=true descendants=true repeat=true persistence=true marqueeScreenshot={marqueeScreenshot} copyScreenshot={screenshot}");

        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_COPY_SMOKE_FAILED: {exception}");
        }
        finally
        {
            SetAutoSetSpawnOnMovePreference(originalAutoSetSpawnOnMove);
            SetAutoSetSpawnOnRotatePreference(originalAutoSetSpawnOnRotate);
            SetParentOnResetPreference(originalParentOnReset);
            CancelCopyPlacement(showStatus: false);
            if (originalProjectPath is not null)
            {
                LoadProject(originalProjectPath);
                await WaitForUiFrames(2);
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }

        GetTree().Quit(exitCode);
    }

    private void RunAutoSetSpawnSmoke(WorldActorNode source, WorldActorNode child)
    {
        bool originalAutoSetSpawnOnMove = _autoSetSpawnOnMove;
        bool originalAutoSetSpawnOnRotate = _autoSetSpawnOnRotate;
        Vector3 originalSourceSpawnPosition = source.State.SpawnPosition;
        Quaternion originalSourceSpawnRotation = source.State.SpawnRotation;
        Vector3 originalChildSpawnPosition = child.State.SpawnPosition;
        Quaternion originalChildSpawnRotation = child.State.SpawnRotation;
        Transform3D originalSourceTransform = source.GlobalTransform;
        Transform3D originalChildTransform = child.GlobalTransform;

        SetActorSelection(new[] { source }, source);
        SetAutoSetSpawnOnMovePreference(true);
        SetAutoSetSpawnOnRotatePreference(true);
        if (!_autoSetSpawnOnMoveToggle.ButtonPressed || !_autoSetSpawnOnRotateToggle.ButtonPressed)
            throw new InvalidOperationException("The automatic restart-transform options did not enable.");

        float moveTarget = source.GlobalPosition.X + 20f;
        if (moveTarget > _positionFields[0].MaxValue)
            moveTarget = source.GlobalPosition.X - 20f;
        if (moveTarget < _positionFields[0].MinValue || moveTarget > _positionFields[0].MaxValue)
            throw new InvalidOperationException("The movement smoke actor is outside the inspector's coordinate range.");
        CommitNumberFieldInput(_positionFields[0], moveTarget);
        if (source.GlobalTransform.IsEqualApprox(originalSourceTransform))
            throw new InvalidOperationException("The move inspector did not change the actor transform.");
        AssertSpawnTransformMatchesCurrent(source, "moving with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "moving an actor family with auto-set enabled");
        UndoEdit();
        if (!source.GlobalTransform.IsEqualApprox(originalSourceTransform)
            || !child.GlobalTransform.IsEqualApprox(originalChildTransform))
            throw new InvalidOperationException("Undo did not restore the actor family transforms after the move.");
        AssertSpawnTransformMatchesCurrent(source, "undoing a move with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "undoing a family move with auto-set enabled");
        RedoEdit();
        AssertSpawnTransformMatchesCurrent(source, "redoing a move with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "redoing a family move with auto-set enabled");
        UndoEdit();
        AssertSpawnTransformMatchesCurrent(source, "restoring a move with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "restoring a family move with auto-set enabled");

        SetAutoSetSpawnOnMovePreference(false);
        float moveWithoutAutoSetTarget = source.GlobalPosition.X + 30f;
        if (moveWithoutAutoSetTarget > _positionFields[0].MaxValue)
            moveWithoutAutoSetTarget = source.GlobalPosition.X - 30f;
        if (moveWithoutAutoSetTarget < _positionFields[0].MinValue
            || moveWithoutAutoSetTarget > _positionFields[0].MaxValue)
            throw new InvalidOperationException("The auto-set-disabled move fixture is outside the inspector's coordinate range.");
        Vector3 sourceSpawnWithoutMoveAutoSet = source.State.SpawnPosition;
        Quaternion sourceRotationWithoutMoveAutoSet = source.State.SpawnRotation;
        Vector3 childSpawnWithoutMoveAutoSet = child.State.SpawnPosition;
        Quaternion childRotationWithoutMoveAutoSet = child.State.SpawnRotation;
        CommitNumberFieldInput(_positionFields[0], moveWithoutAutoSetTarget);
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutMoveAutoSet, sourceRotationWithoutMoveAutoSet, "moving with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutMoveAutoSet, childRotationWithoutMoveAutoSet, "moving a family with auto-set disabled");
        UndoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutMoveAutoSet, sourceRotationWithoutMoveAutoSet, "undoing a move with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutMoveAutoSet, childRotationWithoutMoveAutoSet, "undoing a family move with auto-set disabled");
        RedoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutMoveAutoSet, sourceRotationWithoutMoveAutoSet, "redoing a move with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutMoveAutoSet, childRotationWithoutMoveAutoSet, "redoing a family move with auto-set disabled");
        UndoEdit();

        float rotationTarget = source.GlobalRotationDegrees.Y + 30f;
        if (rotationTarget > _rotationFields[1].MaxValue)
            rotationTarget = source.GlobalRotationDegrees.Y - 30f;
        if (rotationTarget < _rotationFields[1].MinValue || rotationTarget > _rotationFields[1].MaxValue)
            throw new InvalidOperationException("The rotation smoke actor is outside the inspector's coordinate range.");
        Transform3D beforeRotation = source.GlobalTransform;
        CommitNumberFieldInput(_rotationFields[1], rotationTarget);
        if (source.GlobalTransform.IsEqualApprox(beforeRotation))
            throw new InvalidOperationException("The rotation inspector did not change the actor transform.");
        AssertSpawnTransformMatchesCurrent(source, "rotating with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "rotating an actor family with auto-set enabled");
        UndoEdit();
        AssertSpawnTransformMatchesCurrent(source, "undoing a rotation with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "undoing a family rotation with auto-set enabled");
        RedoEdit();
        AssertSpawnTransformMatchesCurrent(source, "redoing a rotation with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "redoing a family rotation with auto-set enabled");
        UndoEdit();

        Button quickRotateButton = FindChild("QuickRotateActorsButton", true, false) as Button
            ?? throw new InvalidOperationException("The quick-rotate toolbar button was not found.");
        Transform3D beforeQuickRotation = source.GlobalTransform;
        quickRotateButton.EmitSignal("pressed");
        if (source.GlobalTransform.IsEqualApprox(beforeQuickRotation))
            throw new InvalidOperationException("The quick-rotate action did not change the actor transform.");
        AssertSpawnTransformMatchesCurrent(source, "quick rotation with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "quickly rotating an actor family with auto-set enabled");
        UndoEdit();
        AssertSpawnTransformMatchesCurrent(source, "undoing quick rotation with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "undoing a quick family rotation with auto-set enabled");
        RedoEdit();
        AssertSpawnTransformMatchesCurrent(source, "redoing quick rotation with auto-set enabled");
        AssertSpawnTransformMatchesCurrent(child, "redoing a quick family rotation with auto-set enabled");
        UndoEdit();

        SetAutoSetSpawnOnRotatePreference(false);
        var persistedAutoSetSettings = new ConfigFile();
        if (persistedAutoSetSettings.Load(EditorSettingsPath) != Error.Ok
            || persistedAutoSetSettings.GetValue("editor", "auto_set_spawn_on_move", true).AsBool()
            || persistedAutoSetSettings.GetValue("editor", "auto_set_spawn_on_rotate", true).AsBool())
            throw new InvalidOperationException("Disabling automatic restart transforms did not persist both editor preferences.");

        Vector3 sourceSpawnWithoutRotateAutoSet = source.State.SpawnPosition;
        Quaternion sourceRotationWithoutRotateAutoSet = source.State.SpawnRotation;
        Vector3 childSpawnWithoutRotateAutoSet = child.State.SpawnPosition;
        Quaternion childRotationWithoutRotateAutoSet = child.State.SpawnRotation;
        beforeRotation = source.GlobalTransform;
        CommitNumberFieldInput(_rotationFields[1], rotationTarget);
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "rotating with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "rotating a family with auto-set disabled");
        UndoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "undoing a rotation with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "undoing a family rotation with auto-set disabled");
        RedoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "redoing a rotation with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "redoing a family rotation with auto-set disabled");
        UndoEdit();

        beforeQuickRotation = source.GlobalTransform;
        quickRotateButton.EmitSignal("pressed");
        if (source.GlobalTransform.IsEqualApprox(beforeQuickRotation))
            throw new InvalidOperationException("The quick-rotate action did not change the actor transform while auto-set was disabled.");
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "quick rotation with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "quick family rotation with auto-set disabled");
        UndoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "undoing quick rotation with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "undoing quick family rotation with auto-set disabled");
        RedoEdit();
        AssertSpawnTransformUnchanged(source, sourceSpawnWithoutRotateAutoSet, sourceRotationWithoutRotateAutoSet, "redoing quick rotation with auto-set disabled");
        AssertSpawnTransformUnchanged(child, childSpawnWithoutRotateAutoSet, childRotationWithoutRotateAutoSet, "redoing quick family rotation with auto-set disabled");
        UndoEdit();

        float scaleTarget = source.Scale.X + 0.5f;
        CommitNumberFieldInput(_scaleFields[0], scaleTarget);
        AssertSpawnTransformMatchesCurrent(source, "scaling with automatic family restart updates");
        AssertSpawnTransformMatchesCurrent(child, "scaling an actor family with automatic restart updates");
        UndoEdit();
        AssertSpawnTransformMatchesCurrent(source, "undoing scale");
        AssertSpawnTransformMatchesCurrent(child, "undoing family scale");
        RedoEdit();
        AssertSpawnTransformMatchesCurrent(source, "redoing scale");
        AssertSpawnTransformMatchesCurrent(child, "redoing family scale");
        UndoEdit();
        if (!source.GlobalTransform.IsEqualApprox(originalSourceTransform)
            || !child.GlobalTransform.IsEqualApprox(originalChildTransform))
            throw new InvalidOperationException("The auto-set smoke did not restore the actor family transforms.");

        source.State.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(originalSourceSpawnPosition));
        source.State.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(originalSourceSpawnRotation));
        child.State.WriteLegacyVector("spawnPosition", CoordinateSpace.ToLegacy(originalChildSpawnPosition));
        child.State.WriteLegacyQuaternion("spawnRotation", CoordinateSpace.ToLegacy(originalChildSpawnRotation));
        SetActorSelection(new[] { source }, source);
        GD.Print("AUTOSET_SPAWN_SMOKE_OK move=enabled+disabled family=source+child undoRedo=true rotate=enabled+disabled quickRotate=enabled+disabled scale=family preferencePersistence=true");

        SetAutoSetSpawnOnMovePreference(originalAutoSetSpawnOnMove);
        SetAutoSetSpawnOnRotatePreference(originalAutoSetSpawnOnRotate);
        ClearHistory();
    }

    private static void CommitNumberFieldInput(SpinBox field, double value)
    {
        LineEdit lineEdit = field.GetLineEdit();
        lineEdit.Text = value.ToString("R", CultureInfo.InvariantCulture);
        field.Apply();
    }

    private static void AssertSpawnTransformMatchesCurrent(WorldActorNode actor, string operation)
    {
        if (!actor.State.SpawnPosition.IsEqualApprox(actor.GlobalPosition)
            || !actor.State.SpawnRotation.IsEqualApprox(actor.GlobalBasis.GetRotationQuaternion()))
            throw new InvalidOperationException($"The restart transform for '{actor.State.DisplayName}' did not match its current transform after {operation}.");
    }

    private static void AssertSpawnTransformUnchanged(
        WorldActorNode actor,
        Vector3 expectedPosition,
        Quaternion expectedRotation,
        string operation)
    {
        if (!actor.State.SpawnPosition.IsEqualApprox(expectedPosition)
            || !actor.State.SpawnRotation.IsEqualApprox(expectedRotation))
            throw new InvalidOperationException($"The restart transform for '{actor.State.DisplayName}' changed during {operation}.");
    }

    private void ClickActorForSmoke(WorldActorNode actor, bool addToSelection = false, bool toggleSelection = false)
    {
        Vector2 screenPosition = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
        if (!GetViewport().GetVisibleRect().HasPoint(screenPosition)
            || !SelectActorAt(screenPosition, addToSelection, toggleSelection))
            throw new InvalidOperationException($"World click did not select actor '{actor.State.DisplayName}'.");
    }

    private void SelectActorInMarqueeForSmoke(WorldActorNode actor, bool addToSelection = false, bool toggleSelection = false)
    {
        Vector2 center = _camera.UnprojectPosition(actor.GetWorldRenderBounds().GetCenter());
        BeginSelectionMarquee(center - new Vector2(5f, 5f), addToSelection, toggleSelection);
        UpdateSelectionMarquee(center + new Vector2(5f, 5f));
        if (!_selectionMarquee.Visible)
            throw new InvalidOperationException("The selection marquee was not drawn during a drag.");
        CompleteSelectionMarquee(center + new Vector2(5f, 5f));
    }

    private static void AssertQuickRotationForSmoke(
        WorldActorNode actor,
        ActorMotionSnapshot before,
        Vector3 pivot,
        Quaternion rotationDelta,
        bool rotated)
    {
        Vector3 expectedPosition = rotated
            ? pivot + rotationDelta * (before.Transform.Origin - pivot)
            : before.Transform.Origin;
        Quaternion expectedRotation = rotated
            ? before.Transform.Basis.GetRotationQuaternion() * rotationDelta
            : before.Transform.Basis.GetRotationQuaternion();
        Vector3 expectedAngularVelocity = rotated ? Vector3.Zero : before.AngularVelocity;
        if (actor.GlobalPosition.DistanceTo(expectedPosition) > 0.01f
            || !actor.GlobalBasis.GetRotationQuaternion().IsEqualApprox(expectedRotation)
            || !actor.GlobalBasis.Scale.IsEqualApprox(before.Transform.Basis.Scale)
            || actor.LinearVelocity.DistanceTo(before.Velocity) > 0.01f
            || actor.AngularVelocity.DistanceTo(expectedAngularVelocity) > 0.01f)
            throw new InvalidOperationException($"Quick rotate produced the wrong transform or velocity for '{actor.State.DisplayName}'.");
    }

    private void AssertActorSelectionForSmoke(params WorldActorNode[] expectedActors)
    {
        if (_selectedActors.Count != expectedActors.Length
            || expectedActors.Any(actor => !_selectedActors.Contains(actor))
            || _actorList.GetSelectedItems().Length + _offstageActorList.GetSelectedItems().Length != expectedActors.Length)
            throw new InvalidOperationException("The actor world selection and actor-list multi-selection diverged.");
    }

    private List<WorldActorNode> FindCopiesSince(HashSet<string> originalActorNames)
    {
        return _actors.Where(actor => !originalActorNames.Contains(actor.LegacyName)).ToList();
    }

    private static void AssertCopyTransform(WorldActorNode source, WorldActorNode copy, Vector3 expectedOffset)
    {
        if (copy.GlobalPosition.DistanceTo(source.GlobalPosition + expectedOffset) > 0.01f
            || !copy.GlobalBasis.GetRotationQuaternion().IsEqualApprox(source.GlobalBasis.GetRotationQuaternion())
            || !copy.GlobalBasis.Scale.IsEqualApprox(source.GlobalBasis.Scale))
            throw new InvalidOperationException($"Copy '{copy.State.DisplayName}' did not preserve the source transform and placement offset.");
    }

    private string GetCopyDisplayNameForSmoke(string rootName, int expectedOrdinal)
    {
        WorldActorNode? root = _actors.FirstOrDefault(actor => actor.LegacyName == rootName);
        return $"{root?.State.DisplayName ?? rootName}-{expectedOrdinal}";
    }
}
