using System;
using System.Collections.Generic;
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
            SelectActor(source);
            await WaitForUiFrames(2);
            _cameraTarget = (source.GlobalPosition + independent.GlobalPosition) * 0.5f;
            _cameraDistance = 18f;
            UpdateOrbitCamera();
            await WaitForUiFrames(2);

            ClickActorForSmoke(source);
            AssertActorSelectionForSmoke(source);
            ClickActorForSmoke(independent, addToSelection: true);
            AssertActorSelectionForSmoke(source, independent);
            if (_selectedActor != independent || _actorList.GetSelectedItems().Length != 2)
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

            SetActorSelection(new[] { independent }, independent);
            string independentOriginalParent = independent.State.TransformParent;
            string independentOriginalSpawnParent = LegacySaveDocument.ReadString(independent.State.Data["spawnTransformParent"]);
            Node independentOriginalSceneParent = independent.GetParent();
            Vector2 cycleTargetPosition = _camera.UnprojectPosition(source.GetWorldRenderBounds().GetCenter());
            Input.WarpMouse(cycleTargetPosition);
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) != source)
                throw new InvalidOperationException("The cycle-rejection smoke test could not resolve the descendant actor.");
            _UnhandledInput(new InputEventKey { Keycode = Key.H, CtrlPressed = true, Pressed = true });
            if (independent.State.TransformParent != independentOriginalParent
                || LegacySaveDocument.ReadString(independent.State.Data["spawnTransformParent"]) != independentOriginalSpawnParent
                || independent.GetParent() != independentOriginalSceneParent)
                throw new InvalidOperationException("Parenting an actor to its descendant created an invalid hierarchy.");

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
                    if (GetActorAtScreenPosition(candidate) is not null)
                        continue;
                    emptyPosition = candidate;
                    foundEmptyPosition = true;
                    break;
                }
            }
            if (!foundEmptyPosition)
                throw new InvalidOperationException("The parent smoke test could not find empty world space.");
            Input.WarpMouse(emptyPosition);
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) is not null)
                throw new InvalidOperationException("The mouse did not remain over empty world space for unparenting.");
            var sourceBeforeUnparent = new ActorMotionSnapshot(source.GlobalTransform, source.LinearVelocity, source.AngularVelocity);
            _UnhandledInput(new InputEventKey { Keycode = Key.H, CtrlPressed = true, Pressed = true });
            if (source.State.TransformParent.Length != 0
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]).Length != 0
                || source.GetParent() != _world
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeUnparent.Transform))
                throw new InvalidOperationException("Ctrl+H over empty world space did not unparent the selected actor.");
            UndoEdit();
            if (source.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != independentName
                || source.GetParent() != independent
                || !source.GlobalTransform.IsEqualApprox(sourceBeforeUnparent.Transform))
                throw new InvalidOperationException("Undo did not restore the actor parent after unparenting.");
            RedoEdit();
            if (source.State.TransformParent.Length != 0 || source.GetParent() != _world)
                throw new InvalidOperationException("Redo did not restore the unparented actor state.");
            UndoEdit();
            UndoEdit();
            if (source.State.TransformParent != originalTransformParent
                || LegacySaveDocument.ReadString(source.State.Data["spawnTransformParent"]) != originalSpawnTransformParent
                || source.GetParent() != originalSceneParent)
                throw new InvalidOperationException("Undo did not return the actor to its original hierarchy.");

            SetActorSelection(new[] { source, independent }, source);
            await WaitForUiFrames(1);


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
            Input.WarpMouse(_camera.UnprojectPosition(persistenceParent.GetWorldRenderBounds().GetCenter()));
            await WaitForUiFrames(1);
            if (GetActorAtScreenPosition(GetViewport().GetMousePosition()) != persistenceParent)
                throw new InvalidOperationException("The parent persistence smoke test could not resolve its target actor.");
            _UnhandledInput(new InputEventKey { Keycode = Key.H, CtrlPressed = true, Pressed = true });
            if (persistenceSource.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(persistenceSource.State.Data["spawnTransformParent"]) != independentName)
                throw new InvalidOperationException("The parent shortcut did not update the project state before save.");
            _document.SaveTo(projectPath);
            LoadProject(projectPath);
            await WaitForUiFrames(2);
            if (_document?.SourcePath != projectPath || _document.Actors.Count != originalCount + 4)
                throw new InvalidOperationException("Copied actor hierarchies did not persist after saving and reloading the project.");
            if (!_actors.Any(actor => LegacySaveDocument.ReadString(actor.State.Data["cloneParent"]) == sourceName))
                throw new InvalidOperationException("Saved copy lineage was not retained in the .voos project.");
            WorldActorNode persistedSource = _actors.Single(actor => actor.LegacyName == sourceName);
            WorldActorNode persistedParent = _actors.Single(actor => actor.LegacyName == independentName);
            if (persistedSource.State.TransformParent != independentName
                || LegacySaveDocument.ReadString(persistedSource.State.Data["spawnTransformParent"]) != independentName
                || persistedSource.GetParent() != persistedParent)
                throw new InvalidOperationException("The actor parent and reset parent did not persist through save and reload.");

            GD.Print($"ACTOR_COPY_SMOKE_OK selection=shift-toggle-marquee=true marqueeVisual=true quickRotate=R+toolbar=true groupPivot=true undo-redo=true parenting=CtrlH=true transformPreserved=true cycleRejected=true unparent=emptySpace=true undo-redo=true parentPersistence=true multiCopyRoots=true multiDelete=undo-redo=true preview=true ctrlC=true snap=true descendants=true repeat=true persistence=true marqueeScreenshot={marqueeScreenshot} copyScreenshot={screenshot}");

        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"ACTOR_COPY_SMOKE_FAILED: {exception}");
        }
        finally
        {
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
            || _actorList.GetSelectedItems().Length != expectedActors.Length)
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
